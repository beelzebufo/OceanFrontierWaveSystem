using System;
using Godot;
using OceanFrontier.Water.Waves.AnimatedWaves;

namespace OceanFrontier.Water.Waves.Foam;

/// <summary>
/// Persistent foam reprojection, decay and whitecap generation pass.
///
/// Reprojection/substep behavior follows Crest 4 LodDataMgrPersistent and
/// UpdateFoam.compute at wave-harmonic/crest
/// db0658ff0b2e93e4a9e28cc2867509658b0ecc00 (MIT).
///
/// The previous LOD buffer is the exact layout of the last committed foam
/// generation. The current LOD buffer is borrowed from AnimatedWaveComposer,
/// so foam never owns or recalculates a second spatial hierarchy.
/// </summary>
internal sealed class OceanFoamSimulationPass : IDisposable
{
	private const int LocalSize = 8;
	private const int PushConstantBytes = 48;

	private readonly RenderingDevice _rd;
	private readonly OceanFoamField _field;
	private readonly AnimatedWaveLodGpuBuffer _previousLodBuffer;
	private readonly byte[] _pushBytes = new byte[PushConstantBytes];

	private Rid _shader;
	private Rid _pipeline;
	private readonly Rid[] _uniformSets = new Rid[2];

	private bool _hasSimulationTime;
	private float _lastSimulationTime;
	private double _timeToSimulate;
	private float _previousWorldScale;
	private bool _diagnosticWasEnabled;
	private bool _firstDispatchLogged;

	public long DispatchCount { get; private set; }
	public int LastUpdateSubstepCount { get; private set; }

	public OceanFoamSimulationPass(
		RenderingDevice rd,
		OceanFoamField field,
		Rid currentLodBuffer,
		Rid animatedWaveDerivativeField,
		AnimatedWaveLodLayout initialLayout)
	{
		_rd = rd ?? throw new ArgumentNullException(nameof(rd));
		_field = field ?? throw new ArgumentNullException(nameof(field));

		if (!currentLodBuffer.IsValid)
		{
			throw new ArgumentException(
				"Current Animated Wave LOD buffer is invalid.",
				nameof(currentLodBuffer));
		}

		if (!animatedWaveDerivativeField.IsValid)
		{
			throw new ArgumentException(
				"Animated Wave derivative field is invalid.",
				nameof(animatedWaveDerivativeField));
		}

		if (initialLayout == null)
		{
			throw new ArgumentNullException(nameof(initialLayout));
		}

		_previousWorldScale = initialLayout.WorldScale;
		_previousLodBuffer = new AnimatedWaveLodGpuBuffer(rd, initialLayout);

		try
		{
			Create(
				currentLodBuffer,
				animatedWaveDerivativeField);
		}
		catch
		{
			Dispose();
			throw;
		}
	}

	private void Create(
		Rid currentLodBuffer,
		Rid animatedWaveDerivativeField)
	{
		RDShaderFile shaderFile = GD.Load<RDShaderFile>(
			"res://Ocean/Shaders/Waves/ocean_foam_update.glsl")
			?? throw new InvalidOperationException(
				"Failed to load ocean_foam_update.glsl.");

		RDShaderSpirV spirv = shaderFile.GetSpirV();
		string compileError = spirv.GetStageCompileError(
			RenderingDevice.ShaderStage.Compute);

		if (!string.IsNullOrEmpty(compileError))
		{
			throw new InvalidOperationException(
				"Foam-1A shader compilation failed:\n" + compileError);
		}

		if (spirv.GetStageBytecode(
				RenderingDevice.ShaderStage.Compute).Length == 0)
		{
			throw new InvalidOperationException(
				"Foam-1A shader has no compute bytecode. " +
				"Reimport ocean_foam_update.glsl in Godot.");
		}

		_shader = _rd.ShaderCreateFromSpirV(spirv);
		_pipeline = _rd.ComputePipelineCreate(_shader);

		_uniformSets[0] = CreateUniformSet(
			_field.Texture0,
			_field.Texture1,
			currentLodBuffer,
			animatedWaveDerivativeField);

		_uniformSets[1] = CreateUniformSet(
			_field.Texture1,
			_field.Texture0,
			currentLodBuffer,
			animatedWaveDerivativeField);

		if (!_shader.IsValid ||
			!_pipeline.IsValid ||
			!_uniformSets[0].IsValid ||
			!_uniformSets[1].IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create Foam-1A GPU resources.");
		}
	}

