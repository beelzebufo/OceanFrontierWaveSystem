using System;
using System.Collections.Generic;
using System.Threading;
using Godot;

namespace OceanFrontier.Water.Queries;

/// <summary>
/// Batched, multi-owner point queries against the final AnimatedWaveField.
/// GPU work and callbacks run on the render thread. Public methods are thread-safe.
/// </summary>
public sealed class OceanPointQueryService
{
	public const int Capacity = 256;

	// Crest QueryBase allows up to 7 asynchronous GPU readback requests.
	// Four was historically sufficient there, but some Linux setups
	// required seven. Keep the same conservative in-flight depth here.
	//
	// Each slot owns persistent 256 * 16-byte input/output buffers,
	// so seven slots still have negligible memory cost.
	private const int SlotCount = 7;
	private const int StrideBytes = 4 * sizeof(float);
	private const int WorkgroupSize = 64;
	private const int PushConstantBytes = 32;

	/// <summary>A stable registration for one independent query consumer.</summary>
	public sealed class OwnerHandle
	{
		internal OwnerHandle(OceanPointQueryService service, long id, int capacity)
		{
			Service = service;
			Id = id;
			Capacity = capacity;
		}

		internal OceanPointQueryService Service { get; }
		public long Id { get; }
		public int Capacity { get; }
	}

	private sealed class OwnerState
	{
		public OwnerState(OwnerHandle handle)
		{
			Handle = handle;
			PendingPositions = new Vector2[handle.Capacity];
			LatestResults = new Vector4[handle.Capacity];
			LatestVelocities = new Vector4[handle.Capacity];
		}

		public OwnerHandle Handle { get; }
		public Vector2[] PendingPositions { get; }
		public Vector4[] LatestResults { get; }
		public Vector4[] LatestVelocities { get; }
		public int PendingCount;
		public float PendingMinGridSize;
		public long NextGeneration;
		public long PendingGeneration;
		public long DispatchedGeneration;
		public int LatestCount;
		public long LatestGeneration;
		public long LatestFrame;
		public int LatestReadbackFrames = -1;
		public double LatestSampleTime = double.NaN;
		public long LatestVelocityEpoch;
		public bool HasLatest;
		public bool HasLatestVelocity;
	}

	private struct Segment
	{
		public long OwnerId;
		public int Offset;
		public int Count;
		public long Generation;
	}

	private sealed class Slot
	{
		public Rid Input;
		public Rid Output;
		public Rid UniformSet;
		public Callable Callback;
		public readonly byte[] Upload = new byte[Capacity * StrideBytes];
		public readonly Segment[] Segments = new Segment[Capacity];
		public bool InFlight;
		public int Count;
		public int SegmentCount;
		public long SubmittedFrame;
		public double SampleTime = double.NaN;
		public long VelocityEpoch;
	}

	private readonly object _sync = new();
	private readonly Dictionary<long, OwnerState> _owners = new();
	private readonly Slot[] _slots = new Slot[SlotCount];
	private readonly byte[] _pushBytes = new byte[PushConstantBytes];

	private RenderingDevice _rd;
	private Rid _shader;
	private Rid _pipeline;
	private Rid _sampler;
	private int _lodCount;
	private int _reservedCapacity;
	private long _nextOwnerId;
	private long _renderFrame;
	private long _velocityEpoch;
	private int _submittedCount;
	private int _latestReadbackFrames = -1;
	private bool _hasCompletedResult;

	/// <summary>
	/// Reserves part of the shared 256-point capacity for a consumer.
	/// Registration creates CPU arrays only; all GPU resources remain shared.
	/// </summary>
	public OwnerHandle RegisterOwner(int capacity)
	{
		if (capacity is < 1 or > Capacity)
		{
			throw new ArgumentOutOfRangeException(nameof(capacity));
		}

		lock (_sync)
		{
			if (_reservedCapacity + capacity > Capacity)
			{
				throw new InvalidOperationException(
					$"Point query owner capacity exceeds the shared limit of {Capacity}.");
			}

			long id = ++_nextOwnerId;
			var handle = new OwnerHandle(this, id, capacity);
			_owners.Add(id, new OwnerState(handle));
			_reservedCapacity += capacity;
			return handle;
		}
	}

