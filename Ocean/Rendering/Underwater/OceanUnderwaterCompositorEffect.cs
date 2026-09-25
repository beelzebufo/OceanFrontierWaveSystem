using System;
using System.Runtime.InteropServices;
using Godot;
using Godot.Collections;

namespace OceanFrontier.Water.Rendering.Underwater;

/// <summary>
/// Minimal forced-underwater full-screen Beer-Lambert pass.
///
/// The callback runs on the render thread and consumes only frame-local
/// RenderData plus persistent RenderingDevice resources owned here.
/// </summary>
public partial class OceanUnderwaterCompositorEffect : CompositorEffect
{
	private const string ShaderPath =
		"res://Ocean/Shaders/Rendering/underwater_simple.glsl";

	private const uint PushConstantBytes = 112;
	private const float MaxOpticalPathMetres = 240.0f;

	private readonly byte[] _pushBytes =
		new byte[PushConstantBytes];

	private RenderingDevice _rd;
	private RDShaderSpirV _spirV;
	private Rid _shader;
	private Rid _pipeline;
	private Rid _nearestSampler;

	private Vector3 _extinction;
	private Vector3 _deepScatterColor;


	public OceanUnderwaterCompositorEffect(
		Vector3 extinction,
		Vector3 deepScatterColor)
	{
		_extinction =
			extinction;

		_deepScatterColor =
			deepScatterColor;


		EffectCallbackType =
			EffectCallbackTypeEnum.PostTransparent;

		AccessResolvedColor =
			true;

		AccessResolvedDepth =
			true;

		_rd =
			RenderingServer.GetRenderingDevice();


		RDShaderFile shaderFile =
			GD.Load<RDShaderFile>(
				ShaderPath);


		if (shaderFile == null)
		{
			GD.PushError(
				$"Could not load underwater compositor shader: {ShaderPath}");


			return;
		}


		_spirV =
			shaderFile.GetSpirV();
	}


	/// <summary>
	/// Render thread only. Extinction and scatter are replaced together so a
	/// callback can never observe a partially updated medium state.
	/// </summary>
	internal void SetOpticsState(
		Vector3 extinction,
		Vector3 deepScatterColor)
	{
		_extinction =
			extinction;

		_deepScatterColor =
			deepScatterColor;
	}


	public override void _Notification(
		int what)
	{
		if (what != NotificationPredelete)
		{
			return;
		}


		ReleaseGpuResources();
	}


	/// <summary>Render thread only. Safe to call more than once.</summary>
	internal void ReleaseGpuResources()
	{
		if (_rd == null)
		{
			return;
		}


		if (_nearestSampler.IsValid)
		{
			_rd.FreeRid(
				_nearestSampler);


			_nearestSampler =
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


		_spirV =
			null;

		_rd =
			null;
	}


	public override void _RenderCallback(
		int effectCallbackType,
		RenderData renderData)
	{
		if (effectCallbackType !=
				(int)EffectCallbackTypeEnum.PostTransparent ||
			!EnsureResources())
		{
			return;
		}


		RenderSceneBuffersRD buffers =
			renderData.GetRenderSceneBuffers() as
			RenderSceneBuffersRD;


		RenderSceneData sceneData =
			renderData.GetRenderSceneData();


		if (buffers == null ||
			sceneData == null)
		{
			return;
		}


		Vector2I size =
			buffers.GetInternalSize();


		if (size.X <= 0 ||
			size.Y <= 0)
		{
			return;
		}


		uint groupCountX =
			(uint)((size.X + 7) / 8);


		uint groupCountY =
			(uint)((size.Y + 7) / 8);


		uint viewCount =
			Math.Min(
				buffers.GetViewCount(),
				sceneData.GetViewCount());


		for (uint view = 0;
			 view < viewCount;
			 view++)
		{
			Rid color =
				buffers.GetColorLayer(
					view);


			Rid depth =
				buffers.GetDepthLayer(
					view);


			if (!color.IsValid ||
				!depth.IsValid)
			{
				continue;
			}


			Projection inverseProjection =
				sceneData
					.GetViewProjection(
						view)
					.Inverse();


			WritePushConstants(
				size,
				inverseProjection);


			var colorUniform =
				new RDUniform
				{
					UniformType =
						RenderingDevice.UniformType.Image,

					Binding =
						0,
				};


			colorUniform.AddId(
				color);


			var depthUniform =
				new RDUniform
				{
					UniformType =
						RenderingDevice.UniformType.SamplerWithTexture,

					Binding =
						1,
				};


			depthUniform.AddId(
				_nearestSampler);

			depthUniform.AddId(
				depth);


			Rid uniformSet =
				UniformSetCacheRD.GetCache(
					_shader,
					0,
					new Array<RDUniform>
					{
						colorUniform,
						depthUniform,
					});


			if (!uniformSet.IsValid)
			{
				continue;
			}


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
				groupCountX,
				groupCountY,
				1);


			_rd.ComputeListEnd();
		}
	}


	private bool EnsureResources()
	{
		if (_rd == null ||
			_spirV == null)
		{
			return false;
		}


		if (!_shader.IsValid)
		{
			string error =
				_spirV.GetStageCompileError(
					RenderingDevice.ShaderStage.Compute);


			if (!string.IsNullOrEmpty(
					error))
			{
				GD.PushError(
					$"Underwater compositor shader compilation failed:\n{error}");


				return false;
			}


			_shader =
				_rd.ShaderCreateFromSpirV(
					_spirV);


			if (!_shader.IsValid)
			{
				return false;
			}


			_pipeline =
				_rd.ComputePipelineCreate(
					_shader);
		}


		if (!_pipeline.IsValid)
		{
			return false;
		}


		if (!_nearestSampler.IsValid)
		{
			_nearestSampler =
				_rd.SamplerCreate(
					new RDSamplerState
					{
						MinFilter =
							RenderingDevice.SamplerFilter.Nearest,

						MagFilter =
							RenderingDevice.SamplerFilter.Nearest,

						MipFilter =
							RenderingDevice.SamplerFilter.Nearest,

						RepeatU =
							RenderingDevice.SamplerRepeatMode.ClampToEdge,

						RepeatV =
							RenderingDevice.SamplerRepeatMode.ClampToEdge,

						RepeatW =
							RenderingDevice.SamplerRepeatMode.ClampToEdge,
					});
		}


		return
			_nearestSampler.IsValid;
	}


	private void WritePushConstants(
		Vector2I size,
		Projection inverseProjection)
	{
		Span<float> values =
			MemoryMarshal.Cast<byte, float>(
				_pushBytes.AsSpan());


		values[0] = size.X;
		values[1] = size.Y;
		values[2] = 0.0f;
		values[3] = 0.0f;


		WriteColumn(
			values,
			4,
			inverseProjection.X);

		WriteColumn(
			values,
			8,
			inverseProjection.Y);

		WriteColumn(
			values,
			12,
			inverseProjection.Z);

		WriteColumn(
			values,
			16,
			inverseProjection.W);


		values[20] = _extinction.X;
		values[21] = _extinction.Y;
		values[22] = _extinction.Z;
		values[23] = MaxOpticalPathMetres;


		values[24] = _deepScatterColor.X;
		values[25] = _deepScatterColor.Y;
		values[26] = _deepScatterColor.Z;
		values[27] = 0.0f;
	}


	private static void WriteColumn(
		Span<float> values,
		int offset,
		Vector4 column)
	{
		values[offset] = column.X;
		values[offset + 1] = column.Y;
		values[offset + 2] = column.Z;
		values[offset + 3] = column.W;
	}
}
