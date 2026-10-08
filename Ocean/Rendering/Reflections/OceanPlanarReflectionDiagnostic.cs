using System;
using Godot;
using OceanFrontier.Water.Runtime;

namespace OceanFrontier.Water.Rendering.Reflections;

/// <summary>
/// Disposable Reflection-1B-V1 feasibility harness. It deliberately owns one
/// persistent planar viewport/camera and exposes direct comparison controls;
/// it is not a production reflection manager.
/// </summary>
public partial class OceanPlanarReflectionDiagnostic : Node
{
	// Layer 16 is camera-only; layers 17/18 belong to the player.
	private const uint PlanarPassMarkerLayerMask = 1u << 15;
	private const uint PlanarCandidateLayerMask = 1u << 18;
	// Rendering bias only; never shifts the camera reflection plane.
	private const float ReflectionClipBias = 0.0f;

	[Export]
	public NodePath SurfaceRendererPath { get; set; }

	[Export]
	public NodePath MainCameraPath { get; set; }

	[Export]
	public NodePath ReflectionProbePath { get; set; }

	[Export]
	public NodePath HullBelowWaterPath { get; set; }

	private AnimatedWaveSurfaceRenderer _surfaceRenderer;
	private Camera3D _mainCamera;
	private ReflectionProbe _reflectionProbe;
	private MeshInstance3D _hullBelowWater;
	private ShaderMaterial _hullClipMaterial;
	private uint _originalMainCameraCullMask;
	private bool _mainCameraMaskCached;

	private SubViewport _planarViewport;
	private Camera3D _planarCamera;
	private Texture2D _planarTexture;

	private bool _segmentedHull = false;
	private bool _materialClipEnabled = true;
	private bool _smallDistortion = true;
	private bool _everyTwoFrames = true;
	private bool _probeEnabled = true;
	private bool _planarEnabled = true;
	private float _probeIntensity = 1.0f;
	private float _planarWeight = 1.0f;
	private int _targetWidth = 256;
	private int _frameIndex;
	private int _surfaceDiagnosticMode;
	private Transform3D _mainCameraSnapshot;
	private Transform3D _mirroredCameraSnapshot;


	internal bool ProbeEnabled =>
		_probeEnabled;

	internal float ProbeIntensity =>
		_probeIntensity;

	internal bool PlanarEnabled =>
		_planarEnabled;

	internal float PlanarWeight =>
		_planarWeight;

	internal bool EveryTwoFrames =>
		_everyTwoFrames;

	internal int TargetWidth =>
		_targetWidth;

	internal bool SmallDistortion =>
		_smallDistortion;

	internal bool SegmentedHull =>
		_segmentedHull;

	internal bool MaterialClipEnabled =>
		_materialClipEnabled;

	internal int SurfaceDiagnosticMode =>
		_surfaceDiagnosticMode;

	internal Texture2D PlanarTexture =>
		_planarTexture;

	public override void _Ready()
	{
		_surfaceRenderer =
			GetNodeOrNull<AnimatedWaveSurfaceRenderer>(
				SurfaceRendererPath);

		_mainCamera =
			GetNodeOrNull<Camera3D>(
				MainCameraPath);

		_reflectionProbe =
			GetNodeOrNull<ReflectionProbe>(
				ReflectionProbePath);

		_hullBelowWater =
			GetNodeOrNull<MeshInstance3D>(
				HullBelowWaterPath);

		if (_surfaceRenderer == null ||
			_mainCamera == null ||
			_reflectionProbe == null ||
			_hullBelowWater == null)
		{
			GD.PushError(
				"Reflection-1B-V1 diagnostic node paths are incomplete.");
			SetProcess(false);
			return;
		}

		_hullClipMaterial =
			_hullBelowWater.MaterialOverride as ShaderMaterial;

		if (_hullClipMaterial?.Shader == null ||
			_hullClipMaterial.Shader.ResourcePath !=
				"res://Ocean/Shaders/Rendering/diagnostic_planar_clip_pbr.gdshader")
		{
			GD.PushError(
				"Reflection-1C requires the diagnostic planar clip ShaderMaterial on HullBelowWater.");
			SetProcess(false);
			return;
		}

		_originalMainCameraCullMask = _mainCamera.CullMask;
		_mainCameraMaskCached = true;
		_mainCamera.CullMask &= ~PlanarPassMarkerLayerMask;

		SetMaterialClipEnabled(_materialClipEnabled);
		CreatePlanarViewport();
		ApplyHullMode();
		SetProbeIntensity(_probeIntensity);
		SetPlanarWeight(_planarWeight);

		RenderingServer.FramePreDraw +=
			SynchronizePlanarStateForDraw;
	}

