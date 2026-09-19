using System;
using Godot;

namespace OceanFrontier.Water.Waves.AnimatedWaves;

internal sealed class AnimatedWaveFftComposePass : IDisposable
{
	private const int LocalSizeX = 8;
	private const int LocalSizeY = 8;

	private readonly RenderingDevice _rd;

	private readonly int _resolution;
	private readonly int _lodCount;
	private readonly int _fftCascadeCount;
	private readonly float _waveResolutionMultiplier;

	private Rid _shader;
	private Rid _pipeline;
	private Rid _sampler;
	private Rid _uniformSet;
	private bool _firstDispatchLogged;

	private readonly uint[] _pushData =
		new uint[3];

	private readonly byte[] _pushBytes =
		new byte[4 * sizeof(uint)];


	public AnimatedWaveFftComposePass(
		RenderingDevice rd,
		Rid fftDisplacement,
		Rid lodBuffer,
		Rid animatedWaveField,
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
			throw new ArgumentException(
				"FFT displacement RID is invalid.",
				nameof(fftDisplacement));

		if (!lodBuffer.IsValid)
			throw new ArgumentException(
				"LOD buffer RID is invalid.",
				nameof(lodBuffer));

		if (!animatedWaveField.IsValid)
			throw new ArgumentException(
				"AnimatedWaveField RID is invalid.",
				nameof(animatedWaveField));

		if (resolution <= 0)
			throw new ArgumentOutOfRangeException(
				nameof(resolution));

		if (lodCount <= 0)
			throw new ArgumentOutOfRangeException(
				nameof(lodCount));

		if (fftCascadeCount <= 0)
			throw new ArgumentOutOfRangeException(
				nameof(fftCascadeCount));

		if (!float.IsFinite(waveResolutionMultiplier) ||
			waveResolutionMultiplier < 1.0f ||
			waveResolutionMultiplier > 4.0f)
			throw new ArgumentOutOfRangeException(
				nameof(waveResolutionMultiplier));


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
				animatedWaveField);
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
		Rid animatedWaveField)
	{
		RDShaderFile shaderFile =
			GD.Load<RDShaderFile>(
				"res://Ocean/Shaders/Waves/animated_wave_fft_compose.glsl");

		if (shaderFile == null)
		{
			throw new InvalidOperationException(
				"Failed to load animated_wave_fft_compose.glsl.");
		}


		RDShaderSpirV spirv =
			shaderFile.GetSpirV();

		string compileError =
			spirv.GetStageCompileError(
				RenderingDevice.ShaderStage.Compute);

		if (!string.IsNullOrEmpty(compileError))
		{
			throw new InvalidOperationException(
				$"Animated Wave FFT compose shader compilation failed:\n{compileError}");
		}

		if (spirv.GetStageBytecode(
				RenderingDevice.ShaderStage.Compute).Length == 0)
		{
			throw new InvalidOperationException(
				"Animated Wave FFT compose shader has no compute bytecode. " +
				"Reimport animated_wave_fft_compose.glsl in the Godot editor.");
		}

		_shader =
			_rd.ShaderCreateFromSpirV(
				spirv);

		if (!_shader.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create Animated Wave FFT compose shader.");
		}


		_pipeline =
			_rd.ComputePipelineCreate(
				_shader);

		if (!_pipeline.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create Animated Wave FFT compose pipeline.");
		}


		var samplerState =
			new RDSamplerState
			{
				MinFilter =
					RenderingDevice.SamplerFilter.Linear,

				MagFilter =
					RenderingDevice.SamplerFilter.Linear,

				MipFilter =
					RenderingDevice.SamplerFilter.Nearest,

				RepeatU =
					RenderingDevice.SamplerRepeatMode.Repeat,

				RepeatV =
					RenderingDevice.SamplerRepeatMode.Repeat,

				RepeatW =
					RenderingDevice.SamplerRepeatMode.ClampToEdge,
			};


		_sampler =
			_rd.SamplerCreate(
				samplerState);

		if (!_sampler.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create FFT repeat sampler.");
		}


		var fftUniform =
			new RDUniform
			{
				UniformType =
					RenderingDevice.UniformType.SamplerWithTexture,

				Binding = 0,
			};

		// For SamplerWithTexture:
		// sampler RID first, texture RID second.
		fftUniform.AddId(
			_sampler);

		fftUniform.AddId(
			fftDisplacement);


		var lodUniform =
			new RDUniform
			{
				UniformType =
					RenderingDevice.UniformType.StorageBuffer,

				Binding = 1,
			};

		lodUniform.AddId(
			lodBuffer);


		var outputUniform =
			new RDUniform
			{
				UniformType =
					RenderingDevice.UniformType.Image,

				Binding = 2,
			};

		outputUniform.AddId(
			animatedWaveField);


		var uniforms =
			new Godot.Collections.Array<RDUniform>
			{
				fftUniform,
				lodUniform,
				outputUniform,
			};


		_uniformSet =
			_rd.UniformSetCreate(
				uniforms,
				_shader,
				0);

		if (!_uniformSet.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create Animated Wave FFT compose uniform set.");
		}


		_pushData[0] =
			(uint)_resolution;

		_pushData[1] =
			(uint)_lodCount;

		_pushData[2] =
			(uint)_fftCascadeCount;

		System.Buffer.BlockCopy(
			_pushData,
			0,
			_pushBytes,
			0,
			3 * sizeof(uint));

		BitConverter.TryWriteBytes(
			_pushBytes.AsSpan(3 * sizeof(uint), sizeof(float)),
			_waveResolutionMultiplier);


		GD.Print(
			"[Ocean] Animated Wave FFT compose pipeline and uniforms valid.");
	}


	public void Dispatch()
	{
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
				 LocalSizeX - 1) /
				LocalSizeX);

		uint groupsY =
			(uint)(
				(_resolution +
				 LocalSizeY - 1) /
				LocalSizeY);


		_rd.ComputeListDispatch(
			computeList,
			groupsX,
			groupsY,
			(uint)_lodCount);


		_rd.ComputeListEnd();


		if (!_firstDispatchLogged)
		{
			_firstDispatchLogged = true;
			GD.Print(
				"[Ocean] Animated Wave FFT first composition dispatch recorded.");
		}
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