	/// <summary>Removes an owner. In-flight results for it are discarded.</summary>
	public bool UnregisterOwner(OwnerHandle owner)
	{
		if (owner == null || !ReferenceEquals(owner.Service, this))
		{
			return false;
		}

		lock (_sync)
		{
			if (!_owners.Remove(owner.Id, out OwnerState state))
			{
				return false;
			}

			_reservedCapacity -= state.Handle.Capacity;
			return true;
		}
	}

	/// <summary>
	/// Replaces this owner's undispatched batch and returns its generation.
	///
	/// minGridSize follows the Crest collision-query contract:
	///
	///     minWavelength = MinSpatialLength / 2
	///     minGridSize   = minWavelength / 2
	///                   = MinSpatialLength / 4
	///
	/// The value is stored once per owner submission and written into
	/// every 16-byte GPU query element.
	/// </summary>
	public long SubmitBatch(
		OwnerHandle owner,
		ReadOnlySpan<Vector2> worldXZ,
		float minGridSize = 0.0f)
	{
		if (!float.IsFinite(minGridSize) ||
			minGridSize < 0.0f)
		{
			throw new ArgumentOutOfRangeException(
				nameof(minGridSize));
		}

		foreach (Vector2 position in worldXZ)
		{
			if (!float.IsFinite(position.X) ||
				!float.IsFinite(position.Y))
			{
				throw new ArgumentException(
					"Query positions must be finite.",
					nameof(worldXZ));
			}
		}

		lock (_sync)
		{
			OwnerState state =
				GetOwner(owner);

			if (worldXZ.Length is < 1 ||
				worldXZ.Length > state.Handle.Capacity)
			{
				throw new ArgumentOutOfRangeException(
					nameof(worldXZ));
			}

			worldXZ.CopyTo(
				state.PendingPositions);

			state.PendingCount =
				worldXZ.Length;

			state.PendingMinGridSize =
				minGridSize;

			state.PendingGeneration =
				++state.NextGeneration;

			return
				state.PendingGeneration;
		}
	}

	/// <summary>Copies only this owner's latest completed result without waiting.</summary>
	public bool TryCopyLatest(
		OwnerHandle owner,
		Span<Vector4> destination,
		out int count,
		out long generation,
		out long submittedFrame,
		out int readbackFrames)
	{
		return TryCopyLatest(
			owner,
			destination,
			out count,
			out generation,
			out submittedFrame,
			out readbackFrames,
			out _);
	}


	/// <summary>
	/// Copies only this owner's latest completed result and its AnimatedWaveField
	/// simulation timestamp without waiting.
	/// </summary>
	public bool TryCopyLatest(
		OwnerHandle owner,
		Span<Vector4> destination,
		out int count,
		out long generation,
		out long submittedFrame,
		out int readbackFrames,
		out double sampleTime)
	{
		lock (_sync)
		{
			OwnerState state = GetOwner(owner);
			count = state.LatestCount;
			generation = state.LatestGeneration;
			submittedFrame = state.LatestFrame;
			readbackFrames = state.LatestReadbackFrames;
			sampleTime = state.LatestSampleTime;

			if (!state.HasLatest)
			{
				return false;
			}

			if (destination.Length < count)
			{
				throw new ArgumentException(
					"Destination is smaller than the completed owner batch.",
					nameof(destination));
			}

			state.LatestResults.AsSpan(0, count).CopyTo(destination);
			return true;
		}
	}


