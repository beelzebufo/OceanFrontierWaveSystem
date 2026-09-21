using System;
using Godot;

namespace OceanFrontier.Water.Waves.AnimatedWaves;

/// <summary>
/// Coarse-to-fine Animated Waves combine pass.
///
/// Crest equivalent:
///
///     LodDataMgrAnimWaves.CombinePassCompute()
///         -> ShapeCombine.compute
///
/// Input:
///
///     AnimatedWaveDirectField
///
/// Output:
///
///     AnimatedWaveField
///
/// Contract:
///
///     Final[last] = Direct[last]
///
///     Final[L] =
///         Direct[L] +
///         Resample(Final[L + 1])
///
/// Dispatch order is strictly:
///
///     last
///     last - 1
///     ...
///     0
///
/// A compute barrier is inserted between dependent LOD dispatches so
/// Final[L + 1] is visible before Final[L] reads it.
///
/// AnimatedWaveField remains the canonical final wave field.
/// </summary>
internal sealed class AnimatedWaveCombinePass : IDisposable
{
	private const int LocalSizeX = 8;
	private const int LocalSizeY = 8;

	private const int PushConstantBytes = 16;


	private readonly RenderingDevice _rd;

	private readonly int _resolution;
	private readonly int _lodCount;


	private Rid _shader;
	private Rid _pipeline;
	private Rid _uniformSet;


	private readonly byte[] _pushBytes =
		new byte[PushConstantBytes];


	private bool _firstDispatchLogged;


	public AnimatedWaveCombinePass(
		RenderingDevice rd,
		Rid directWaveField,
		Rid lodBuffer,
		Rid animatedWaveField,
		int resolution,
		int lodCount)
	{
		_rd =
			rd ??
			throw new ArgumentNullException(
				nameof(rd));


		if (!directWaveField.IsValid)
		{
			throw new ArgumentException(
				"Animated Wave direct-field RID is invalid.",
				nameof(directWaveField));
		}


		if (!lodBuffer.IsValid)
		{
			throw new ArgumentException(
				"Animated Wave LOD buffer RID is invalid.",
				nameof(lodBuffer));
		}


		if (!animatedWaveField.IsValid)
		{
			throw new ArgumentException(
				"AnimatedWaveField RID is invalid.",
				nameof(animatedWaveField));
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


		_resolution =
			resolution;

		_lodCount =
			lodCount;


		try
		{
			Create(
				directWaveField,
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
		Rid directWaveField,
		Rid lodBuffer,
		Rid animatedWaveField)
	{
		RDShaderFile shaderFile =
			GD.Load<RDShaderFile>(
				"res://Ocean/Shaders/Waves/animated_wave_combine.glsl");


		if (shaderFile == null)
		{
			throw new InvalidOperationException(
				"Failed to load animated_wave_combine.glsl.");
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
				"Animated Wave combine shader compilation failed:\n" +
				compileError);
		}


		if (spirv.GetStageBytecode(
				RenderingDevice.ShaderStage.Compute)
			.Length == 0)
		{
			throw new InvalidOperationException(
				"Animated Wave combine shader has no compute bytecode. " +
				"Reimport animated_wave_combine.glsl in Godot.");
		}


		_shader =
			_rd.ShaderCreateFromSpirV(
				spirv);


		if (!_shader.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create Animated Wave combine shader.");
		}


		_pipeline =
			_rd.ComputePipelineCreate(
				_shader);


		if (!_pipeline.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create Animated Wave combine pipeline.");
		}


		//
		// binding 0:
		//
		// Direct per-LOD wave contributions.
		//
		// Crest equivalent:
		//
		//     _LD_TexArray_WaveBuffer
		//

		var directUniform =
			new RDUniform
			{
				UniformType =
					RenderingDevice
						.UniformType
						.Image,

				Binding =
					0,
			};


		directUniform.AddId(
			directWaveField);


		//
		// binding 1:
		//
		// Spatial Animated Wave LOD metadata.
		//
		// Crest equivalent:
		//
		//     _CrestCascadeData
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
		// Canonical cumulative AnimatedWaveField.
		//
		// The same image is written at the current LOD and read
		// from the already-completed next/coarser LOD.
		//
		// This mirrors Crest ShapeCombine.compute.
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
			animatedWaveField);


		var uniforms =
			new Godot.Collections.Array<RDUniform>
			{
				directUniform,
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
				"Failed to create Animated Wave combine uniform set.");
		}


		//
		// Static push constants:
		//
		//  0 : uint resolution
		//  4 : uint lod_count
		//  8 : uint current_lod
		// 12 : uint padding
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


		GD.Print(
			"[Ocean] Animated Wave coarse-to-fine combine pipeline and uniforms valid.");
	}


	/// <summary>
	/// Builds the cumulative canonical AnimatedWaveField.
	///
	/// Must run after every direct-input pass that modifies
	/// AnimatedWaveDirectField.
	///
	/// Dispatch dependency:
	///
	///     Final[L + 1]
	///         must be complete and visible
	///         before Final[L] starts.
	/// </summary>
	public void Dispatch()
	{
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
		// Keep the complete ShapeCombine chain in one compute list.
		//
		// Godot's ComputeListAddBarrier() raises a Vulkan compute
		// barrier inside this list. That is exactly the dependency
		// required between neighbouring spatial LOD combines.
		//

		long computeList =
			_rd.ComputeListBegin();


		_rd.ComputeListBindComputePipeline(
			computeList,
			_pipeline);


		_rd.ComputeListBindUniformSet(
			computeList,
			_uniformSet,
			0);


		//
		// Crest order:
		//
		//     largest/coarsest LOD
		//         -> ...
		//         -> LOD0
		//
		// Do not parallelise this loop.
		//

		for (int lod =
				 _lodCount - 1;
			 lod >= 0;
			 lod--)
		{
			BitConverter.TryWriteBytes(
				_pushBytes.AsSpan(
					8,
					sizeof(uint)),
				(uint)lod);


			_rd.ComputeListSetPushConstant(
				computeList,
				_pushBytes,
				(uint)_pushBytes.Length);


			_rd.ComputeListDispatch(
				computeList,
				groupsX,
				groupsY,
				1);


			//
			// Final[lod] becomes an input of the immediately
			// following Final[lod - 1] dispatch.
			//
			// Barrier is unnecessary after LOD0 because no later
			// dispatch in this compute list reads from it.
			//

			if (lod > 0)
			{
				_rd.ComputeListAddBarrier(
					computeList);
			}
		}


		_rd.ComputeListEnd();


		if (!_firstDispatchLogged)
		{
			_firstDispatchLogged =
				true;


			GD.Print(
				"[Ocean] Animated Wave first coarse-to-fine combine dispatch recorded.");
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