	private void CreatePlanarViewport()
	{
		_planarViewport =
			new SubViewport
			{
				Name = "PlanarReflectionViewport",
				OwnWorld3D = false,
				World3D = GetViewport().World3D,
				TransparentBg = true,
				Disable3D = false,
				Msaa3D = Viewport.Msaa.Disabled,
				ScreenSpaceAA =
					Viewport.ScreenSpaceAAEnum.Disabled,
				UseTaa = false,
				UseOcclusionCulling = false,
				PositionalShadowAtlasSize = 0,
				RenderTargetClearMode =
					SubViewport.ClearMode.Always,
			};

		AddChild(_planarViewport);

		_planarCamera =
			new Camera3D
			{
				Name = "PlanarReflectionCamera",
				CullMask =
					PlanarCandidateLayerMask |
					PlanarPassMarkerLayerMask,
				Current = true,
				// This camera is driven from the main camera's already effective
				// render-time transform once per draw. Interpolating it again would
				// mix two camera states.
				PhysicsInterpolationMode =
					PhysicsInterpolationModeEnum.Off,
			};

		_planarViewport.AddChild(_planarCamera);

		UpdateViewportSize();

		_planarTexture =
			_planarViewport.GetTexture();

		_surfaceRenderer.SetPlanarReflectionDiagnostic(
			_planarTexture,
				_planarWeight,
				0.0035f);
	}

	public override void _Process(
		double delta)
	{
		if (_planarViewport == null ||
			_planarCamera == null)
		{
			return;
		}

		UpdateViewportSize();

		bool aboveWater =
			_mainCamera.GlobalPosition.Y >=
				OceanRuntime.MeanSeaLevelY;

		bool planarActive =
			aboveWater &&
			!_surfaceRenderer.PlanetCurvatureEnabled &&
			_planarEnabled &&
			_planarWeight > 0.0f;

		_surfaceRenderer.SetPlanarReflectionWeight(
			planarActive
				? _planarWeight
				: 0.0f);

		if (!planarActive)
		{
			_planarViewport.RenderTargetUpdateMode =
				SubViewport.UpdateMode.Disabled;
		}
		else if (!_everyTwoFrames)
		{
			_planarViewport.RenderTargetUpdateMode =
				SubViewport.UpdateMode.Always;
		}
		else if ((_frameIndex & 1) == 0)
		{
			_planarViewport.RenderTargetUpdateMode =
				SubViewport.UpdateMode.Once;
		}

		_frameIndex++;
	}


	private void SynchronizePlanarStateForDraw()
	{
		if (!IsInsideTree() ||
			_planarCamera == null ||
			_mainCamera == null ||
			_surfaceRenderer == null)
		{
			return;
		}


		CopyProjectionAndEnvironment();


		// This is the exact effective camera transform used by Camera3D for the
		// coming draw, including physics interpolation (when globally enabled)
		// and camera H/V offsets. FramePreDraw runs after gameplay processing and
		// before RenderingServer updates either viewport.
		_mainCameraSnapshot =
			_mainCamera.GetCameraTransform();


		_mirroredCameraSnapshot =
			ReflectCameraTransform(
				_mainCameraSnapshot,
				OceanRuntime.MeanSeaLevelY)
				.Orthonormalized();


		_planarCamera.GlobalTransform =
			_mirroredCameraSnapshot;
		// FramePreDraw follows SceneTree's normal transform notification flush.
		// Update the RenderingServer camera before the SubViewport draws.
		_planarCamera.ForceUpdateTransform();


		// Camera3D's raw projection maps view-space Y upward. The shader applies
		// the same final Y inversion as Camera3D.UnprojectPosition when mapping
		// NDC to ViewportTexture UV. Use the same mirrored snapshot assigned to
		// the camera instead of re-reading mutable node state.
		Projection reflectionView =
			new(
				_mirroredCameraSnapshot.AffineInverse());


		Projection reflectionViewProjection =
			_planarCamera.GetCameraProjection() *
			reflectionView;


		_surfaceRenderer.SetPlanarReflectionViewProjection(
			reflectionViewProjection);
	}

	private void UpdateViewportSize()
	{
		Vector2 mainSize =
			GetViewport().GetVisibleRect().Size;

		if (mainSize.X <= 0 ||
			mainSize.Y <= 0)
		{
			return;
		}

		int targetHeight =
			Math.Max(
				2,
				Mathf.RoundToInt(
					_targetWidth *
					(float)mainSize.Y /
					mainSize.X));

		var targetSize =
			new Vector2I(
				_targetWidth,
				targetHeight);

		if (_planarViewport.Size !=
			targetSize)
		{
			_planarViewport.Size =
				targetSize;

			_planarViewport.RenderTargetUpdateMode =
				SubViewport.UpdateMode.Once;
		}
	}