	/// <summary>
	/// Copies this owner's latest completed water-surface velocity estimate.
	///
	/// Velocity follows Crest QueryBase.CalculateVelocities():
	///
	///     velocity = (current displacement - previous displacement) / dt
	///
	/// It is available only when two consecutive completed result generations
	/// with matching point counts and valid simulation timestamps exist.
	/// </summary>
	public bool TryCopyLatestVelocities(
		OwnerHandle owner,
		Span<Vector4> destination,
		out int count,
		out long generation)
	{
		lock (_sync)
		{
			OwnerState state =
				GetOwner(owner);

			count =
				state.LatestCount;

			generation =
				state.LatestGeneration;

			if (!state.HasLatestVelocity)
			{
				return false;
			}

			if (destination.Length < count)
			{
				throw new ArgumentException(
					"Destination is smaller than the completed velocity batch.",
					nameof(destination));
			}

			state.LatestVelocities
				.AsSpan(0, count)
				.CopyTo(destination);

			return true;
		}
	}


	/// <summary>
	/// Invalidates finite-difference velocity history without discarding
	/// the latest displacement result.
	///
	/// Use this when the wave field changes discontinuously, for example
	/// after regenerating H0 from new spectrum settings.
	/// </summary>
	internal void InvalidateVelocities()
	{
		lock (_sync)
		{
			_velocityEpoch++;

			foreach (OwnerState owner in _owners.Values)
			{
				owner.HasLatestVelocity =
					false;
			}
		}
	}


	public void GetDiagnostics(
		out int submittedCount,
		out int readbackFrames,
		out bool hasCompletedResult)
	{
		lock (_sync)
		{
			submittedCount = _submittedCount;
			readbackFrames = _latestReadbackFrames;
			hasCompletedResult = _hasCompletedResult;
		}
	}

	private OwnerState GetOwner(OwnerHandle owner)
	{
		if (owner == null ||
			!ReferenceEquals(owner.Service, this) ||
			!_owners.TryGetValue(owner.Id, out OwnerState state))
		{
			throw new InvalidOperationException("Point query owner is not registered.");
		}

		return state;
	}

	/// <summary>Render thread only. Borrowed composer resources must outlive this service.</summary>
	internal void Initialize(RenderingDevice rd, Rid field, Rid lodBuffer, int lodCount)
	{
		if (rd == null || !field.IsValid || !lodBuffer.IsValid || lodCount < 1)
		{
			throw new ArgumentException("Point query GPU inputs are invalid.");
		}

		Release();
		_rd = rd;
		_lodCount = lodCount;

		try
		{
			RDShaderFile file = GD.Load<RDShaderFile>(
				"res://Ocean/Shaders/Waves/animated_wave_point_query.glsl");
			if (file == null)
			{
				throw new InvalidOperationException("Point query shader was not loaded.");
			}

			RDShaderSpirV spirv = file.GetSpirV();
			string error = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
			if (!string.IsNullOrEmpty(error))
			{
				throw new InvalidOperationException(
					$"Point query shader compilation failed:\n{error}");
			}

			if (spirv.GetStageBytecode(RenderingDevice.ShaderStage.Compute).Length == 0)
			{
				throw new InvalidOperationException(
					"Point query shader has no compute bytecode. Reimport it in Godot.");
			}

			_shader = rd.ShaderCreateFromSpirV(spirv);
			if (!_shader.IsValid)
			{
				throw new InvalidOperationException("Point query shader creation failed.");
			}

			_pipeline = rd.ComputePipelineCreate(_shader);
			if (!_pipeline.IsValid)
			{
				throw new InvalidOperationException("Point query pipeline creation failed.");
			}

			_sampler = rd.SamplerCreate(new RDSamplerState
			{
				MinFilter = RenderingDevice.SamplerFilter.Linear,
				MagFilter = RenderingDevice.SamplerFilter.Linear,
				MipFilter = RenderingDevice.SamplerFilter.Nearest,
				RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge,
				RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge,
				RepeatW = RenderingDevice.SamplerRepeatMode.ClampToEdge,
			});
			if (!_sampler.IsValid)
			{
				throw new InvalidOperationException("Point query sampler creation failed.");
			}

			for (int i = 0; i < SlotCount; i++)
			{
				var slot = new Slot();
				_slots[i] = slot;
				slot.Input = rd.StorageBufferCreate(Capacity * StrideBytes, slot.Upload);
				slot.Output = rd.StorageBufferCreate(
					Capacity * StrideBytes,
					new byte[Capacity * StrideBytes]);
				if (!slot.Input.IsValid || !slot.Output.IsValid)
				{
					throw new InvalidOperationException("Point query buffer creation failed.");
				}

				var sampledField = Uniform(
					RenderingDevice.UniformType.SamplerWithTexture, 0, _sampler, field);
				var lodData = Uniform(
					RenderingDevice.UniformType.StorageBuffer, 1, lodBuffer);
				var queryData = Uniform(
					RenderingDevice.UniformType.StorageBuffer, 2, slot.Input);
				var resultData = Uniform(
					RenderingDevice.UniformType.StorageBuffer, 3, slot.Output);

				slot.UniformSet = rd.UniformSetCreate(
					new Godot.Collections.Array<RDUniform>
					{
						sampledField,
						lodData,
						queryData,
						resultData,
					},
					_shader,
					0);
				if (!slot.UniformSet.IsValid)
				{
					throw new InvalidOperationException("Point query uniform set creation failed.");
				}

				slot.Callback = Callable.From<byte[]>(data => OnReadback(slot, data));
			}

			GD.Print(
				$"[Ocean Query] Ready: {Capacity} shared points, " +
				$"{SlotCount} persistent slots.");
		}
		catch
		{
			Release();
			throw;
		}
	}

