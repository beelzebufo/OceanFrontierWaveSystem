using System;
using Godot;

namespace OceanFrontier.Water.Waves.AnimatedWaves;

/// <summary>
/// Derives normal.xyz and raw horizontal Jacobian from the completely
/// composed canonical AnimatedWaveField.
/// </summary>
internal sealed class AnimatedWaveDerivativePass : IDisposable
{
	private const int LocalSizeX = 8;
	private const int LocalSizeY = 8;

	private readonly RenderingDevice _rd;
	private readonly int _resolution;
	private readonly int _lodCount;

	private Rid _shader;
	private Rid _pipeline;
	private Rid _uniformSet;
	private bool _firstDispatchLogged;

	public long DispatchCount { get; private set; }

	public AnimatedWaveDerivativePass(
		RenderingDevice rd,
		Rid animatedWaveField,
		Rid lodBuffer,
		Rid derivativeField,
		int resolution,
		int lodCount)
	{
		_rd = rd ?? throw new ArgumentNullException(nameof(rd));

		if (!animatedWaveField.IsValid)
		{
			throw new ArgumentException(
				"AnimatedWaveField RID is invalid.", nameof(animatedWaveField));
		}

		if (!lodBuffer.IsValid)
		{
			throw new ArgumentException(
				"Animated Wave LOD buffer RID is invalid.", nameof(lodBuffer));
		}

		if (!derivativeField.IsValid)
		{
			throw new ArgumentException(
				"AnimatedWaveDerivativeField RID is invalid.", nameof(derivativeField));
		}

		if (resolution <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(resolution));
		}

		if (lodCount <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(lodCount));
		}

		_resolution = resolution;
		_lodCount = lodCount;

		try
		{
			Create(animatedWaveField, lodBuffer, derivativeField);
		}
		catch
		{
			Dispose();
			throw;
		}
	}

	private void Create(
		Rid animatedWaveField,
		Rid lodBuffer,
		Rid derivativeField)
	{
		RDShaderFile shaderFile = GD.Load<RDShaderFile>(
			"res://Ocean/Shaders/Waves/animated_wave_derivatives.glsl");

		if (shaderFile == null)
		{
			throw new InvalidOperationException(
				"Failed to load animated_wave_derivatives.glsl.");
		}

		RDShaderSpirV spirv = shaderFile.GetSpirV();
		string compileError = spirv.GetStageCompileError(
			RenderingDevice.ShaderStage.Compute);

		if (!string.IsNullOrEmpty(compileError))
		{
			throw new InvalidOperationException(
				"Animated Wave derivative shader compilation failed:\n" + compileError);
		}

		if (spirv.GetStageBytecode(RenderingDevice.ShaderStage.Compute).Length == 0)
		{
			throw new InvalidOperationException(
				"Animated Wave derivative shader has no compute bytecode. " +
				"Reimport animated_wave_derivatives.glsl in Godot.");
		}

		_shader = _rd.ShaderCreateFromSpirV(spirv);
		if (!_shader.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create Animated Wave derivative shader.");
		}

		_pipeline = _rd.ComputePipelineCreate(_shader);
		if (!_pipeline.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create Animated Wave derivative pipeline.");
		}

		var sourceUniform = new RDUniform
		{
			UniformType = RenderingDevice.UniformType.Image,
			Binding = 0,
		};
		sourceUniform.AddId(animatedWaveField);

		var lodUniform = new RDUniform
		{
			UniformType = RenderingDevice.UniformType.StorageBuffer,
			Binding = 1,
		};
		lodUniform.AddId(lodBuffer);

		var outputUniform = new RDUniform
		{
			UniformType = RenderingDevice.UniformType.Image,
			Binding = 2,
		};
		outputUniform.AddId(derivativeField);

		var uniforms = new Godot.Collections.Array<RDUniform>
		{
			sourceUniform,
			lodUniform,
			outputUniform,
		};

		_uniformSet = _rd.UniformSetCreate(uniforms, _shader, 0);
		if (!_uniformSet.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create Animated Wave derivative uniform set.");
		}
	}

	/// <summary>
	/// Must run immediately after the last canonical field writer.
	/// </summary>
	public void Dispatch()
	{
		uint groupsX = (uint)((_resolution + LocalSizeX - 1) / LocalSizeX);
		uint groupsY = (uint)((_resolution + LocalSizeY - 1) / LocalSizeY);

		long computeList = _rd.ComputeListBegin();

		// Make preceding final-field writes visible to this read pass.
		_rd.ComputeListAddBarrier(computeList);
		_rd.ComputeListBindComputePipeline(computeList, _pipeline);
		_rd.ComputeListBindUniformSet(computeList, _uniformSet, 0);
		_rd.ComputeListDispatch(computeList, groupsX, groupsY, (uint)_lodCount);

		// Publish derivative image writes before later draw/compute consumers.
		_rd.ComputeListAddBarrier(computeList);
		_rd.ComputeListEnd();

		DispatchCount++;

		if (!_firstDispatchLogged)
		{
			_firstDispatchLogged = true;
			GD.Print(
				$"[Ocean] Animated Wave derivative dispatch: " +
				$"{groupsX}x{groupsY}x{_lodCount} workgroups cover " +
				$"{_resolution}x{_resolution}x{_lodCount} texels; " +
				"runs after final composition.");
		}
	}

	public void Dispose()
	{
		if (_uniformSet.IsValid)
		{
			_rd.FreeRid(_uniformSet);
			_uniformSet = default;
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
