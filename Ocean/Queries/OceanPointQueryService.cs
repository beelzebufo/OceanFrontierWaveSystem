using System;
using System.Threading;
using Godot;

namespace OceanFrontier.Water.Queries;

/// <summary>
/// Latest-wins batches of world XZ positions. GPU work and callbacks run on the
/// render thread; callers submit and copy completed results on the main thread.
/// Result Vector4.xyz is displacement and .w is 1 when the point is valid.
/// </summary>
public sealed class OceanPointQueryService
{
	public const int Capacity = 256;
	private const int SlotCount = 4;
	private const int StrideBytes = 4 * sizeof(float);
	private const int WorkgroupSize = 64;

	private sealed class Slot
	{
		public Rid Input;
		public Rid Output;
		public Rid UniformSet;
		public Callable Callback;
		public readonly byte[] Upload = new byte[Capacity * StrideBytes];
		public bool InFlight;
		public int Count;
		public long Generation;
		public long SubmittedFrame;
	}

	private readonly object _sync = new();
	private readonly Vector2[] _pendingPositions = new Vector2[Capacity];
	private readonly Vector4[] _latestResults = new Vector4[Capacity];
	private readonly Slot[] _slots = new Slot[SlotCount];
	private readonly byte[] _pushBytes = new byte[16];
	private RenderingDevice _rd;
	private Rid _shader;
	private Rid _pipeline;
	private Rid _sampler;
	private int _lodCount;
	private int _pendingCount;
	private float _pendingMinTexelWidth;
	private long _pendingGeneration;
	private long _nextGeneration;
	private long _dispatchedGeneration;
	private long _renderFrame;
	private int _submittedCount;
	private int _latestCount;
	private long _latestGeneration;
	private long _latestFrame;
	private int _latestReadbackFrames = -1;
	private bool _hasLatest;

	/// <summary>
	/// Replaces an undispatched batch. A zero minimum samples the finest spatial
	/// LOD containing each point; a positive value requests a coarser texel width.
	/// Returns the generation assigned to this batch.
	/// </summary>
	public long SubmitBatch(ReadOnlySpan<Vector2> worldXZ, float minTexelWidth = 0.0f)
	{
		if (worldXZ.Length is < 1 or > Capacity)
			throw new ArgumentOutOfRangeException(nameof(worldXZ));
		if (!float.IsFinite(minTexelWidth) || minTexelWidth < 0.0f)
			throw new ArgumentOutOfRangeException(nameof(minTexelWidth));
		foreach (Vector2 position in worldXZ)
			if (!float.IsFinite(position.X) || !float.IsFinite(position.Y))
				throw new ArgumentException("Query positions must be finite.", nameof(worldXZ));

		lock (_sync)
		{
			worldXZ.CopyTo(_pendingPositions);
			_pendingCount = worldXZ.Length;
			_pendingMinTexelWidth = minTexelWidth;
			_pendingGeneration = ++_nextGeneration;
			return _pendingGeneration;
		}
	}

	/// <summary>Copies the latest completed batch without waiting for the GPU.</summary>
	public bool TryCopyLatest(Span<Vector4> destination, out int count,
		out long generation, out long submittedFrame, out int readbackFrames)
	{
		lock (_sync)
		{
			count = _latestCount;
			generation = _latestGeneration;
			submittedFrame = _latestFrame;
			readbackFrames = _latestReadbackFrames;
			if (!_hasLatest) return false;
			if (destination.Length < count)
				throw new ArgumentException("Destination is smaller than the completed batch.", nameof(destination));
			_latestResults.AsSpan(0, count).CopyTo(destination);
			return true;
		}
	}

	public void GetDiagnostics(out int submittedCount, out int readbackFrames,
		out bool hasCompletedResult)
	{
		lock (_sync)
		{
			submittedCount = _submittedCount;
			readbackFrames = _latestReadbackFrames;
			hasCompletedResult = _hasLatest;
		}
	}

