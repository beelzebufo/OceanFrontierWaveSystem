using System;
using System.Runtime.InteropServices;
using System.Threading;
using Godot;
using Godot.Collections;
using OceanFrontier.Water.Rendering;
using OceanFrontier.Water.Waves.AnimatedWaves;

namespace OceanFrontier.Water.Rendering.Underwater;

/// <summary>
/// Minimal forced-underwater full-screen Beer-Lambert pass.
///
/// The callback runs on the render thread and consumes only frame-local
/// RenderData plus persistent RenderingDevice resources owned here.
/// </summary>
public partial class OceanUnderwaterCompositorEffect :
	CompositorEffect,
	IAnimatedWaveFieldGpuConsumer
{
	private const string ShaderPath =
		"res://Ocean/Shaders/Rendering/underwater_simple.glsl";

	private const uint PushConstantBytes = 128;
	private const uint FrameUniformBytes = 128;
	private const float MaxOpticalPathMetres = 240.0f;

	private readonly byte[] _pushBytes =
		new byte[PushConstantBytes];

	private readonly byte[] _frameUniformBytes =
		new byte[FrameUniformBytes];

	private RenderingDevice _rd;
	private RDShaderSpirV _spirV;
	private Rid _shader;
	private Rid _pipeline;
	private Rid _nearestSampler;
	private Rid _linearClampSampler;
	private Rid _linearRepeatMipSampler;
	private Rid _frameUniformBuffer;
	private Rid _neutralTexture;

	private AnimatedWaveFieldGpuSnapshot _animatedWaveSnapshot;

	private OceanCausticsGpuState _causticsState;
	private Rid _causticsTexture;
	private Rid _causticsDistortionTexture;
	private int _causticsMipCount = 1;
	private bool _hasCausticsTexture;
	private bool _hasCausticsDistortionTexture;

	private Vector3 _extinction;
	private Vector3 _deepScatterColor;
	private Vector3 _primarySunRayDirectionWorld;
	private Vector3 _primarySunRadiance;
	private Vector3 _oceanAmbientLight;
	private float _pendingCameraWaterDepth;
	private float _pendingVisualTime;


	internal OceanUnderwaterCompositorEffect(
		Vector3 extinction,
		Vector3 deepScatterColor,
		Vector3 primarySunRayDirectionWorld,
		Vector3 primarySunRadiance,
		Vector3 oceanAmbientLight,
		OceanCausticsGpuState causticsState)
	{
		_extinction =
			extinction;

		_deepScatterColor =
			deepScatterColor;

		_primarySunRayDirectionWorld =
			primarySunRayDirectionWorld;

		_primarySunRadiance =
			primarySunRadiance;

		_oceanAmbientLight =
			oceanAmbientLight;

		_causticsState =
			causticsState;


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


	/// <summary>
	/// Render thread only. Sun and ambient values come from one lighting
	/// revision and are replaced coherently.
	/// </summary>
	internal void SetLightingState(
		Vector3 rayDirectionWorld,
		Vector3 linearRadiance,
		Vector3 ambientLinear)
	{
		_primarySunRayDirectionWorld =
			rayDirectionWorld;

		_primarySunRadiance =
			linearRadiance;

		_oceanAmbientLight =
			ambientLinear;
	}


	void IAnimatedWaveFieldGpuConsumer.SetAnimatedWaveFieldGpuSnapshot(
		AnimatedWaveFieldGpuSnapshot snapshot)
	{
		_animatedWaveSnapshot =
			snapshot;
	}


	void IAnimatedWaveFieldGpuConsumer.ClearAnimatedWaveFieldGpuSnapshot()
	{
		_animatedWaveSnapshot =
			default;
	}


	/// <summary>
	/// Render thread only. RenderingServer texture handles are resolved here
	/// to borrowed global-RD texture handles.
	/// </summary>
	internal void SetCausticsState(
		OceanCausticsGpuState state)
	{
		_causticsState =
			state;


		ResolveCausticsTextures();
	}


	/// <summary>
	/// Main-thread publication of the canonical camera-surface query depth.
	/// The render callback consumes this single scalar atomically.
	/// </summary>
	internal void SetPendingCameraWaterDepth(
		float waterDepth)
	{
		Volatile.Write(
			ref _pendingCameraWaterDepth,
			float.IsFinite(
				waterDepth)
				? Mathf.Max(
					waterDepth,
					0.0f)
				: 0.0f);
	}


	internal void SetPendingVisualTime(
		float visualTime)
	{
		Volatile.Write(
			ref _pendingVisualTime,
			float.IsFinite(visualTime)
				? visualTime
				: 0.0f);
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


		_animatedWaveSnapshot =
			default;

		_causticsTexture =
			default;

		_causticsDistortionTexture =
			default;

		_hasCausticsTexture =
			false;

		_hasCausticsDistortionTexture =
			false;


		if (_neutralTexture.IsValid)
		{
			_rd.FreeRid(
				_neutralTexture);

			_neutralTexture =
				default;
		}


		if (_frameUniformBuffer.IsValid)
		{
			_rd.FreeRid(
				_frameUniformBuffer);

			_frameUniformBuffer =
				default;
		}


		if (_linearRepeatMipSampler.IsValid)
		{
			_rd.FreeRid(
				_linearRepeatMipSampler);

			_linearRepeatMipSampler =
				default;
		}


		if (_linearClampSampler.IsValid)
		{
			_rd.FreeRid(
				_linearClampSampler);

			_linearClampSampler =
				default;
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
			!_animatedWaveSnapshot.IsValid ||
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


		float cameraWaterDepth =
			Volatile.Read(
				ref _pendingCameraWaterDepth);


		float sunDownCos =
			Mathf.Max(
				-_primarySunRayDirectionWorld.Y,
				0.0001f);


		float incidentSunOpticalPath =
			Mathf.Clamp(
				cameraWaterDepth /
					sunDownCos,
				0.0f,
				MaxOpticalPathMetres);


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


			Transform3D cameraToWorld =
				sceneData.GetCamTransform();


			cameraToWorld.Origin +=
				cameraToWorld.Basis *
				sceneData.GetViewEyeOffset(
					view);


			if (!WriteFrameUniforms(
					cameraToWorld,
					Volatile.Read(
						ref _pendingVisualTime)))
			{
				continue;
			}


			WritePushConstants(
				size,
				inverseProjection,
				_primarySunRayDirectionWorld,
				incidentSunOpticalPath);


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


			var animatedWaveUniform =
				new RDUniform
				{
					UniformType =
						RenderingDevice.UniformType.SamplerWithTexture,

					Binding =
						2,
				};


			animatedWaveUniform.AddId(
				_linearClampSampler);

			animatedWaveUniform.AddId(
				_animatedWaveSnapshot.AnimatedWaveField);


			var lodMetadataUniform =
				new RDUniform
				{
					UniformType =
						RenderingDevice.UniformType.StorageBuffer,

					Binding =
						3,
				};


			lodMetadataUniform.AddId(
				_animatedWaveSnapshot.LodMetadataBuffer);


			var causticsUniform =
				new RDUniform
				{
					UniformType =
						RenderingDevice.UniformType.SamplerWithTexture,

					Binding =
						4,
				};


			causticsUniform.AddId(
				_linearRepeatMipSampler);

			causticsUniform.AddId(
				_causticsTexture);


			var distortionUniform =
				new RDUniform
				{
					UniformType =
						RenderingDevice.UniformType.SamplerWithTexture,

					Binding =
						5,
				};


			distortionUniform.AddId(
				_linearRepeatMipSampler);

			distortionUniform.AddId(
				_causticsDistortionTexture);


			var frameUniform =
				new RDUniform
				{
					UniformType =
						RenderingDevice.UniformType.UniformBuffer,

					Binding =
						6,
				};


			frameUniform.AddId(
				_frameUniformBuffer);


			Rid uniformSet =
				UniformSetCacheRD.GetCache(
					_shader,
					0,
					new Array<RDUniform>
					{
						colorUniform,
						depthUniform,
						animatedWaveUniform,
						lodMetadataUniform,
						causticsUniform,
						distortionUniform,
						frameUniform,
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


		if (!_linearClampSampler.IsValid)
		{
			_linearClampSampler =
				_rd.SamplerCreate(
					new RDSamplerState
					{
						MinFilter = RenderingDevice.SamplerFilter.Linear,
						MagFilter = RenderingDevice.SamplerFilter.Linear,
						MipFilter = RenderingDevice.SamplerFilter.Nearest,
						RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge,
						RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge,
						RepeatW = RenderingDevice.SamplerRepeatMode.ClampToEdge,
					});
		}


		if (!_linearRepeatMipSampler.IsValid)
		{
			_linearRepeatMipSampler =
				_rd.SamplerCreate(
					new RDSamplerState
					{
						MinFilter = RenderingDevice.SamplerFilter.Linear,
						MagFilter = RenderingDevice.SamplerFilter.Linear,
						MipFilter = RenderingDevice.SamplerFilter.Linear,
						RepeatU = RenderingDevice.SamplerRepeatMode.Repeat,
						RepeatV = RenderingDevice.SamplerRepeatMode.Repeat,
						RepeatW = RenderingDevice.SamplerRepeatMode.Repeat,
					});
		}


		if (!_frameUniformBuffer.IsValid)
		{
			_frameUniformBuffer =
				_rd.UniformBufferCreate(
					FrameUniformBytes,
					_frameUniformBytes);
		}


		if (!_neutralTexture.IsValid)
		{
			var textureFormat =
				new RDTextureFormat
				{
					Format = RenderingDevice.DataFormat.R8G8B8A8Unorm,
					Width = 1,
					Height = 1,
					Depth = 1,
					ArrayLayers = 1,
					Mipmaps = 1,
					TextureType = RenderingDevice.TextureType.Type2D,
					UsageBits = RenderingDevice.TextureUsageBits.SamplingBit,
				};


			_neutralTexture =
				_rd.TextureCreate(
					textureFormat,
					new RDTextureView(),
					new Array<byte[]>
					{
						new byte[]
						{
							128,
							128,
							128,
							255,
						},
					});
		}


		if (!_causticsTexture.IsValid ||
			!_causticsDistortionTexture.IsValid)
		{
			ResolveCausticsTextures();
		}


		return
			_nearestSampler.IsValid &&
			_linearClampSampler.IsValid &&
			_linearRepeatMipSampler.IsValid &&
			_frameUniformBuffer.IsValid &&
			_neutralTexture.IsValid &&
			_causticsTexture.IsValid &&
			_causticsDistortionTexture.IsValid;
	}


	private void ResolveCausticsTextures()
	{
		if (_rd == null ||
			!_neutralTexture.IsValid)
		{
			return;
		}


		Rid caustics =
			_causticsState.Texture.IsValid
				? RenderingServer.TextureGetRdTexture(
					_causticsState.Texture,
					false)
				: default;


		_hasCausticsTexture =
			caustics.IsValid;


		_causticsTexture =
			_hasCausticsTexture
				? caustics
				: _neutralTexture;


		_causticsMipCount =
			_hasCausticsTexture
				? Math.Max(
					(int)_rd.TextureGetFormat(
						caustics).Mipmaps,
					1)
				: 1;


		Rid distortion =
			_causticsState.DistortionTexture.IsValid
				? RenderingServer.TextureGetRdTexture(
					_causticsState.DistortionTexture,
					false)
				: default;


		_hasCausticsDistortionTexture =
			distortion.IsValid;


		_causticsDistortionTexture =
			_hasCausticsDistortionTexture
				? distortion
				: _neutralTexture;
	}


	private bool WriteFrameUniforms(
		Transform3D cameraToWorld,
		float visualTime)
	{
		Span<float> values =
			MemoryMarshal.Cast<byte, float>(
				_frameUniformBytes.AsSpan());


		WriteColumn(
			values,
			0,
			new Vector4(
				cameraToWorld.Basis.X.X,
				cameraToWorld.Basis.X.Y,
				cameraToWorld.Basis.X.Z,
				0.0f));

		WriteColumn(
			values,
			4,
			new Vector4(
				cameraToWorld.Basis.Y.X,
				cameraToWorld.Basis.Y.Y,
				cameraToWorld.Basis.Y.Z,
				0.0f));

		WriteColumn(
			values,
			8,
			new Vector4(
				cameraToWorld.Basis.Z.X,
				cameraToWorld.Basis.Z.Y,
				cameraToWorld.Basis.Z.Z,
				0.0f));

		WriteColumn(
			values,
			12,
			new Vector4(
				cameraToWorld.Origin.X,
				cameraToWorld.Origin.Y,
				cameraToWorld.Origin.Z,
				1.0f));


		values[16] = visualTime;
		values[17] =
			_hasCausticsTexture &&
			_causticsState.Strength > 0.0f
				? 1.0f
				: 0.0f;

		values[18] =
			_hasCausticsDistortionTexture &&
			_causticsState.DistortionStrength > 0.0f
				? 1.0f
				: 0.0f;
		values[19] = Math.Max(_causticsMipCount - 1, 0);

		values[20] = _causticsState.Scale;
		values[21] = _causticsState.TextureAverage;
		values[22] = _causticsState.Strength;
		values[23] = _causticsState.FocalDepth;

		values[24] = _causticsState.DepthOfField;
		values[25] = _causticsState.DistortionScale;
		values[26] = _causticsState.DistortionStrength;
		values[27] = _animatedWaveSnapshot.LodCount;

		values[28] = _oceanAmbientLight.X;
		values[29] = _oceanAmbientLight.Y;
		values[30] = _oceanAmbientLight.Z;
		values[31] = 0.0f;


		return
			_rd.BufferUpdate(
				_frameUniformBuffer,
				0,
				FrameUniformBytes,
				_frameUniformBytes) ==
			Error.Ok;
	}


	private void WritePushConstants(
		Vector2I size,
		Projection inverseProjection,
		Vector3 sunRayDirectionWorld,
		float incidentSunOpticalPath)
	{
		Span<float> values =
			MemoryMarshal.Cast<byte, float>(
				_pushBytes.AsSpan());


		values[0] = size.X;
		values[1] = size.Y;
		values[2] = _primarySunRadiance.X;
		values[3] = _primarySunRadiance.Y;


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
		values[27] = _primarySunRadiance.Z;


		values[28] = sunRayDirectionWorld.X;
		values[29] = sunRayDirectionWorld.Y;
		values[30] = sunRayDirectionWorld.Z;
		values[31] = incidentSunOpticalPath;
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