	private Rid CreateUniformSet(
		Rid source,
		Rid target,
		Rid currentLodBuffer,
		Rid animatedWaveDerivativeField)
	{
		var sourceUniform = new RDUniform
		{
			UniformType = RenderingDevice.UniformType.Image,
			Binding = 0,
		};
		sourceUniform.AddId(source);

		var targetUniform = new RDUniform
		{
			UniformType = RenderingDevice.UniformType.Image,
			Binding = 1,
		};
		targetUniform.AddId(target);

		var currentLodUniform = new RDUniform
		{
			UniformType = RenderingDevice.UniformType.StorageBuffer,
			Binding = 2,
		};
		currentLodUniform.AddId(currentLodBuffer);

		var previousLodUniform = new RDUniform
		{
			UniformType = RenderingDevice.UniformType.StorageBuffer,
			Binding = 3,
		};
		previousLodUniform.AddId(_previousLodBuffer.Buffer);

		var derivativeUniform = new RDUniform
		{
			UniformType = RenderingDevice.UniformType.Image,
			Binding = 4,
		};
		derivativeUniform.AddId(animatedWaveDerivativeField);

		return _rd.UniformSetCreate(
			new Godot.Collections.Array<RDUniform>
			{
				sourceUniform,
				targetUniform,
				currentLodUniform,
				previousLodUniform,
				derivativeUniform,
			},
			_shader,
			0);
	}

	/// <summary>
	/// Runs after AnimatedWaveComposer has committed the current spatial
	/// layout. simulationTime is ocean simulation time, not wall time.
	/// </summary>
	public void Update(
		float simulationTime,
		AnimatedWaveLodLayout currentLayout,
		OceanFoamState settings)
	{
		if (currentLayout == null)
		{
			throw new ArgumentNullException(nameof(currentLayout));
		}

		float frameSimulationTime = 0.0f;
		if (_hasSimulationTime &&
			float.IsFinite(simulationTime) &&
			simulationTime >= _lastSimulationTime)
		{
			frameSimulationTime = simulationTime - _lastSimulationTime;
		}

		_lastSimulationTime = float.IsFinite(simulationTime)
			? simulationTime
			: _lastSimulationTime;
		_hasSimulationTime = true;

		if (!settings.Enabled)
		{
			LastUpdateSubstepCount = 0;
			_timeToSimulate = 0.0;
			// Enabling the simulation while the diagnostic switch is already on
			// should still produce exactly one validation spot.
			_diagnosticWasEnabled = false;
			return;
		}

		_timeToSimulate += frameSimulationTime;

		float substepDt = 1.0f / settings.SimulationFrequency;
		int physicalSubsteps = (int)Math.Floor(
			_timeToSimulate * settings.SimulationFrequency + 1e-6);

		LastUpdateSubstepCount = physicalSubsteps;

		// Crest persistent-sim contract: even when simulation time has not
		// advanced, run one dt=0 reprojection so camera-relative grids cannot
		// drag the field through world space.
		int dispatches = Math.Max(1, physicalSubsteps);
		bool inject =
			settings.InjectWorldSpaceSpot &&
			!_diagnosticWasEnabled;

		int sourceLodOffset = CalculateSourceLodOffset(
			currentLayout.WorldScale,
			_previousWorldScale);

		for (int step = 0; step < dispatches; step++)
		{
			float dt = physicalSubsteps > 0
				? substepDt
				: 0.0f;

			Dispatch(
				dt,
				settings.FadeRate,
				settings.WaveFoamStrength,
				settings.WaveFoamCoverage,
				step == 0 ? sourceLodOffset : 0,
				inject && step == 0,
				settings.DiagnosticSpotWorldXZ,
				settings.DiagnosticSpotRadius,
				settings.DiagnosticSpotAmount);

			_field.CommitWrite();

			if (step == 0)
			{
				// From this point, the newly written texture belongs to currentLayout.
				// Subsequent substeps must therefore sample it with current metadata.
				_previousLodBuffer.Upload(currentLayout);
				_previousWorldScale = currentLayout.WorldScale;
			}

			if (physicalSubsteps > 0)
			{
				_timeToSimulate = Math.Max(0.0, _timeToSimulate - substepDt);
			}
		}

		_diagnosticWasEnabled = settings.InjectWorldSpaceSpot;
	}