	private static RDUniform Uniform(
		RenderingDevice.UniformType type,
		int binding,
		params Rid[] ids)
	{
		var uniform = new RDUniform { UniformType = type, Binding = binding };
		foreach (Rid id in ids)
		{
			uniform.AddId(id);
		}

		return uniform;
	}

	/// <summary>
	/// Render thread only. Flattens every owner's newest undispatched batch into
	/// exactly one compute dispatch after AnimatedWaveComposer.ComposeFft().
	/// </summary>
	internal void DispatchAfterCompose(
		Vector2 focusXZ,
		float lodScaleAlpha)
	{
		DispatchAfterCompose(
			focusXZ,
			lodScaleAlpha,
			double.NaN);
	}


	/// <summary>
	/// Render thread only.
	///
	/// sampleTime must describe the AnimatedWaveField generation being queried.
	/// OceanRuntime supplies simulation time so pause and time scaling preserve
	/// the same temporal contract as the wave field itself.
	/// </summary>
	internal void DispatchAfterCompose(
		Vector2 focusXZ,
		float lodScaleAlpha,
		double sampleTime)
	{
		if (_rd == null)
		{
			return;
		}

		if (!float.IsFinite(lodScaleAlpha))
		{
			throw new ArgumentOutOfRangeException(nameof(lodScaleAlpha));
		}

		lodScaleAlpha = Mathf.Clamp(lodScaleAlpha, 0.0f, 1.0f);
		long frame = Interlocked.Increment(ref _renderFrame);
		Slot slot = null;

		lock (_sync)
		{
			bool hasPending = false;
			foreach (OwnerState owner in _owners.Values)
			{
				if (owner.PendingGeneration != owner.DispatchedGeneration)
				{
					hasPending = true;
					break;
				}
			}

			if (!hasPending)
			{
				return;
			}

			foreach (Slot candidate in _slots)
			{
				if (candidate != null && !candidate.InFlight)
				{
					slot = candidate;
					break;
				}
			}

			if (slot == null)
			{
				return;
			}

			slot.Count = 0;
			slot.SegmentCount = 0;
			slot.SubmittedFrame = frame;
			slot.SampleTime = sampleTime;
			slot.VelocityEpoch = _velocityEpoch;

			foreach (OwnerState owner in _owners.Values)
			{
				if (owner.PendingGeneration == owner.DispatchedGeneration)
				{
					continue;
				}

				int offset = slot.Count;
				slot.Segments[slot.SegmentCount++] = new Segment
				{
					OwnerId = owner.Handle.Id,
					Offset = offset,
					Count = owner.PendingCount,
					Generation = owner.PendingGeneration,
				};

				for (int i = 0; i < owner.PendingCount; i++)
				{
					WriteQuery(
						slot.Upload,
						offset + i,
						owner.PendingPositions[i],
						owner.PendingMinGridSize);
				}

				slot.Count += owner.PendingCount;
			}

			slot.InFlight = true;
		}

		try
		{
			uint bytes = (uint)(slot.Count * StrideBytes);
			Error uploadError = _rd.BufferUpdate(
				slot.Input, 0, bytes, slot.Upload.AsSpan(0, (int)bytes));
			if (uploadError != Error.Ok)
			{
				throw new InvalidOperationException($"Point query upload failed: {uploadError}.");
			}

			WritePushConstants(slot.Count, focusXZ, lodScaleAlpha);
			long list = _rd.ComputeListBegin();
			_rd.ComputeListBindComputePipeline(list, _pipeline);
			_rd.ComputeListBindUniformSet(list, slot.UniformSet, 0);
			_rd.ComputeListSetPushConstant(list, _pushBytes, (uint)_pushBytes.Length);
			_rd.ComputeListDispatch(
				list,
				(uint)((slot.Count + WorkgroupSize - 1) / WorkgroupSize),
				1,
				1);
			_rd.ComputeListEnd();

			Error readbackError = _rd.BufferGetDataAsync(
				slot.Output, slot.Callback, 0, bytes);
			if (readbackError != Error.Ok)
			{
				throw new InvalidOperationException(
					$"Point query async readback failed: {readbackError}.");
			}

			lock (_sync)
			{
				for (int i = 0; i < slot.SegmentCount; i++)
				{
					Segment segment = slot.Segments[i];
					if (_owners.TryGetValue(segment.OwnerId, out OwnerState owner))
					{
						owner.DispatchedGeneration = Math.Max(
							owner.DispatchedGeneration,
							segment.Generation);
					}
				}

				_submittedCount = slot.Count;
			}
		}
		catch
		{
			lock (_sync)
			{
				slot.InFlight = false;
			}

			throw;
		}
	}

