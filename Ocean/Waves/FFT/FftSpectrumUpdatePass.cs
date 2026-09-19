using System;
using Godot;
using Godot.Collections;

namespace OceanFrontier.Water.Waves.FFT;

/// <summary>
/// Evolves H0 into H(k,t) and builds horizontal displacement spectra.
///
/// Must run on the render thread.
/// </summary>
internal sealed class FftSpectrumUpdatePass : IDisposable
{
	private const string ShaderPath =
		"res://Ocean/Shaders/Waves/fft_spectrum_update.glsl";

	private const int LocalSize = 8;

	private readonly RenderingDevice _rd;
	private readonly FftGpuResources _resources;

	private Rid _shader;
	private Rid _pipeline;
	private Rid _uniformSet;
	private bool _dispatchLogged;

	public FftSpectrumUpdatePass(
		RenderingDevice rd,
		FftGpuResources resources)
	{
		_rd = rd ??
			throw new ArgumentNullException(nameof(rd));

		_resources = resources ??
			throw new ArgumentNullException(nameof(resources));

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
		RDShaderFile shaderFile =
			GD.Load<RDShaderFile>(ShaderPath);

		if (shaderFile == null)
		{
			throw new InvalidOperationException(
				$"Could not load compute shader: {ShaderPath}");
		}

		RDShaderSpirV spirV =
			shaderFile.GetSpirV();

		string compileError =
			spirV.GetStageCompileError(
				RenderingDevice.ShaderStage.Compute);

		if (!string.IsNullOrEmpty(compileError))
		{
			throw new InvalidOperationException(
				$"FFT spectrum update shader compilation failed:\n" +
				compileError);
		}

		_shader =
			_rd.ShaderCreateFromSpirV(spirV);

		if (!_shader.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create FFT spectrum update shader.");
		}

		_pipeline =
			_rd.ComputePipelineCreate(_shader);

		if (!_pipeline.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create FFT spectrum update pipeline.");
		}

		var inputInitial =
			CreateImageUniform(
				0,
				_resources.SpectrumInitial);

		var outputHeight =
			CreateImageUniform(
				1,
				_resources.SpectrumHeight);

		var outputX =
			CreateImageUniform(
				2,
				_resources.SpectrumDisplaceX);

		var outputZ =
			CreateImageUniform(
				3,
				_resources.SpectrumDisplaceZ);

		var uniforms =
			new Array<RDUniform>
			{
				inputInitial,
				outputHeight,
				outputX,
				outputZ,
			};

		_uniformSet =
			_rd.UniformSetCreate(
				uniforms,
				_shader,
				0);

		if (!_uniformSet.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create FFT spectrum update uniform set.");
		}

		GD.Print("[Ocean] SpectrumUpdate pipeline and uniform set valid");
	}

	public void Dispatch(
		float simulationTime,
		float chop,
		float gravity,
		float loopPeriod)
	{
		byte[] pushConstants =
			BuildPushConstants(
				simulationTime,
				chop,
				gravity,
				loopPeriod);

		uint groupsX =
			(uint)(
				(_resources.Resolution +
				 LocalSize - 1)
				/ LocalSize);

		uint groupsY = groupsX;

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
			pushConstants,
			(uint)pushConstants.Length);

		_rd.ComputeListDispatch(
			computeList,
			groupsX,
			groupsY,
			(uint)_resources.CascadeCount);

		_rd.ComputeListEnd();

		if (!_dispatchLogged)
		{
			_dispatchLogged = true;
			GD.Print("[Ocean] SpectrumUpdate first dispatch recorded");
		}
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

		uniform.AddId(texture);

		return uniform;
	}

	private byte[] BuildPushConstants(
		float simulationTime,
		float chop,
		float gravity,
		float loopPeriod)
	{
		// 32 bytes:
		//
		// uvec4 dimensions
		// vec4 simulation

		var bytes =
			new byte[32];

		uint[] dimensions =
		{
			(uint)_resources.Resolution,
			(uint)_resources.CascadeCount,
			0u,
			0u,
		};

		float[] simulation =
		{
			simulationTime,
			chop,
			gravity,
			loopPeriod,
		};

		Buffer.BlockCopy(
			dimensions,
			0,
			bytes,
			0,
			16);

		Buffer.BlockCopy(
			simulation,
			0,
			bytes,
			16,
			16);

		return bytes;
	}

	public void Dispose()
	{
		Free(ref _uniformSet);
		Free(ref _pipeline);
		Free(ref _shader);
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