	private void Dispatch(
		float dt,
		float fadeRate,
		float waveFoamStrength,
		float waveFoamCoverage,
		int sourceLodOffset,
		bool inject,
		Vector2 injectionWorldXZ,
		float injectionRadius,
		float injectionAmount)
	{
		WriteUInt(0, (uint)_field.Resolution);
		WriteUInt(4, (uint)_field.LodCount);
		WriteInt(8, sourceLodOffset);
		WriteUInt(12, inject ? 1u : 0u);
		WriteFloat(16, dt);
		WriteFloat(20, fadeRate);
		WriteFloat(24, injectionRadius);
		WriteFloat(28, injectionAmount);
		WriteFloat(32, injectionWorldXZ.X);
		WriteFloat(36, injectionWorldXZ.Y);
		WriteFloat(40, waveFoamStrength);
		WriteFloat(44, waveFoamCoverage);

		int sourceIndex = _field.Latest == _field.Texture0 ? 0 : 1;
		uint groups = (uint)((_field.Resolution + LocalSize - 1) / LocalSize);

		long list = _rd.ComputeListBegin();
		_rd.ComputeListBindComputePipeline(list, _pipeline);
		_rd.ComputeListBindUniformSet(list, _uniformSets[sourceIndex], 0);
		_rd.ComputeListSetPushConstant(list, _pushBytes, PushConstantBytes);
		_rd.ComputeListDispatch(list, groups, groups, (uint)_field.LodCount);
		_rd.ComputeListEnd();

		DispatchCount++;

		if (!_firstDispatchLogged)
		{
			_firstDispatchLogged = true;
			GD.Print(
				$"[Ocean] Foam dispatch: {groups}x{groups}x" +
				$"{_field.LodCount} workgroups; persistent reprojection " +
				"and derivative-driven whitecaps active.");
		}
	}

	private static int CalculateSourceLodOffset(
		float currentWorldScale,
		float previousWorldScale)
	{
		if (currentWorldScale <= 0.0f || previousWorldScale <= 0.0f)
		{
			return 0;
		}

		// Spatial LOD sizes are powers of two. At a whole-stack x2 change,
		// current LOD N matches the previous hierarchy at N + 1.
		return (int)MathF.Round(
			MathF.Log2(currentWorldScale / previousWorldScale));
	}

	private void WriteUInt(int offset, uint value) =>
		BitConverter.TryWriteBytes(_pushBytes.AsSpan(offset, sizeof(uint)), value);

	private void WriteInt(int offset, int value) =>
		BitConverter.TryWriteBytes(_pushBytes.AsSpan(offset, sizeof(int)), value);

	private void WriteFloat(int offset, float value) =>
		BitConverter.TryWriteBytes(_pushBytes.AsSpan(offset, sizeof(float)), value);

	public void Dispose()
	{
		for (int index = 0; index < _uniformSets.Length; index++)
		{
			if (_uniformSets[index].IsValid)
			{
				_rd.FreeRid(_uniformSets[index]);
				_uniformSets[index] = default;
			}
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

		_previousLodBuffer?.Dispose();
	}
}
