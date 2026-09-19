using System;
using Godot;
using Godot.Collections;

namespace OceanFrontier.Water.Waves.FFT;

internal readonly struct FftSpectrumInitSettings
{
	public readonly int Resolution;
	public readonly int CascadeCount;
	public readonly int BandCount;

	public readonly float WindSpeed;
	public readonly float WindTurbulence;
	public readonly float Gravity;
	public readonly float LoopPeriod;

	public readonly float WindDirectionX;
	public readonly float WindDirectionY;

	public readonly float SmallestWavelengthPowerOfTwo;

	public FftSpectrumInitSettings(
		int resolution,
		int cascadeCount,
		int bandCount,
		float windSpeed,
		float windTurbulence,
		float gravity,
		float loopPeriod,
		float windDirectionX,
		float windDirectionY,
		float smallestWavelengthPowerOfTwo)
	{
		Resolution = resolution;
		CascadeCount = cascadeCount;
		BandCount = bandCount;

		WindSpeed = windSpeed;
		WindTurbulence = windTurbulence;
		Gravity = gravity;
		LoopPeriod = loopPeriod;

		WindDirectionX = windDirectionX;
		WindDirectionY = windDirectionY;

		SmallestWavelengthPowerOfTwo =
			smallestWavelengthPowerOfTwo;
	}
}

/// <summary>
/// Generates H0(k) / H0(-k) into SpectrumInitial.
///
/// Must only be used from the rendering thread.
/// </summary>
internal sealed class FftSpectrumInitPass : IDisposable
{
	private const string ShaderPath =
		"res://Ocean/Shaders/Waves/fft_spectrum_init.glsl";

	private const int LocalSize = 8;

	private readonly RenderingDevice _rd;
	private readonly FftGpuResources _resources;
	private readonly int _bandCount;

	private Rid _controlsBuffer;
	private Rid _shader;
	private Rid _pipeline;
	private Rid _uniformSet;

	public FftSpectrumInitPass(
		RenderingDevice rd,
		FftGpuResources resources,
		int bandCount)
	{
		_rd = rd ?? throw new ArgumentNullException(nameof(rd));
		_resources = resources
			?? throw new ArgumentNullException(nameof(resources));

		if (bandCount <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(bandCount));
		}

		_bandCount = bandCount;

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

	private void Create()
	{
		uint controlsSizeBytes =
			checked((uint)(_bandCount * sizeof(float)));

		_controlsBuffer = _rd.StorageBufferCreate(
			controlsSizeBytes);

		if (!_controlsBuffer.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create FFT spectrum controls buffer.");
		}

		RDShaderFile shaderFile =
			GD.Load<RDShaderFile>(ShaderPath);

		if (shaderFile == null)
		{
			throw new InvalidOperationException(
				$"Could not load compute shader: {ShaderPath}");
		}

		RDShaderSpirV spirV = shaderFile.GetSpirV();

		string compileError =
			spirV.GetStageCompileError(
				RenderingDevice.ShaderStage.Compute);

		if (!string.IsNullOrEmpty(compileError))
		{
			throw new InvalidOperationException(
				$"FFT spectrum init shader compilation failed:\n" +
				compileError);
		}

		_shader = _rd.ShaderCreateFromSpirV(
			spirV,
			"Ocean FFT Spectrum Init");

		if (!_shader.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create FFT spectrum init shader.");
		}

		_pipeline = _rd.ComputePipelineCreate(_shader);

		if (!_pipeline.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create FFT spectrum init pipeline.");
		}

		var controlsUniform = new RDUniform
		{
			UniformType =
				RenderingDevice.UniformType.StorageBuffer,
			Binding = 0,
		};

		controlsUniform.AddId(_controlsBuffer);

		var outputUniform = new RDUniform
		{
			UniformType =
				RenderingDevice.UniformType.Image,
			Binding = 1,
		};

		outputUniform.AddId(_resources.SpectrumInitial);

		var uniforms = new Array<RDUniform>
		{
			controlsUniform,
			outputUniform,
		};

		_uniformSet = _rd.UniformSetCreate(
			uniforms,
			_shader,
			0);

		if (!_uniformSet.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create FFT spectrum init uniform set.");
		}

		GD.Print("[Ocean] SpectrumInit pipeline and uniform set valid");
	}