	private static void WriteQuery(
		byte[] upload,
		int index,
		Vector2 position,
		float minGridSize)
	{
		int offset =
			index *
			StrideBytes;

		BitConverter.TryWriteBytes(
			upload.AsSpan(offset, 4),
			position.X);

		BitConverter.TryWriteBytes(
			upload.AsSpan(offset + 4, 4),
			position.Y);

		BitConverter.TryWriteBytes(
			upload.AsSpan(offset + 8, 4),
			minGridSize);

		BitConverter.TryWriteBytes(
			upload.AsSpan(offset + 12, 4),
			0.0f);
	}

	private void WritePushConstants(int count, Vector2 focusXZ, float lodScaleAlpha)
	{
		BitConverter.TryWriteBytes(_pushBytes.AsSpan(0, 4), (uint)count);
		BitConverter.TryWriteBytes(_pushBytes.AsSpan(4, 4), (uint)_lodCount);
		BitConverter.TryWriteBytes(_pushBytes.AsSpan(8, 4), focusXZ.X);
		BitConverter.TryWriteBytes(_pushBytes.AsSpan(12, 4), focusXZ.Y);
		BitConverter.TryWriteBytes(_pushBytes.AsSpan(16, 4), lodScaleAlpha);
		BitConverter.TryWriteBytes(_pushBytes.AsSpan(20, 4), 0.0f);
		BitConverter.TryWriteBytes(_pushBytes.AsSpan(24, 4), 0.0f);
		BitConverter.TryWriteBytes(_pushBytes.AsSpan(28, 4), 0.0f);
	}

