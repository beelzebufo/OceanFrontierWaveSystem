using System;
using Godot;

namespace OceanFrontier.Water.Waves.AnimatedWaves;

/// <summary>
/// GPU pass which writes FFT wave inputs into the per-LOD
/// AnimatedWaveDirectField.
///
/// Crest equivalent:
///
///     ShapeWaves.WaveBatch
///         -> LodDataMgrAnimWaves.FilterWavelength
///         -> AnimWavesSpectrum.shader
///         -> LodDataMgrAnimWaves._waveBuffers
///
/// This pass does NOT write the canonical AnimatedWaveField.
///
/// Output:
///
///     AnimatedWaveDirectField
///
/// Each spatial LOD layer receives only the FFT wavelength
/// contributions assigned directly to that LOD.
///
/// A separate coarse-to-fine combine pass will later produce:
///
///     AnimatedWaveField
/// </summary>
internal sealed class AnimatedWaveFftDirectPass : IDisposable
{
	private const int LocalSizeX = 8;
	private const int LocalSizeY = 8;

	private const int PushConstantBytes = 32;


	private readonly RenderingDevice _rd;

	private readonly int _resolution;
	private readonly int _lodCount;
	private readonly int _fftCascadeCount;

	private readonly float _waveResolutionMultiplier;


	private Rid _shader;
	private Rid _pipeline;
	private Rid _sampler;
	private Rid _uniformSet;


	private readonly byte[] _pushBytes =
		new byte[PushConstantBytes];


	private bool _firstDispatchLogged;


	public AnimatedWaveFftDirectPass(
		RenderingDevice rd,
		Rid fftDisplacement,
		Rid lodBuffer,
		Rid directWaveField,
		Rid seaFloorDepthField,
		int resolution,
		int lodCount,
		int fftCascadeCount,
		float waveResolutionMultiplier)
	{
		_rd =
			rd ??
			throw new ArgumentNullException(
				nameof(rd));


		if (!fftDisplacement.IsValid)
		{
			throw new ArgumentException(
				"FFT displacement RID is invalid.",
				nameof(fftDisplacement));
		}


		if (!lodBuffer.IsValid)
		{
			throw new ArgumentException(
				"Animated Wave LOD buffer RID is invalid.",
				nameof(lodBuffer));
		}


		if (!directWaveField.IsValid || !seaFloorDepthField.IsValid)
		{
			throw new ArgumentException(
				"Animated Wave direct-field RID is invalid.",
				nameof(directWaveField));
		}


		if (resolution <= 0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(resolution));
		}


