using System;
using Godot;

namespace OceanFrontier.Water.Rendering.Reflections;

/// <summary>
/// Disposable Reflection-1B-V1 feasibility harness. It deliberately owns one
/// persistent planar viewport/camera and exposes direct comparison controls;
/// it is not a production reflection manager.
/// </summary>
public partial class OceanPlanarReflectionDiagnostic : Node
{
	private const uint PlanarCandidateLayerMask = 1u << 18;
	private const float SeaLevel = 0.0f;

	private static readonly float[] ProbeIntensities =
	{
		1.0f,
		0.5f,
		0.25f,
		0.0f,
	};

	private static readonly float[] PlanarWeights =
	{
		0.0f,
		0.5f,
		1.0f,
	};

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

	private SubViewport _planarViewport;
	private Camera3D _planarCamera;
	private Texture2D _planarTexture;

	private Label _statusLabel;
	private TextureRect _rgbPreview;
	private TextureRect _alphaPreview;

	private bool _segmentedHull = true;
	private bool _smallDistortion = true;
	private bool _everyTwoFrames = true;
	private bool _probeEnabled = true;
	private bool _planarEnabled = true;
	private bool _previewsVisible = true;
	private float _probeIntensity = 1.0f;
	private float _planarWeight = 1.0f;
	private int _targetWidth = 256;
	private int _frameIndex;
	private int _probeIntensityIndex;
	private int _planarWeightIndex = 2;
	private int _surfaceDiagnosticMode;
	private ulong _reflectionStateRevision;
	private Transform3D _mainCameraSnapshot;
	private Transform3D _mirroredCameraSnapshot;
	private double _statusAccumulator;


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

	internal int SurfaceDiagnosticMode =>
		_surfaceDiagnosticMode;

	internal bool PreviewsVisible =>
		_previewsVisible;

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

		CreatePlanarViewport();
		CreateOverlay();
		ApplyHullMode();
		ApplyWeights();

		RenderingServer.FramePreDraw +=
			SynchronizePlanarStateForDraw;