	// Render thread only. The field and LOD metadata are borrowed from the composer.
	internal void Initialize(RenderingDevice rd, Rid field, Rid lodBuffer, int lodCount)
	{
		if (rd == null || !field.IsValid || !lodBuffer.IsValid || lodCount < 1)
			throw new ArgumentException("Point query GPU inputs are invalid.");
		Release();
		_rd = rd;
		_lodCount = lodCount;
		try
		{
			RDShaderFile file = GD.Load<RDShaderFile>(
				"res://Ocean/Shaders/Waves/animated_wave_point_query.glsl");
			if (file == null) throw new InvalidOperationException("Point query shader was not loaded.");
			RDShaderSpirV spirv = file.GetSpirV();
			string error = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
			if (!string.IsNullOrEmpty(error))
				throw new InvalidOperationException($"Point query shader compilation failed:\n{error}");
			if (spirv.GetStageBytecode(RenderingDevice.ShaderStage.Compute).Length == 0)
				throw new InvalidOperationException("Point query shader has no compute bytecode. Reimport it in Godot.");
			_shader = rd.ShaderCreateFromSpirV(spirv);
			if (!_shader.IsValid)
				throw new InvalidOperationException("Point query shader creation failed.");
			_pipeline = rd.ComputePipelineCreate(_shader);
			if (!_pipeline.IsValid)
				throw new InvalidOperationException("Point query pipeline creation failed.");
			var samplerState = new RDSamplerState
			{
				MinFilter = RenderingDevice.SamplerFilter.Linear,
				MagFilter = RenderingDevice.SamplerFilter.Linear,
				MipFilter = RenderingDevice.SamplerFilter.Nearest,
				RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge,
				RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge,
				RepeatW = RenderingDevice.SamplerRepeatMode.ClampToEdge,
			};
			_sampler = rd.SamplerCreate(samplerState);
			if (!_sampler.IsValid)
				throw new InvalidOperationException("Point query sampler creation failed.");

			for (int i = 0; i < SlotCount; i++)
			{
				var slot = new Slot();
				_slots[i] = slot;
				slot.Input = rd.StorageBufferCreate(Capacity * StrideBytes, slot.Upload);
				slot.Output = rd.StorageBufferCreate(Capacity * StrideBytes, new byte[Capacity * StrideBytes]);
				if (!slot.Input.IsValid || !slot.Output.IsValid)
					throw new InvalidOperationException("Point query buffer creation failed.");
				var sampledField = new RDUniform
				{
					UniformType = RenderingDevice.UniformType.SamplerWithTexture,
					Binding = 0,
				};
				sampledField.AddId(_sampler);
				sampledField.AddId(field);
				var lodData = new RDUniform
				{
					UniformType = RenderingDevice.UniformType.StorageBuffer,
					Binding = 1,
				};
				lodData.AddId(lodBuffer);
				var queryData = new RDUniform
				{
					UniformType = RenderingDevice.UniformType.StorageBuffer,
					Binding = 2,
				};
				queryData.AddId(slot.Input);
				var resultData = new RDUniform
				{
					UniformType = RenderingDevice.UniformType.StorageBuffer,
					Binding = 3,
				};
				resultData.AddId(slot.Output);
				slot.UniformSet = rd.UniformSetCreate(
					new Godot.Collections.Array<RDUniform> { sampledField, lodData, queryData, resultData },
					_shader, 0);
				if (!slot.UniformSet.IsValid)
					throw new InvalidOperationException("Point query uniform set creation failed.");
				slot.Callback = Callable.From<byte[]>(data => OnReadback(slot, data));
			}
			GD.Print($"[Ocean Query] Ready: {Capacity} points, {SlotCount} persistent slots.");
		}
		catch
		{
			Release();
			throw;
		}
	}