		if (lodCount <= 0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(lodCount));
		}


		if (fftCascadeCount <= 0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(fftCascadeCount));
		}


		if (!float.IsFinite(
				waveResolutionMultiplier) ||
			waveResolutionMultiplier < 1.0f ||
			waveResolutionMultiplier > 4.0f)
		{
			throw new ArgumentOutOfRangeException(
				nameof(waveResolutionMultiplier));
		}


		_resolution =
			resolution;

		_lodCount =
			lodCount;

		_fftCascadeCount =
			fftCascadeCount;

		_waveResolutionMultiplier =
			waveResolutionMultiplier;


		try
		{
			Create(
				fftDisplacement,
				lodBuffer,
				directWaveField,
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
		Rid directWaveField,
		Rid seaFloorDepthField)
	{
		RDShaderFile shaderFile =
			GD.Load<RDShaderFile>(
				"res://Ocean/Shaders/Waves/animated_wave_fft_direct.glsl");


		if (shaderFile == null)
		{
			throw new InvalidOperationException(
				"Failed to load animated_wave_fft_direct.glsl.");
		}


		RDShaderSpirV spirv =
			shaderFile.GetSpirV();


		string compileError =
			spirv.GetStageCompileError(
				RenderingDevice.ShaderStage.Compute);


		if (!string.IsNullOrEmpty(
				compileError))
		{
			throw new InvalidOperationException(
				"Animated Wave FFT direct shader compilation failed:\n" +
				compileError);
		}


		if (spirv.GetStageBytecode(
				RenderingDevice.ShaderStage.Compute)
			.Length == 0)
		{
			throw new InvalidOperationException(
				"Animated Wave FFT direct shader has no compute bytecode. " +
				"Reimport animated_wave_fft_direct.glsl in Godot.");
		}


		_shader =
			_rd.ShaderCreateFromSpirV(
				spirv);


		if (!_shader.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create Animated Wave FFT direct shader.");
		}


		_pipeline =
			_rd.ComputePipelineCreate(
				_shader);


		if (!_pipeline.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create Animated Wave FFT direct pipeline.");
		}


		//
		// Crest FFT wave buffers are periodic.
		//
		// Therefore direct FFT input sampling must repeat in X/Y.
		//

		var samplerState =
			new RDSamplerState
			{
				MinFilter =
					RenderingDevice
						.SamplerFilter
						.Linear,

				MagFilter =
					RenderingDevice
						.SamplerFilter
						.Linear,

				MipFilter =
					RenderingDevice
						.SamplerFilter
						.Nearest,

				RepeatU =
					RenderingDevice
						.SamplerRepeatMode
						.Repeat,

				RepeatV =
					RenderingDevice
						.SamplerRepeatMode
						.Repeat,

				RepeatW =
					RenderingDevice
						.SamplerRepeatMode
						.ClampToEdge,
			};


		_sampler =
			_rd.SamplerCreate(
				samplerState);


		if (!_sampler.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create FFT direct-input sampler.");
		}


		//
		// binding 0:
		//
		// Raw periodic FFT displacement.
		//

		var fftUniform =
			new RDUniform
			{
				UniformType =
					RenderingDevice
						.UniformType
						.SamplerWithTexture,

				Binding =
					0,
			};


		//
		// SamplerWithTexture requires:
		//
		//     sampler RID
		//     texture RID
		//

		fftUniform.AddId(
			_sampler);

		fftUniform.AddId(
			fftDisplacement);


		//
		// binding 1:
		//
		// Spatial Animated Wave LOD metadata.
		//

		var lodUniform =
			new RDUniform
			{
				UniformType =
					RenderingDevice
						.UniformType
						.StorageBuffer,

				Binding =
					1,
			};


		lodUniform.AddId(
			lodBuffer);


		//
		// binding 2:
		//
		// Direct per-LOD Animated Waves buffer.
		//
		// Crest equivalent:
		//
		//     LodDataMgrAnimWaves._waveBuffers
		//

		var outputUniform =
			new RDUniform
			{
				UniformType =
					RenderingDevice
						.UniformType
						.Image,

				Binding =
					2,
			};


		outputUniform.AddId(
			directWaveField);


		var uniforms =
			new Godot.Collections.Array<RDUniform>
			{
				fftUniform,
				lodUniform,
				outputUniform,
				CreateImageUniform(3, seaFloorDepthField),
			};


		_uniformSet =
			_rd.UniformSetCreate(
				uniforms,
				_shader,
				0);


		if (!_uniformSet.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create Animated Wave FFT direct uniform set.");
		}


		//
		// Static push-constant fields.
		//
		// Shader layout:
		//
		//  0 : uint  resolution
		//  4 : uint  lod_count
		//  8 : uint  fft_cascade_count
		// 12 : float wave_resolution_multiplier
		// 16 : float lod_scale_alpha
		// 20 : uint  has_sea_floor_depth
		// 24 : float shallow_water_attenuation
		// 28 : float shallow_water_maximum_depth
		//

		BitConverter.TryWriteBytes(
			_pushBytes.AsSpan(
				0,
				sizeof(uint)),
			(uint)_resolution);


		BitConverter.TryWriteBytes(
			_pushBytes.AsSpan(
				4,
				sizeof(uint)),
			(uint)_lodCount);


		BitConverter.TryWriteBytes(
			_pushBytes.AsSpan(
				8,
				sizeof(uint)),
			(uint)_fftCascadeCount);


		BitConverter.TryWriteBytes(
			_pushBytes.AsSpan(
				12,
				sizeof(float)),
			_waveResolutionMultiplier);


		//
		// Dynamic lod_scale_alpha is written in Dispatch().
		// Padding remains zero.
		//


		GD.Print(
			"[Ocean] Animated Wave FFT direct pipeline and uniforms valid.");
	}


	/// <summary>
	/// Writes direct FFT wave contributions into
	/// AnimatedWaveDirectField.
	///
	/// lodScaleAlpha follows Crest's end-of-LOD-chain transition:
	///
	///     second-last LOD = 1 - alpha
	///     last LOD        = alpha
	///
	/// This pass does not create cumulative Animated Waves.
	/// </summary>
	public void Dispatch(
		float lodScaleAlpha,
		bool hasSeaFloorDepth,
		float shallowWaterAttenuation,
		float shallowWaterMaximumDepth)
	{
		if (!float.IsFinite(
				lodScaleAlpha))
		{
			throw new ArgumentOutOfRangeException(
				nameof(lodScaleAlpha));
		}


		lodScaleAlpha =
			Mathf.Clamp(
				lodScaleAlpha,
				0.0f,
				1.0f);


		BitConverter.TryWriteBytes(
			_pushBytes.AsSpan(
				16,
				sizeof(float)),
			lodScaleAlpha);

		BitConverter.TryWriteBytes(_pushBytes.AsSpan(20, sizeof(uint)), hasSeaFloorDepth ? 1u : 0u);
		BitConverter.TryWriteBytes(_pushBytes.AsSpan(24, sizeof(float)), Mathf.Clamp(shallowWaterAttenuation, 0.0f, 1.0f));
		BitConverter.TryWriteBytes(_pushBytes.AsSpan(28, sizeof(float)), Mathf.Clamp(shallowWaterMaximumDepth, 1.0f, 1000.0f));


		long computeList =
			_rd.ComputeListBegin();


		_rd.ComputeListBindComputePipeline(
			computeList,
			_pipeline);


		_rd.ComputeListBindUniformSet(
			computeList,
			_uniformSet,
			0);


		_rd.ComputeListSetPushConstant(
			computeList,
			_pushBytes,
			(uint)_pushBytes.Length);


		uint groupsX =
			(uint)(
				(_resolution +
				 LocalSizeX -
				 1) /
				LocalSizeX);


		uint groupsY =
			(uint)(
				(_resolution +
				 LocalSizeY -
				 1) /
				LocalSizeY);


		//
		// One Z workgroup layer per spatial Animated Wave LOD.
		//
		// Layers are independent in this direct-input stage.
		//

		_rd.ComputeListDispatch(
			computeList,
			groupsX,
			groupsY,
			(uint)_lodCount);


		_rd.ComputeListEnd();


		if (!_firstDispatchLogged)
		{
			_firstDispatchLogged =
				true;


			GD.Print(
				"[Ocean] Animated Wave FFT first direct-input dispatch recorded.");
		}
	}

	private static RDUniform CreateImageUniform(int binding, Rid texture)
	{
		var uniform = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = binding };
		uniform.AddId(texture);
		return uniform;
	}


	public void Dispose()
	{
		if (_uniformSet.IsValid)
		{
			_rd.FreeRid(
				_uniformSet);


			_uniformSet =
				default;
		}


		if (_sampler.IsValid)
		{
			_rd.FreeRid(
				_sampler);


			_sampler =
				default;
		}


		if (_pipeline.IsValid)
		{
			_rd.FreeRid(
				_pipeline);


			_pipeline =
				default;
		}


		if (_shader.IsValid)
		{
			_rd.FreeRid(
				_shader);


			_shader =
				default;
		}
	}
}