		UpdateStatus();
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
				CullMask = PlanarCandidateLayerMask,
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
				PlanarWeights[_planarWeightIndex],
				0.0035f);
	}

	private void CreateOverlay()
	{
		var canvas =
			new CanvasLayer
			{
				Name = "PlanarReflectionDiagnosticOverlay",
				Layer = 100,
			};

		AddChild(canvas);

		_statusLabel =
			new Label
			{
				Position = new Vector2(344.0f, 12.0f),
				Modulate = Colors.White,
			};

		_statusLabel.AddThemeColorOverride(
			"font_shadow_color",
			Colors.Black);

		_statusLabel.AddThemeConstantOverride(
			"shadow_offset_x",
			2);

		_statusLabel.AddThemeConstantOverride(
			"shadow_offset_y",
			2);

		canvas.AddChild(_statusLabel);

		_rgbPreview = CreatePreview(
			new Vector2(1076.0f, 12.0f));

		canvas.AddChild(_rgbPreview);

		_alphaPreview = CreatePreview(
			new Vector2(1076.0f, 126.0f));

		var alphaShader =
			new Shader
			{
				Code =
					"shader_type canvas_item;\n" +
					"void fragment(){ float a = texture(TEXTURE, UV).a; COLOR = vec4(vec3(a), 1.0); }",
			};

		_alphaPreview.Material =
			new ShaderMaterial
			{
				Shader = alphaShader,
			};

		canvas.AddChild(_alphaPreview);
	}

	private TextureRect CreatePreview(
		Vector2 position)
	{
		return new TextureRect
		{
			Position = position,
			Size = new Vector2(192.0f, 108.0f),
			Texture = _planarTexture,
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
		};
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
				SeaLevel;

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
		_statusAccumulator += delta;

		if (_statusAccumulator >= 0.25)
		{
			_statusAccumulator = 0.0;
			UpdateStatus();
		}
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
				_mainCameraSnapshot)
				.Orthonormalized();


		_planarCamera.GlobalTransform =
			_mirroredCameraSnapshot;


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


		_reflectionStateRevision++;
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
		Transform3D source)
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
			SeaLevel;

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

		UpdateStatus();
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

		UpdateStatus();
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

		UpdateStatus();
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

		UpdateStatus();
	}


	internal void SetEveryTwoFrames(
		bool everyTwoFrames)
	{
		_everyTwoFrames =
			everyTwoFrames;

		UpdateStatus();
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
		UpdateStatus();
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

		UpdateStatus();
	}


	internal void SetSegmentedHull(
		bool segmented)
	{
		_segmentedHull =
			segmented;

		ApplyHullMode();
		UpdateStatus();
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

		UpdateStatus();
	}


	internal void SetPreviewsVisible(
		bool visible)
	{
		_previewsVisible =
			visible;

		if (_rgbPreview != null)
		{
			_rgbPreview.Visible =
				visible;
		}


		if (_alphaPreview != null)
		{
			_alphaPreview.Visible =
				visible;
		}
	}

	private void ApplyWeights()
	{
		SetProbeIntensity(
			ProbeIntensities[_probeIntensityIndex]);

		SetPlanarWeight(
			PlanarWeights[_planarWeightIndex]);
	}

	private void UpdateStatus()
	{
		if (_statusLabel == null)
		{
			return;
		}

		double fps =
			Engine.GetFramesPerSecond();

		double frameMilliseconds =
			fps > 0.0
				? 1000.0 / fps
				: 0.0;

		_statusLabel.Text =
			"Reflection-1B-V1\n" +
			$"F1 hull: {(_segmentedHull ? "SEGMENTED" : "WHOLE")}\n" +
			$"F2 probe: {(_probeEnabled ? "ON" : "OFF")} {_probeIntensity:0.##}\n" +
			$"F3 planar: {(_planarEnabled ? "ON" : "OFF")} {_planarWeight:0.##}\n" +
			$"F4 distortion: {(_smallDistortion ? "SMALL" : "OFF")}\n" +
			$"F5 size: {_planarViewport.Size.X}x{_planarViewport.Size.Y}\n" +
			$"F6 update: {(_everyTwoFrames ? "EVERY 2" : "ALWAYS")}\n" +
			$"F7 surface: {GetSurfaceDiagnosticName()}\n" +
			$"sync: PRE_DRAW  state/VP/planar: {_reflectionStateRevision}\n" +
			$"physics interp: {_mainCamera.IsPhysicsInterpolatedAndEnabled()}\n" +
			$"main: {_mainCameraSnapshot.Origin.X:0.0}, {_mainCameraSnapshot.Origin.Y:0.0}, {_mainCameraSnapshot.Origin.Z:0.0}\n" +
			$"mirror: {_mirroredCameraSnapshot.Origin.X:0.0}, {_mirroredCameraSnapshot.Origin.Y:0.0}, {_mirroredCameraSnapshot.Origin.Z:0.0}\n" +
			$"main forward: {-_mainCameraSnapshot.Basis.Z.X:0.00}, {-_mainCameraSnapshot.Basis.Z.Y:0.00}, {-_mainCameraSnapshot.Basis.Z.Z:0.00}\n" +
			$"mirror det: {_mirroredCameraSnapshot.Basis.Determinant():0.00}\n" +
			$"FPS: {fps:0.0}  frame: {frameMilliseconds:0.00} ms\n" +
			"RGB preview / alpha preview at right";
	}

	public override void _UnhandledKeyInput(
		InputEvent @event)
	{
		if (@event is not InputEventKey key ||
			!key.Pressed ||
			key.Echo)
		{
			return;
		}

		switch (key.Keycode)
		{
			case Key.F1:
				SetSegmentedHull(
					!_segmentedHull);
				break;

			case Key.F2:
				_probeIntensityIndex =
					(_probeIntensityIndex + 1) %
					ProbeIntensities.Length;
				SetProbeIntensity(
					ProbeIntensities[_probeIntensityIndex]);
				break;

			case Key.F3:
				_planarWeightIndex =
					(_planarWeightIndex + 1) %
					PlanarWeights.Length;
				SetPlanarWeight(
					PlanarWeights[_planarWeightIndex]);
				break;

			case Key.F4:
				SetSmallDistortion(
					!_smallDistortion);
				break;

			case Key.F5:
				SetTargetWidth(
					_targetWidth == 256
						? 1024
						: 256);
				break;

			case Key.F6:
				SetEveryTwoFrames(
					!_everyTwoFrames);
				break;

			case Key.F7:
				SetSurfaceDiagnosticMode(
					(_surfaceDiagnosticMode + 1) % 7);
				break;

			default:
				return;
		}

		UpdateStatus();
		GetViewport().SetInputAsHandled();
	}


	private string GetSurfaceDiagnosticName()
	{
		return _surfaceDiagnosticMode switch
		{
			1 => "SAMPLED RGB",
			2 => "SAMPLED ALPHA",
			3 => "PLANAR UV",
			4 => "FORCE WEIGHT 1",
			5 => "DISTORTION OFF",
			6 => "OLD SCREEN_UV RGB",
			_ => "FINAL",
		};
	}

	public override void _ExitTree()
	{
		RenderingServer.FramePreDraw -=
			SynchronizePlanarStateForDraw;

		_surfaceRenderer?.SetPlanarReflectionDiagnosticMode(0);
		_surfaceRenderer?.SetPlanarReflectionWeight(0.0f);
	}
}