	private void OnReadback(Slot slot, byte[] data)
	{
		lock (_sync)
		{
			if (_rd == null || Array.IndexOf(_slots, slot) < 0)
			{
				return;
			}

			bool complete = data != null && data.Length == slot.Count * StrideBytes;
			int latency = (int)Math.Max(
				0,
				Interlocked.Read(ref _renderFrame) - slot.SubmittedFrame);
			bool routedAny = false;

			if (complete)
			{
				for (int segmentIndex = 0; segmentIndex < slot.SegmentCount; segmentIndex++)
				{
					Segment segment = slot.Segments[segmentIndex];
					if (!_owners.TryGetValue(segment.OwnerId, out OwnerState owner) ||
						segment.Generation <= owner.LatestGeneration)
					{
						continue;
					}

					bool canCalculateVelocity =
						owner.HasLatest &&
						owner.LatestCount == segment.Count &&
						owner.LatestVelocityEpoch == slot.VelocityEpoch &&
						double.IsFinite(slot.SampleTime) &&
						double.IsFinite(owner.LatestSampleTime);


					double velocityDt =
						canCalculateVelocity
							? slot.SampleTime -
							  owner.LatestSampleTime
							: 0.0;


					canCalculateVelocity &=
						velocityDt >=
						0.0001;


					float inverseDt =
						canCalculateVelocity
							? (float)(
								1.0 /
								velocityDt)
							: 0.0f;


					for (int i = 0; i < segment.Count; i++)
					{
						int offset =
							(segment.Offset + i) *
							StrideBytes;


						Vector4 current =
							new(
								BitConverter.ToSingle(
									data,
									offset),

								BitConverter.ToSingle(
									data,
									offset + 4),

								BitConverter.ToSingle(
									data,
									offset + 8),

								BitConverter.ToSingle(
									data,
									offset + 12));


						if (canCalculateVelocity)
						{
							Vector4 previous =
								owner.LatestResults[i];


							if (current.W > 0.5f &&
								previous.W > 0.5f)
							{
								owner.LatestVelocities[i] =
									new Vector4(
										(current.X - previous.X) *
										inverseDt,

										(current.Y - previous.Y) *
										inverseDt,

										(current.Z - previous.Z) *
										inverseDt,

										1.0f);
							}
							else
							{
								owner.LatestVelocities[i] =
									Vector4.Zero;
							}
						}


						owner.LatestResults[i] =
							current;
					}


					owner.HasLatestVelocity =
						canCalculateVelocity;


					owner.LatestSampleTime =
						slot.SampleTime;

					owner.LatestVelocityEpoch =
						slot.VelocityEpoch;


					owner.LatestCount = segment.Count;
					owner.LatestGeneration = segment.Generation;
					owner.LatestFrame = slot.SubmittedFrame;
					owner.LatestReadbackFrames = latency;
					owner.HasLatest = true;
					routedAny = true;
				}
			}

			if (routedAny)
			{
				_latestReadbackFrames = latency;
				_hasCompletedResult = true;
			}

			slot.InFlight = false;
		}
	}

	/// <summary>Render thread only. Owner registrations survive GPU reinitialization.</summary>
	internal void Release()
	{
		lock (_sync)
		{
			foreach (Slot slot in _slots)
			{
				if (slot == null)
				{
					continue;
				}

				if (_rd != null)
				{
					if (slot.UniformSet.IsValid) _rd.FreeRid(slot.UniformSet);
					if (slot.Input.IsValid) _rd.FreeRid(slot.Input);
					if (slot.Output.IsValid) _rd.FreeRid(slot.Output);
				}

				slot.InFlight = false;
			}

			if (_rd != null)
			{
				if (_sampler.IsValid) _rd.FreeRid(_sampler);
				if (_pipeline.IsValid) _rd.FreeRid(_pipeline);
				if (_shader.IsValid) _rd.FreeRid(_shader);
			}

			Array.Clear(_slots);
			_sampler = default;
			_pipeline = default;
			_shader = default;
			_rd = null;
			_submittedCount = 0;
			_latestReadbackFrames = -1;
			_hasCompletedResult = false;
			_velocityEpoch = 0;

			foreach (OwnerState owner in _owners.Values)
			{
				owner.DispatchedGeneration = 0;
				owner.LatestCount = 0;
				owner.LatestGeneration = 0;
				owner.LatestFrame = 0;
				owner.LatestReadbackFrames = -1;
				owner.LatestSampleTime = double.NaN;
				owner.LatestVelocityEpoch = 0;
				owner.HasLatest = false;
				owner.HasLatestVelocity = false;

				Array.Clear(
					owner.LatestVelocities,
					0,
					owner.LatestVelocities.Length);
			}
		}
	}
}
