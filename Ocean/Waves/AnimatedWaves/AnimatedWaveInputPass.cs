using System;
using Godot;

namespace OceanFrontier.Water.Waves.AnimatedWaves;

/// <summary>
/// Applies ordered Animated Waves input descriptors to DirectField or the
/// canonical final field. Ordinary inputs share one dispatch per active phase;
/// packet-only pre-combine work is bounded to eligible LOD regions.
/// </summary>
internal sealed class AnimatedWaveInputPass : IDisposable
{
	public const int DescriptorStrideBytes = 80;
	public const int DescriptorBufferBytes =
		AnimatedWaveInputRegistry.Capacity *
		DescriptorStrideBytes;


	private const int LocalSizeX = 8;
	private const int LocalSizeY = 8;
	private const int PushConstantBytes = 64;
	private readonly GerstnerWavePacketInput[] _packetInputs = new GerstnerWavePacketInput[GerstnerWavePacketInput.Capacity];
	private int _packetCount;
	private bool _hasOrdinaryPreInputs;


	private readonly RenderingDevice _rd;
	private readonly int _resolution;
	private readonly int _lodCount;
	private readonly int _fftCascadeCount;
	private readonly float _waveResolutionMultiplier;

	private readonly byte[] _descriptorBytes =
		new byte[DescriptorBufferBytes];

	private readonly byte[] _pushBytes =
		new byte[PushConstantBytes];


	private Rid _shader;
	private Rid _pipeline;
	private Rid _fftSampler;
	private Rid _descriptorBuffer;
	private Rid _directUniformSet;
	private Rid _finalUniformSet;


	public int ActiveInputCount { get; private set; }
	public bool HasPreCombineInputs { get; private set; }
	public bool HasPostCombineInputs { get; private set; }
	public long DispatchCount { get; private set; }


	public AnimatedWaveInputPass(
		RenderingDevice rd,
		Rid fftDisplacement,
		Rid lodBuffer,
		Rid directField,
		Rid finalField,
		Rid seaFloorDepthField,
		int resolution,
		int lodCount,
		int fftCascadeCount,
		float waveResolutionMultiplier)
	{
		_rd = rd ??
			throw new ArgumentNullException(
				nameof(rd));


		if (!fftDisplacement.IsValid ||
			!lodBuffer.IsValid ||
			!directField.IsValid ||
			!finalField.IsValid ||
			!seaFloorDepthField.IsValid)
		{
			throw new ArgumentException(
				"Animated Wave input pass received an invalid GPU resource.");
		}


		if (resolution <= 0 ||
			lodCount <= 0 ||
			fftCascadeCount <= 0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(resolution));
		}


		_resolution =
			resolution;

		_lodCount =
			lodCount;

		if (!float.IsFinite(waveResolutionMultiplier) ||
			waveResolutionMultiplier < 1.0f ||
			waveResolutionMultiplier > 4.0f)
		{
			throw new ArgumentOutOfRangeException(
				nameof(waveResolutionMultiplier));
		}

		_fftCascadeCount = fftCascadeCount;
		_waveResolutionMultiplier = waveResolutionMultiplier;