	private void CopyProjectionAndEnvironment()
	{
		_planarCamera.Projection =
			_mainCamera.Projection;

		_planarCamera.Fov =
			_mainCamera.Fov;

		_planarCamera.Size =
			_mainCamera.Size;

		_planarCamera.Near =
			_mainCamera.Near;

		_planarCamera.Far =
			Mathf.Min(
				_mainCamera.Far,
				160.0f);

		_planarCamera.FrustumOffset =
			_mainCamera.FrustumOffset;

		_planarCamera.KeepAspect =
			_mainCamera.KeepAspect;

		_planarCamera.Environment =
			_mainCamera.Environment;

		_planarCamera.Attributes =
			_mainCamera.Attributes;
	}

	private static Transform3D ReflectCameraTransform(
		Transform3D source,
		float planeY)
	{
		static Vector3 ReflectVector(
			Vector3 value)
		{
			return new Vector3(
				value.X,
				-value.Y,
				value.Z);
		}

		var reflectedBasis =
			new Basis(
				ReflectVector(source.Basis.X),
				ReflectVector(source.Basis.Y),
				ReflectVector(source.Basis.Z));

		Vector3 reflectedOrigin =
			ReflectVector(source.Origin);

		reflectedOrigin.Y +=
			2.0f *
			planeY;

		return new Transform3D(
			reflectedBasis,
			reflectedOrigin);
	}

	private void ApplyHullMode()
	{
		_hullBelowWater.Layers =
			_segmentedHull
				? 1u
				: PlanarCandidateLayerMask;
	}


	internal void SetProbeEnabled(
		bool enabled)
	{
		_probeEnabled =
			enabled;

		_reflectionProbe.Visible =
			enabled;

	}


	internal void SetProbeIntensity(
		float intensity)
	{
		_probeIntensity =
			Mathf.Clamp(
				intensity,
				0.0f,
				2.0f);

		_reflectionProbe.Intensity =
			_probeIntensity;

	}


	internal void SetPlanarEnabled(
		bool enabled)
	{
		_planarEnabled =
			enabled;

		_surfaceRenderer.SetPlanarReflectionWeight(
			enabled
				? _planarWeight
				: 0.0f);

	}


	internal void SetPlanarWeight(
		float weight)
	{
		_planarWeight =
			Mathf.Clamp(
				weight,
				0.0f,
				1.0f);

		_surfaceRenderer.SetPlanarReflectionWeight(
			_planarEnabled
				? _planarWeight
				: 0.0f);

	}


	internal void SetEveryTwoFrames(
		bool everyTwoFrames)
	{
		_everyTwoFrames =
			everyTwoFrames;

	}


	internal void SetTargetWidth(
		int width)
	{
		_targetWidth =
			width switch
			{
				512 => 512,
				768 => 768,
				1024 => 1024,
				_ => 256,
			};

		UpdateViewportSize();
	}


	internal void SetSmallDistortion(
		bool enabled)
	{
		_smallDistortion =
			enabled;

		_surfaceRenderer.SetPlanarReflectionDistortion(
			enabled
				? 0.0035f
				: 0.0f);

	}


	internal void SetSegmentedHull(
		bool segmented)
	{
		_segmentedHull =
			segmented;

		ApplyHullMode();
	}


	internal void SetMaterialClipEnabled(
		bool enabled)
	{
		_materialClipEnabled = enabled;

		if (_hullClipMaterial == null)
		{
			return;
		}

		_hullClipMaterial.SetShaderParameter(
			"reflection_clip_enabled", enabled);
		_hullClipMaterial.SetShaderParameter(
			"reflection_clip_y", OceanRuntime.MeanSeaLevelY);
		_hullClipMaterial.SetShaderParameter(
			"reflection_clip_bias", ReflectionClipBias);
	}


	internal void SetSurfaceDiagnosticMode(
		int mode)
	{
		_surfaceDiagnosticMode =
			Math.Clamp(
				mode,
				0,
				6);

		_surfaceRenderer.SetPlanarReflectionDiagnosticMode(
			_surfaceDiagnosticMode);

	}

	public override void _ExitTree()
	{
		RenderingServer.FramePreDraw -=
			SynchronizePlanarStateForDraw;

		if (_mainCameraMaskCached &&
			GodotObject.IsInstanceValid(_mainCamera))
		{
			_mainCamera.CullMask = _originalMainCameraCullMask;
			_mainCameraMaskCached = false;
		}

		_surfaceRenderer?.SetPlanarReflectionDiagnosticMode(0);
		_surfaceRenderer?.SetPlanarReflectionWeight(0.0f);
	}
}