	public void Dispatch(
		in FftSpectrumInitSettings settings,
		float[] linearPowerControls)
	{
		if (linearPowerControls == null)
		{
			throw new ArgumentNullException(
				nameof(linearPowerControls));
		}

		if (linearPowerControls.Length != _bandCount)
		{
			throw new ArgumentException(
				$"Expected {_bandCount} spectrum controls, " +
				$"received {linearPowerControls.Length}.",
				nameof(linearPowerControls));
		}

		if (settings.Resolution != _resources.Resolution ||
			settings.CascadeCount != _resources.CascadeCount ||
			settings.BandCount != _bandCount)
		{
			throw new InvalidOperationException(
				"Spectrum init settings do not match GPU resources.");
		}

		UploadControls(linearPowerControls);

		byte[] pushConstants =
			BuildPushConstants(settings);

		uint groupsX =
			(uint)((settings.Resolution + LocalSize - 1) /
				   LocalSize);

		uint groupsY =
			(uint)((settings.Resolution + LocalSize - 1) /
				   LocalSize);

		long computeList = _rd.ComputeListBegin();

		_rd.ComputeListBindComputePipeline(
			computeList,
			_pipeline);

		_rd.ComputeListBindUniformSet(
			computeList,
			_uniformSet,
			0);

		_rd.ComputeListSetPushConstant(
			computeList,
			pushConstants,
			(uint)pushConstants.Length);

		_rd.ComputeListDispatch(
			computeList,
			groupsX,
			groupsY,
			(uint)settings.CascadeCount);

		_rd.ComputeListEnd();
	}

	private void UploadControls(float[] controls)
	{
		byte[] bytes =
			new byte[controls.Length * sizeof(float)];

		Buffer.BlockCopy(
			controls,
			0,
			bytes,
			0,
			bytes.Length);

		Error error = _rd.BufferUpdate(
			_controlsBuffer,
			0,
			(uint)bytes.Length,
			bytes);

		if (error != Error.Ok)
		{
			throw new InvalidOperationException(
				$"Failed to upload spectrum controls: {error}");
		}
	}

	private static byte[] BuildPushConstants(
		in FftSpectrumInitSettings settings)
	{
		// GLSL layout:
		//
		// uvec4:
		//   resolution
		//   cascadeCount
		//   bandCount
		//   reserved
		//
		// vec4:
		//   windSpeed
		//   turbulence
		//   gravity
		//   loopPeriod
		//
		// vec4:
		//   windDirectionX
		//   windDirectionY
		//   smallestWavelengthPowerOfTwo
		//   reserved
		//
		// Total: 48 bytes.

		var data = new byte[48];

		uint[] ints =
		{
			(uint)settings.Resolution,
			(uint)settings.CascadeCount,
			(uint)settings.BandCount,
			0u,
		};

		float[] floats =
		{
			settings.WindSpeed,
			settings.WindTurbulence,
			settings.Gravity,
			settings.LoopPeriod,

			settings.WindDirectionX,
			settings.WindDirectionY,
			settings.SmallestWavelengthPowerOfTwo,
			0.0f,
		};

		Buffer.BlockCopy(
			ints,
			0,
			data,
			0,
			16);

		Buffer.BlockCopy(
			floats,
			0,
			data,
			16,
			32);

		return data;
	}

	public void Dispose()
	{
		Free(ref _uniformSet);
		Free(ref _pipeline);
		Free(ref _shader);
		Free(ref _controlsBuffer);
	}

	private void Free(ref Rid rid)
	{
		if (!rid.IsValid)
		{
			return;
		}

		_rd.FreeRid(rid);
		rid = default;
	}
}
