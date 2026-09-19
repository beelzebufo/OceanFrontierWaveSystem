using System;
using Godot;
using Godot.Collections;

namespace OceanFrontier.Water.Waves.FFT;

/// <summary>
/// Performs the 2D inverse FFT for H / displacement-X / displacement-Z.
///
/// Dispatch order:
///
/// 1. X axis:
///    SpectrumHeight/X/Z -> FftTempHeight/X/Z
///
/// 2. compute barrier
///
/// 3. Y axis:
///    FftTempHeight/X/Z -> Displacement
///
/// Must execute on the rendering thread.
/// </summary>
internal sealed class FftTransformPass : IDisposable
{
	private const string ShaderPath =
		"res://Ocean/Shaders/Waves/fft_ifft.glsl";

	private const int MaxSupportedResolution = 512;

	private const int LocalSize = 128;

	// 6 shared vec2[MAX_FFT_SIZE] arrays.
	private const ulong RequiredSharedMemoryBytes =
		6UL *
		MaxSupportedResolution *
		2UL *
		sizeof(float);

	private readonly RenderingDevice _rd;
	private readonly FftGpuResources _resources;

	private Rid _sampler;

	private Rid _shader;
	private Rid _pipeline;

	private Rid _xUniformSet;
	private Rid _yUniformSet;
	private bool _dispatchLogged;


	public FftTransformPass(
		RenderingDevice rd,
		FftGpuResources resources)
	{
		_rd = rd ??
			throw new ArgumentNullException(nameof(rd));

		_resources = resources ??
			throw new ArgumentNullException(nameof(resources));

		ValidateConfiguration();

		try
		{
			Create();
		}
		catch
		{
			Dispose();
			throw;
		}
	}


	private void ValidateConfiguration()
	{
		if (_resources.Resolution >
			MaxSupportedResolution)
		{
			throw new NotSupportedException(
				$"FFT resolution {_resources.Resolution} exceeds " +
				$"{MaxSupportedResolution}, which is the maximum " +
				"supported by the current IFFT kernel.");
		}


		ulong maxInvocations =
			_rd.LimitGet(
				RenderingDevice.Limit
					.MaxComputeWorkgroupInvocations);

		ulong maxSizeX =
			_rd.LimitGet(
				RenderingDevice.Limit
					.MaxComputeWorkgroupSizeX);

		ulong maxSharedMemory =
			_rd.LimitGet(
				RenderingDevice.Limit
					.MaxComputeSharedMemorySize);


		if (maxInvocations < LocalSize ||
			maxSizeX < LocalSize)
		{
			throw new NotSupportedException(
				$"GPU does not support the required " +
				$"{LocalSize}-thread FFT workgroup. " +
				$"MaxInvocations={maxInvocations}, " +
				$"MaxWorkgroupSizeX={maxSizeX}.");
		}


		if (maxSharedMemory <
			RequiredSharedMemoryBytes)
		{
			throw new NotSupportedException(
				$"GPU compute shared memory is too small for FFT. " +
				$"Required={RequiredSharedMemoryBytes} bytes, " +
				$"available={maxSharedMemory} bytes.");
		}


		ulong maxStorageImages =
			_rd.LimitGet(
				RenderingDevice.Limit
					.MaxStorageImagesPerUniformSet);

		GD.Print(
			$"[Ocean] IFFT limits: MaxComputeWorkgroupInvocations={maxInvocations}, " +
			$"MaxComputeWorkgroupSizeX={maxSizeX}, " +
			$"MaxComputeSharedMemorySize={maxSharedMemory}, " +
			$"MaxStorageImagesPerUniformSet={maxStorageImages}, " +
			$"RequiredSharedMemory={RequiredSharedMemoryBytes}");

		if (maxStorageImages < 4)
		{
			throw new NotSupportedException(
				"GPU does not support the four storage-image " +
				"bindings required by the FFT transform.");
		}
	}


	private void Create()
	{
		CreateSampler();
		CreatePipeline();
		CreateUniformSets();
	}


	private void CreateSampler()
	{
		var samplerState =
			new RDSamplerState
			{
				MinFilter =
					RenderingDevice.SamplerFilter.Nearest,

				MagFilter =
					RenderingDevice.SamplerFilter.Nearest,

				RepeatU =
					RenderingDevice.SamplerRepeatMode.ClampToEdge,

				RepeatV =
					RenderingDevice.SamplerRepeatMode.ClampToEdge,
			};


		_sampler =
			_rd.SamplerCreate(
				samplerState);


		if (!_sampler.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create FFT sampler.");
		}
	}


	private void CreatePipeline()
	{
		RDShaderFile shaderFile =
			GD.Load<RDShaderFile>(
				ShaderPath);


		if (shaderFile == null)
		{
			throw new InvalidOperationException(
				$"Could not load FFT shader: {ShaderPath}");
		}


		RDShaderSpirV spirV =
			shaderFile.GetSpirV();


		string compileError =
			spirV.GetStageCompileError(
				RenderingDevice.ShaderStage.Compute);


		if (!string.IsNullOrEmpty(
				compileError))
		{
			throw new InvalidOperationException(
				$"FFT shader compilation failed:\n" +
				compileError);
		}


		_shader =
			_rd.ShaderCreateFromSpirV(
				spirV);


		if (!_shader.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create FFT shader RID.");
		}


		_pipeline =
			_rd.ComputePipelineCreate(
				_shader);


		if (!_pipeline.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create FFT compute pipeline.");
		}
	}