		try
		{
			Create(
				fftDisplacement,
				lodBuffer,
				directField,
				finalField,
				seaFloorDepthField);
		}
		catch
		{
			Dispose();

			throw;
		}
	}


	private void Create(
		Rid fftDisplacement,
		Rid lodBuffer,
		Rid directField,
		Rid finalField,
		Rid seaFloorDepthField)
	{
		RDShaderFile shaderFile =
			GD.Load<RDShaderFile>(
				"res://Ocean/Shaders/Waves/animated_wave_inputs.glsl");


		if (shaderFile == null)
		{
			throw new InvalidOperationException(
				"Failed to load animated_wave_inputs.glsl.");
		}


		RDShaderSpirV spirv =
			shaderFile.GetSpirV();


		string compileError =
			spirv.GetStageCompileError(
				RenderingDevice.ShaderStage.Compute);


		if (!string.IsNullOrEmpty(compileError))
		{
			throw new InvalidOperationException(
				"Animated Wave input shader compilation failed:\n" +
				compileError);
		}


		if (spirv.GetStageBytecode(
				RenderingDevice.ShaderStage.Compute)
			.Length == 0)
		{
			throw new InvalidOperationException(
				"Animated Wave input shader has no compute bytecode. " +
				"Reimport animated_wave_inputs.glsl in Godot.");
		}


		_shader =
			_rd.ShaderCreateFromSpirV(
				spirv);


		_pipeline =
			_rd.ComputePipelineCreate(
				_shader);


		_descriptorBuffer =
			_rd.StorageBufferCreate(
				DescriptorBufferBytes,
				_descriptorBytes);


		if (!_shader.IsValid ||
			!_pipeline.IsValid ||
			!_descriptorBuffer.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create Animated Wave input GPU resources.");
		}


		var samplerState = new RDSamplerState
		{
			MinFilter = RenderingDevice.SamplerFilter.Linear,
			MagFilter = RenderingDevice.SamplerFilter.Linear,
			MipFilter = RenderingDevice.SamplerFilter.Nearest,
			RepeatU = RenderingDevice.SamplerRepeatMode.Repeat,
			RepeatV = RenderingDevice.SamplerRepeatMode.Repeat,
			RepeatW = RenderingDevice.SamplerRepeatMode.ClampToEdge,
		};

		_fftSampler = _rd.SamplerCreate(samplerState);

		if (!_fftSampler.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create Animated Wave directional FFT sampler.");
		}


		_directUniformSet =
			CreateUniformSet(
				fftDisplacement,
				lodBuffer,
				directField,
				seaFloorDepthField);


		_finalUniformSet =
			CreateUniformSet(
				fftDisplacement,
				lodBuffer,
				finalField,
				seaFloorDepthField);


		if (!_directUniformSet.IsValid ||
			!_finalUniformSet.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create Animated Wave input uniform sets.");
		}


		GD.Print(
			$"[Ocean] Animated Wave inputs ready: " +
			$"{AnimatedWaveInputRegistry.Capacity} descriptors, " +
			$"{DescriptorBufferBytes} bytes.");
	}


	private Rid CreateUniformSet(
		Rid fftDisplacement,
		Rid lodBuffer,
		Rid target,
		Rid seaFloorDepthField)
	{
		var lodUniform =
			new RDUniform
			{
				UniformType =
					RenderingDevice.UniformType.StorageBuffer,

				Binding =
					0,
			};


		lodUniform.AddId(
			lodBuffer);


		var descriptorUniform =
			new RDUniform
			{
				UniformType =
					RenderingDevice.UniformType.StorageBuffer,

				Binding =
					1,
			};


		descriptorUniform.AddId(
			_descriptorBuffer);


		var targetUniform =
			new RDUniform
			{
				UniformType =
					RenderingDevice.UniformType.Image,

				Binding =
					2,
			};


		targetUniform.AddId(
			target);

		var fftUniform = new RDUniform
		{
			UniformType = RenderingDevice.UniformType.SamplerWithTexture,
			Binding = 3,
		};

		fftUniform.AddId(_fftSampler);
		fftUniform.AddId(fftDisplacement);

		var depthUniform = new RDUniform
		{
			UniformType = RenderingDevice.UniformType.Image,
			Binding = 4,
		};
		depthUniform.AddId(seaFloorDepthField);


		return _rd.UniformSetCreate(
			new Godot.Collections.Array<RDUniform>
			{
				lodUniform,
				descriptorUniform,
				targetUniform,
				fftUniform,
				depthUniform,
			},
			_shader,
			0);
	}


	/// <summary>
	/// Uploads one ordered snapshot. std430 descriptor stride is 80 bytes:
	///
	///  0 vec4 center_xz_axis_x
	/// 16 vec4 axis_z_size_xz
	/// 32 vec4 feather_weight_amplitude_wavelength
	/// 48 vec4 displacement_xyz_scale; x aliases DirectionalFft radians
	/// 64 uvec4 placement_blend_operation_flags
	/// Radial packet operation reuses these slots as documented in WriteDescriptor.
	/// </summary>
	public void Upload(
		ReadOnlySpan<AnimatedWaveInputSnapshot> inputs)
	{
		if (inputs.Length >
			AnimatedWaveInputRegistry.Capacity)
		{
			throw new ArgumentOutOfRangeException(
				nameof(inputs));
		}


		Array.Clear(_packetInputs);
		_packetCount = 0;
		_hasOrdinaryPreInputs = false;

		ActiveInputCount =
			inputs.Length;

		HasPreCombineInputs =
			false;

		HasPostCombineInputs =
			false;


		if (inputs.IsEmpty)
		{
			return;
		}


		for (int index = 0;
			 index < inputs.Length;
			 index++)
		{
			AnimatedWaveInputSnapshot input =
				inputs[index];


			if (input.Placement ==
				AnimatedWaveInputPlacement.AllLodsPostCombine)
			{
				HasPostCombineInputs =
					true;
			}
			else
			{
				HasPreCombineInputs =
					true;
			}


			if (input.Packet != null) _packetInputs[_packetCount++] = input.Packet;
			else if (input.Placement != AnimatedWaveInputPlacement.AllLodsPostCombine)
				_hasOrdinaryPreInputs = true;

			WriteDescriptor(
				index,
				input);
		}


		uint byteCount =
			(uint)(
				inputs.Length *
				DescriptorStrideBytes);


		Error error =
			_rd.BufferUpdate(
				_descriptorBuffer,
				0,
				byteCount,
				_descriptorBytes.AsSpan(
					0,
					(int)byteCount));


		if (error != Error.Ok)
		{
			throw new InvalidOperationException(
				$"Animated Wave input descriptor upload failed: {error}.");
		}
	}


	public void DispatchPreCombine(
		float lodScaleAlpha,
		bool hasSeaFloorDepth,
		float shallowWaterAttenuation,
		float shallowWaterMaximumDepth,
		float simulationTime = 0.0f,
		float gravity = 9.81f,
		AnimatedWaveLodLayout layout = null)
	{
		if (HasPreCombineInputs && !_hasOrdinaryPreInputs && layout != null)
		{
			DispatchPacketBounds(layout, lodScaleAlpha, hasSeaFloorDepth,
				shallowWaterAttenuation, shallowWaterMaximumDepth, simulationTime, gravity);
			return;
		}
		if (HasPreCombineInputs)
		{
			Dispatch(
				_directUniformSet,
				0,
				lodScaleAlpha,
				hasSeaFloorDepth,
				shallowWaterAttenuation,
				shallowWaterMaximumDepth, simulationTime, gravity);
		}
	}


	public void DispatchPostCombine()
	{
		if (HasPostCombineInputs)
		{
			Dispatch(
				_finalUniformSet,
				1,
				1.0f,
				false,
				0.0f,
				1000.0f);
		}
	}


	// Packet-only frames touch just the union of packet AABBs on eligible LODs.
	// Mixed ordinary-input frames reuse their existing dispatch and ordering.
	private void DispatchPacketBounds(AnimatedWaveLodLayout layout, float alpha, bool depth,
		float attenuation, float maxDepth, float time, float gravity)
	{
		for (int lod = 0; lod < _lodCount; lod++)
		{
			AnimatedWaveLodSlice slice = layout[lod];
			Vector2 lower = new(float.PositiveInfinity, float.PositiveInfinity);
			Vector2 upper = new(float.NegativeInfinity, float.NegativeInfinity);
			for (int i = 0; i < _packetCount; i++)
			{
				GerstnerWavePacketInput packet = _packetInputs[i];
				float elapsed = time - packet.StartTime;
				float wavelength = packet.Wavelength * 0.25f;
				if (elapsed <= 0 || elapsed >= packet.Lifetime || wavelength < slice.MinWavelength) continue;
				bool lastPair = wavelength >= layout[_lodCount - 1].MinWavelength;
				bool eligible = lastPair
					? _lodCount == 1 || (lod == _lodCount - 2 && alpha < 1) || (lod == _lodCount - 1 && alpha > 0)
					: wavelength < slice.MaxWavelength;
				if (!eligible) continue;
				float radius = 0.5f * MathF.Sqrt(gravity * packet.Wavelength / Mathf.Tau) * elapsed;
				Vector2 extent = new(radius, radius);
				lower = lower.Min(packet.WorldPositionXZ - extent);
				upper = upper.Max(packet.WorldPositionXZ + extent);
			}
			if (!lower.IsFinite()) continue;
			Vector2 corner = slice.CenterXZ - Vector2.One * slice.WorldSize * 0.5f;
			// Clamp in float before converting to int, including far-away finite positions.
			Vector2 lo = ((lower - corner) / slice.TexelWidth).Clamp(Vector2.Zero, Vector2.One * _resolution);
			Vector2 hi = ((upper - corner) / slice.TexelWidth).Clamp(Vector2.Zero, Vector2.One * _resolution);
			int x = (int)MathF.Floor(lo.X), y = (int)MathF.Floor(lo.Y);
			int width = (int)MathF.Ceiling(hi.X) - x, height = (int)MathF.Ceiling(hi.Y) - y;
			if (width <= 0 || height <= 0) continue;
			Dispatch(_directUniformSet, 0, alpha, depth, attenuation, maxDepth, time, gravity,
				x, y, lod, width, height, 1);
		}
	}


	private void Dispatch(
		Rid uniformSet,
		uint phase,
		float lodScaleAlpha,
		bool hasSeaFloorDepth,
		float shallowWaterAttenuation,
		float shallowWaterMaximumDepth,
		float simulationTime = 0.0f,
		float gravity = 9.81f,
		int offsetX = 0, int offsetY = 0, int offsetLod = 0,
		int width = 0, int height = 0, int layers = 0)
	{
		WriteUInt(
			_pushBytes,
			0,
			(uint)_resolution);

		WriteUInt(
			_pushBytes,
			4,
			(uint)_lodCount);

		WriteUInt(
			_pushBytes,
			8,
			(uint)ActiveInputCount);

		WriteUInt(
			_pushBytes,
			12,
			phase);

		WriteFloat(
			_pushBytes,
			16,
			Mathf.Clamp(
				lodScaleAlpha,
				0.0f,
				1.0f));

		WriteUInt(
			_pushBytes,
			20,
			(uint)_fftCascadeCount);

		WriteFloat(
			_pushBytes,
			24,
			_waveResolutionMultiplier);

		WriteFloat(_pushBytes, 40, simulationTime);
		WriteFloat(_pushBytes, 44, gravity);
		WriteUInt(_pushBytes, 28, hasSeaFloorDepth ? 1u : 0u);
		WriteFloat(_pushBytes, 32, Mathf.Clamp(shallowWaterAttenuation, 0.0f, 1.0f));
		WriteFloat(_pushBytes, 36, Mathf.Clamp(shallowWaterMaximumDepth, 1.0f, 1000.0f));


		WriteUInt(_pushBytes, 48, (uint)offsetX);
		WriteUInt(_pushBytes, 52, (uint)offsetY);
		WriteUInt(_pushBytes, 56, (uint)offsetLod);

		long computeList =
			_rd.ComputeListBegin();


		_rd.ComputeListBindComputePipeline(
			computeList,
			_pipeline);


		_rd.ComputeListBindUniformSet(
			computeList,
			uniformSet,
			0);


		_rd.ComputeListSetPushConstant(
			computeList,
			_pushBytes,
			PushConstantBytes);


		_rd.ComputeListDispatch(
			computeList,
			(uint)(((width > 0 ? width : _resolution) + LocalSizeX - 1) / LocalSizeX),
			(uint)(((height > 0 ? height : _resolution) + LocalSizeY - 1) / LocalSizeY),
			(uint)(layers > 0 ? layers : _lodCount));


		_rd.ComputeListEnd();


		DispatchCount++;
	}


	private void WriteDescriptor(
		int index,
		AnimatedWaveInputSnapshot input)
	{
		int offset =
			index *
			DescriptorStrideBytes;


		if (input.Packet is GerstnerWavePacketInput packet)
		{
			// Radial operation reuses the existing 80-byte tagged descriptor.
			WriteFloat(_descriptorBytes, offset + 0, packet.WorldPositionXZ.X);
			WriteFloat(_descriptorBytes, offset + 4, packet.WorldPositionXZ.Y);
			WriteFloat(_descriptorBytes, offset + 8, packet.StartTime);
			WriteFloat(_descriptorBytes, offset + 12, packet.Lifetime);
			WriteFloat(_descriptorBytes, offset + 16, packet.Wavelength);
			WriteFloat(_descriptorBytes, offset + 20, packet.Amplitude);
			WriteFloat(_descriptorBytes, offset + 24, packet.CrestCount);
			WriteFloat(_descriptorBytes, offset + 28, packet.Chop);
			WriteFloat(_descriptorBytes, offset + 32, packet.InitialPhase);
			WriteFloat(_descriptorBytes, offset + 36, packet.FadeIn);
			WriteFloat(_descriptorBytes, offset + 40, packet.FadeOut);
			WriteFloat(_descriptorBytes, offset + 44, packet.AttenuationStrength);
			_descriptorBytes.AsSpan(offset + 48, 16).Clear();
			WriteUInt(_descriptorBytes, offset + 64, 0);
			WriteUInt(_descriptorBytes, offset + 68, 0);
			WriteUInt(_descriptorBytes, offset + 72, (uint)AnimatedWaveInputOperation.RadialGerstnerPacket);
			WriteUInt(_descriptorBytes, offset + 76, 0);
			return;
		}

		WriteFloat(_descriptorBytes, offset + 0, input.CenterXZ.X);
		WriteFloat(_descriptorBytes, offset + 4, input.CenterXZ.Y);
		WriteFloat(_descriptorBytes, offset + 8, input.AxisX.X);
		WriteFloat(_descriptorBytes, offset + 12, input.AxisX.Y);

		WriteFloat(_descriptorBytes, offset + 16, input.AxisZ.X);
		WriteFloat(_descriptorBytes, offset + 20, input.AxisZ.Y);
		WriteFloat(_descriptorBytes, offset + 24, input.SizeXZ.X);
		WriteFloat(_descriptorBytes, offset + 28, input.SizeXZ.Y);

		WriteFloat(_descriptorBytes, offset + 32, input.FeatherWidth);
		WriteFloat(_descriptorBytes, offset + 36, input.Weight);
		WriteFloat(_descriptorBytes, offset + 40, input.SourceAmplitude);
		WriteFloat(_descriptorBytes, offset + 44, input.WavelengthMeters);

		// DirectionalFft aliases offset 48 as relative direction radians.
		WriteFloat(
			_descriptorBytes,
			offset + 48,
			input.Operation == AnimatedWaveInputOperation.DirectionalFft
				? input.DirectionRadians
				: input.Displacement.X);
		WriteFloat(_descriptorBytes, offset + 52, input.Displacement.Y);
		WriteFloat(_descriptorBytes, offset + 56, input.Displacement.Z);
		WriteFloat(_descriptorBytes, offset + 60, input.Scale);

		WriteUInt(_descriptorBytes, offset + 64, (uint)input.Placement);
		WriteUInt(_descriptorBytes, offset + 68, (uint)input.BlendMode);
		WriteUInt(_descriptorBytes, offset + 72, (uint)input.Operation);
		WriteUInt(_descriptorBytes, offset + 76, input.Invert ? 1u : 0u);
	}


	private static void WriteFloat(
		byte[] destination,
		int offset,
		float value)
	{
		BitConverter.TryWriteBytes(
			destination.AsSpan(
				offset,
				sizeof(float)),
			value);
	}


	private static void WriteUInt(
		byte[] destination,
		int offset,
		uint value)
	{
		BitConverter.TryWriteBytes(
			destination.AsSpan(
				offset,
				sizeof(uint)),
			value);
	}


	public void Dispose()
	{
		if (_finalUniformSet.IsValid)
		{
			_rd.FreeRid(_finalUniformSet);
			_finalUniformSet = default;
		}


		if (_directUniformSet.IsValid)
		{
			_rd.FreeRid(_directUniformSet);
			_directUniformSet = default;
		}


		if (_descriptorBuffer.IsValid)
		{
			_rd.FreeRid(_descriptorBuffer);
			_descriptorBuffer = default;
		}

		if (_fftSampler.IsValid)
		{
			_rd.FreeRid(_fftSampler);
			_fftSampler = default;
		}


		if (_pipeline.IsValid)
		{
			_rd.FreeRid(_pipeline);
			_pipeline = default;
		}


		if (_shader.IsValid)
		{
			_rd.FreeRid(_shader);
			_shader = default;
		}
	}
}