	// Called immediately after AnimatedWaveComposer.ComposeFft() on the render thread.
	internal void DispatchAfterCompose(Vector2 focusXZ)
	{
		if (_rd == null) return;
		long frame = Interlocked.Increment(ref _renderFrame);
		Slot slot = null;
		lock (_sync)
		{
			if (_pendingGeneration == _dispatchedGeneration) return;
			foreach (Slot candidate in _slots)
				if (!candidate.InFlight) { slot = candidate; break; }
			if (slot == null) return; // Keep the newest pending batch for a later frame.
			slot.Count = _pendingCount;
			slot.Generation = _pendingGeneration;
			slot.SubmittedFrame = frame;
			slot.InFlight = true;
			for (int i = 0; i < slot.Count; i++)
			{
				int offset = i * StrideBytes;
				BitConverter.TryWriteBytes(slot.Upload.AsSpan(offset, 4), _pendingPositions[i].X);
				BitConverter.TryWriteBytes(slot.Upload.AsSpan(offset + 4, 4), _pendingPositions[i].Y);
				BitConverter.TryWriteBytes(slot.Upload.AsSpan(offset + 8, 4), _pendingMinTexelWidth);
				BitConverter.TryWriteBytes(slot.Upload.AsSpan(offset + 12, 4), 0.0f);
			}
		}

		try
		{
			uint bytes = (uint)(slot.Count * StrideBytes);
			Error uploadError = _rd.BufferUpdate(slot.Input, 0, bytes,
				slot.Upload.AsSpan(0, (int)bytes));
			if (uploadError != Error.Ok)
				throw new InvalidOperationException($"Point query upload failed: {uploadError}.");
			BitConverter.TryWriteBytes(_pushBytes.AsSpan(0, 4), (uint)slot.Count);
			BitConverter.TryWriteBytes(_pushBytes.AsSpan(4, 4), (uint)_lodCount);
			BitConverter.TryWriteBytes(_pushBytes.AsSpan(8, 4), focusXZ.X);
			BitConverter.TryWriteBytes(_pushBytes.AsSpan(12, 4), focusXZ.Y);
			long list = _rd.ComputeListBegin();
			_rd.ComputeListBindComputePipeline(list, _pipeline);
			_rd.ComputeListBindUniformSet(list, slot.UniformSet, 0);
			_rd.ComputeListSetPushConstant(list, _pushBytes, (uint)_pushBytes.Length);
			_rd.ComputeListDispatch(list, (uint)((slot.Count + WorkgroupSize - 1) / WorkgroupSize), 1, 1);
			_rd.ComputeListEnd();
			Error readbackError = _rd.BufferGetDataAsync(slot.Output, slot.Callback, 0, bytes);
			if (readbackError != Error.Ok)
				throw new InvalidOperationException($"Point query async readback failed: {readbackError}.");
			lock (_sync)
			{
				_dispatchedGeneration = slot.Generation;
				_submittedCount = slot.Count;
			}
		}
		catch
		{
			lock (_sync) slot.InFlight = false;
			throw;
		}
	}

	private void OnReadback(Slot slot, byte[] data)
	{
		lock (_sync)
		{
			if (_rd == null || Array.IndexOf(_slots, slot) < 0) return;
			if (data != null && data.Length == slot.Count * StrideBytes &&
				slot.Generation > _latestGeneration)
			{
				for (int i = 0; i < slot.Count; i++)
				{
					int offset = i * StrideBytes;
					_latestResults[i] = new Vector4(
						BitConverter.ToSingle(data, offset),
						BitConverter.ToSingle(data, offset + 4),
						BitConverter.ToSingle(data, offset + 8),
						BitConverter.ToSingle(data, offset + 12));
				}
				_latestCount = slot.Count;
				_latestGeneration = slot.Generation;
				_latestFrame = slot.SubmittedFrame;
				_latestReadbackFrames = (int)Math.Max(0,
					Interlocked.Read(ref _renderFrame) - slot.SubmittedFrame);
				_hasLatest = true;
			}
			slot.InFlight = false;
		}
	}

	// Render thread only; compositor-owned RIDs must outlive these uniform sets.
	internal void Release()
	{
		lock (_sync)
		{
			foreach (Slot slot in _slots)
			{
				if (slot == null) continue;
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
			_hasLatest = false;
			_latestCount = 0;
			_latestGeneration = 0;
			_latestFrame = 0;
			_latestReadbackFrames = -1;
			_dispatchedGeneration = 0;
		}
	}
}