	private void CreateUniformSets()
	{
		// -------------------------------------------------------------
		// X transform
		//
		// Spectrum H/X/Z
		//       ->
		// Temp H/X/Z
		// -------------------------------------------------------------

		_xUniformSet =
			CreateUniformSet(
				inputH:
					_resources.SpectrumHeight,

				inputX:
					_resources.SpectrumDisplaceX,

				inputZ:
					_resources.SpectrumDisplaceZ,

				outputH:
					_resources.FftTempHeight,

				outputX:
					_resources.FftTempX,

				outputZ:
					_resources.FftTempZ);


		// -------------------------------------------------------------
		// Y transform
		//
		// Temp H/X/Z
		//       ->
		// final raw FFT displacement
		//
		// outputH/X/Z are not used when axis == 1.
		// They still need valid descriptors because this is one shader.
		//
		// Spectrum textures are safe dummy bindings here and are distinct
		// from the temp input textures.
		// -------------------------------------------------------------

		_yUniformSet =
			CreateUniformSet(
				inputH:
					_resources.FftTempHeight,

				inputX:
					_resources.FftTempX,

				inputZ:
					_resources.FftTempZ,

				outputH:
					_resources.SpectrumHeight,

				outputX:
					_resources.SpectrumDisplaceX,

				outputZ:
					_resources.SpectrumDisplaceZ);


		if (!_xUniformSet.IsValid ||
			!_yUniformSet.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create FFT uniform sets.");
		}

		GD.Print("[Ocean] IFFT pipeline, sampler and uniform sets valid");
	}


	private Rid CreateUniformSet(
		Rid inputH,
		Rid inputX,
		Rid inputZ,
		Rid outputH,
		Rid outputX,
		Rid outputZ)
	{
		var uniforms =
			new Array<RDUniform>
			{
				CreateSampledTextureUniform(
					0,
					inputH),

				CreateSampledTextureUniform(
					1,
					inputX),

				CreateSampledTextureUniform(
					2,
					inputZ),

				CreateSampledTextureUniform(
					3,
					_resources.Butterfly),

				CreateImageUniform(
					4,
					outputH),

				CreateImageUniform(
					5,
					outputX),

				CreateImageUniform(
					6,
					outputZ),

				CreateImageUniform(
					7,
					_resources.Displacement),
			};


		return _rd.UniformSetCreate(
			uniforms,
			_shader,
			0);
	}


	private RDUniform CreateSampledTextureUniform(
		int binding,
		Rid texture)
	{
		var uniform =
			new RDUniform
			{
				UniformType =
					RenderingDevice.UniformType
						.SamplerWithTexture,

				Binding = binding,
			};

		uniform.AddId(
			_sampler);

		uniform.AddId(
			texture);

		return uniform;
	}


	private static RDUniform CreateImageUniform(
		int binding,
		Rid texture)
	{
		var uniform =
			new RDUniform
			{
				UniformType =
					RenderingDevice.UniformType.Image,

				Binding = binding,
			};

		uniform.AddId(
			texture);

		return uniform;
	}


	/// <summary>
	/// Executes both dimensions of the inverse FFT.
	///
	/// Exactly two compute dispatches.
	/// No resource allocation.
	/// No CPU readback.
	/// </summary>
	public void Dispatch()
	{
		long list =
			_rd.ComputeListBegin();


		_rd.ComputeListBindComputePipeline(
			list,
			_pipeline);


		// -------------------------------------------------------------
		// X axis.
		// -------------------------------------------------------------

		_rd.ComputeListBindUniformSet(
			list,
			_xUniformSet,
			0);


		byte[] xPushConstants =
			BuildPushConstants(
				axis: 0);


		_rd.ComputeListSetPushConstant(
			list,
			xPushConstants,
			(uint)xPushConstants.Length);


		_rd.ComputeListDispatch(
			list,
			(uint)_resources.Resolution,
			1,
			(uint)_resources.CascadeCount);

		if (!_dispatchLogged)
		{
			GD.Print("[Ocean] IFFT X dispatched");
		}


		// X wrote Temp textures.
		// Y will immediately read them.
		//
		// Explicit compute barrier is required between these dependent
		// dispatches.
		_rd.ComputeListAddBarrier(
			list);

		if (!_dispatchLogged)
		{
			GD.Print("[Ocean] IFFT barrier issued");
		}


		// -------------------------------------------------------------
		// Y axis.
		// -------------------------------------------------------------

		_rd.ComputeListBindUniformSet(
			list,
			_yUniformSet,
			0);


		byte[] yPushConstants =
			BuildPushConstants(
				axis: 1);


		_rd.ComputeListSetPushConstant(
			list,
			yPushConstants,
			(uint)yPushConstants.Length);


		_rd.ComputeListDispatch(
			list,
			(uint)_resources.Resolution,
			1,
			(uint)_resources.CascadeCount);

		if (!_dispatchLogged)
		{
			GD.Print("[Ocean] IFFT Y dispatched");
			_dispatchLogged = true;
		}


		_rd.ComputeListEnd();
	}


	private byte[] BuildPushConstants(
		uint axis)
	{
		// uvec4 = 16 bytes.
		uint[] values =
		{
			(uint)_resources.Resolution,
			(uint)_resources.CascadeCount,
			(uint)_resources.FftPassCount,
			axis,
		};


		var bytes =
			new byte[16];


		Buffer.BlockCopy(
			values,
			0,
			bytes,
			0,
			bytes.Length);


		return bytes;
	}


	public void Dispose()
	{
		Free(_yUniformSet);
		_yUniformSet = default;

		Free(_xUniformSet);
		_xUniformSet = default;

		Free(_pipeline);
		_pipeline = default;

		Free(_shader);
		_shader = default;

		Free(_sampler);
		_sampler = default;
	}


	private void Free(Rid rid)
	{
		if (rid.IsValid)
		{
			_rd.FreeRid(rid);
		}
	}
}
