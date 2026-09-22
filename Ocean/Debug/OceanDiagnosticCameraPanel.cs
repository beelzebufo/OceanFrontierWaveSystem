using System;
using Godot;
using OceanFrontier.Water.Rendering;
using OceanFrontier.Water.Runtime;
using OceanFrontier.Water.Waves.AnimatedWaves;

namespace OceanFrontier.Water.Debug;

internal sealed class OceanDiagnosticCameraPanel
{
	private const float CrestNestedHorizonFarDistance =
		200000.0f;


	private OceanRuntime _runtime;

	private AnimatedWaveSurfaceRenderer _surface;

	private OceanDiagnosticCameraController _camera;

	private RigidBody3D _diagnosticHull;

	private Func<int> _getSelectedLod;


	private OceanDiagnosticCameraController.ViewMode _viewMode =
		OceanDiagnosticCameraController.ViewMode.Perspective;

	private CheckBox _vesselCamera;

	private CheckBox _freeCamera;

	private bool _syncing;

	private bool _framed;

	private bool _overviewRequested;

	private float _nestedFarDistance =
		CrestNestedHorizonFarDistance;


	internal void Initialize(
		VBoxContainer parent,
		OceanRuntime runtime,
		AnimatedWaveSurfaceRenderer surface,
		OceanDiagnosticCameraController camera,
		RigidBody3D diagnosticHull,
		Func<int> getSelectedLod)
	{
		_runtime =
			runtime;

		_surface =
			surface;

		_camera =
			camera;

		_diagnosticHull =
			diagnosticHull;

		_getSelectedLod =
			getSelectedLod;


		Build(
			parent);


		UpdateFarDistance();
	}


	private void Build(
		VBoxContainer parent)
	{
		OceanDiagnosticUi.Header(
			parent,
			"Camera");


		OceanDiagnosticUi.Info(
			parent,
			"Choose a fixed diagnostic view, free-fly camera, or a camera attached to the test vessel.");


		var viewRowA =
			new HBoxContainer();


		parent.AddChild(
			viewRowA);


		OceanDiagnosticUi.Button(
			viewRowA,
			"Perspective",
			() =>
				RequestView(
					OceanDiagnosticCameraController.ViewMode.Perspective));


		OceanDiagnosticUi.Button(
			viewRowA,
			"Top",
			() =>
				RequestView(
					OceanDiagnosticCameraController.ViewMode.Top));


		var viewRowB =
			new HBoxContainer();


		parent.AddChild(
			viewRowB);


		OceanDiagnosticUi.Button(
			viewRowB,
			"Side X",
			() =>
				RequestView(
					OceanDiagnosticCameraController.ViewMode.SideX));


		OceanDiagnosticUi.Button(
			viewRowB,
			"Side Z",
			() =>
				RequestView(
					OceanDiagnosticCameraController.ViewMode.SideZ));


		OceanDiagnosticUi.Button(
			parent,
			"Overview",
			() =>
			{
				ExitVesselMode();


				_overviewRequested =
					true;


				_framed =
					false;
			});


		_freeCamera =
			OceanDiagnosticUi.Check(
				parent,
				"Free camera",
				false);


		_freeCamera.Toggled +=
			value =>
			{
				if (_syncing)
				{
					return;
				}


				if (value)
				{
					SetVesselToggle(
						false);
				}


				_camera?.SetFreeCameraEnabled(
					value);
			};


		_vesselCamera =
			OceanDiagnosticUi.Check(
				parent,
				"Vessel camera",
				false);


		_vesselCamera.Toggled +=
			value =>
			{
				if (_syncing)
				{
					return;
				}


				if (value)
				{
					SetFreeToggle(
						false);


					if (_diagnosticHull != null)
					{
						_camera?.EnterVesselMode(
							_diagnosticHull);


						_overviewRequested =
							false;


						_framed =
							true;
					}
				}
				else
				{
					_camera?.ExitVesselMode();


					_framed =
						false;
				}
			};


		var follow =
			OceanDiagnosticUi.Check(
				parent,
				"Ocean follows camera",
				false);


		follow.Toggled +=
			value =>
				_runtime.FocusOverrideEnabled =
					!value;


		var viewHeightLod =
			OceanDiagnosticUi.Check(
				parent,
				"View-height LOD scaling",
				false);


		viewHeightLod.Toggled +=
			value =>
			{
				if (value)
				{
					_runtime.LodScaleOverrideEnabled =
						false;
				}
				else
				{
					_runtime.LodScaleOverride =
						1.0f;


					_runtime.LodScaleOverrideEnabled =
						true;
				}


				_framed =
					false;
			};


		VBoxContainer horizon =
			OceanDiagnosticUi.Foldout(
				parent,
				"Planet / Horizon",
				expanded: false);


		OceanDiagnosticUi.Info(
			horizon,
			"Renderer-only. AnimatedWaveField and physics remain in local tangent space.");


		SpinBox planetRadius =
			OceanDiagnosticUi.Spin(
				horizon,
				"Planet radius",
				_surface?.PlanetRadius ??
					6371000.0,
				10000.0,
				20000000.0,
				1000.0,
				" m");


		CheckBox planetCurvature =
			OceanDiagnosticUi.Check(
				horizon,
				"Planet curvature",
				_surface?.PlanetCurvatureEnabled ??
					false);


		planetCurvature.Toggled +=
			value =>
				_surface?.SetPlanetCurvature(
					value,
					(float)planetRadius.Value);


		planetRadius.ValueChanged +=
			value =>
				_surface?.SetPlanetCurvature(
					planetCurvature.ButtonPressed,
					(float)value);


		OceanDiagnosticUi.Info(
			horizon,
			"Crest's outer skirt remains flat topology; the renderer bends its final GPU vertex positions. Nested mode keeps the far plane near 200 km.");


		OceanDiagnosticUi.Spin(
			horizon,
			"Nested far",
			_nestedFarDistance,
			1000.0,
			1000000.0,
			1000.0,
			" m")
			.ValueChanged +=
				value =>
			{
				_nestedFarDistance =
					(float)value;


				UpdateFarDistance();
			};


		VBoxContainer vessel =
			OceanDiagnosticUi.Foldout(
				parent,
				"Vessel camera settings",
				expanded: false);


		OceanDiagnosticUi.Check(
			vessel,
			"Mast view (20 m)",
			false)
			.Toggled +=
				value =>
					_camera?.SetVesselMastEnabled(
						value);


		OceanDiagnosticUi.Spin(
			vessel,
			"Camera damping",
			_camera?.VesselCameraDamping ??
				0.35,
			0.0,
			2.0,
			0.05,
			" s")
			.ValueChanged +=
				value =>
					_camera?.SetVesselCameraDamping(
						(float)value);


		OceanDiagnosticUi.Info(
			vessel,
			"Vessel mode: WASD moves over the deck, RMB looks around. Shift = fast, Ctrl = precision.");


		OceanDiagnosticUi.Info(
			parent,
			"Free camera: WASD + Q/E, RMB to look. Fixed views use the mouse wheel to zoom.");
	}


	internal void Tick(
		double delta)
	{
		UpdateFarDistance();


		if (_camera == null ||
			_runtime == null ||
			_surface == null ||
			_framed)
		{
			return;
		}


		int lod =
			_getSelectedLod?.Invoke() ??
			0;


		if (!_runtime.TryGetAnimatedWaveSurface(
				lod,
				out _,
				out _,
				out _,
				out AnimatedWaveLodSlice slice))
		{
			return;
		}


		if (_overviewRequested)
		{
			_camera.FrameOverview(
				_viewMode,
				slice);
		}
		else
		{
			_camera.Frame(
				_viewMode,
				slice);
		}


		_overviewRequested =
			false;


		_framed =
			true;
	}


	private void UpdateFarDistance()
	{
		if (_camera == null)
		{
			return;
		}


		bool nested =
			_surface?.LayoutMode ==
			AnimatedWaveSurfaceRenderer.SurfaceLayoutMode.NestedLod;


		_camera.SetMinimumFarDistance(
			nested
				? _nestedFarDistance
				: 0.0f);
	}


	private void RequestView(
		OceanDiagnosticCameraController.ViewMode mode)
	{
		ExitVesselMode();


		SetFreeToggle(
			false);


		_camera?.SetFreeCameraEnabled(
			false);


		_viewMode =
			mode;


		_overviewRequested =
			false;


		_framed =
			false;
	}


	private void ExitVesselMode()
	{
		SetVesselToggle(
			false);


		_camera?.ExitVesselMode();
	}


	private void SetVesselToggle(
		bool value)
	{
		if (_vesselCamera == null)
		{
			return;
		}


		bool previous =
			_syncing;


		_syncing =
			true;


		_vesselCamera.ButtonPressed =
			value;


		_syncing =
			previous;
	}


	private void SetFreeToggle(
		bool value)
	{
		if (_freeCamera == null)
		{
			return;
		}


		bool previous =
			_syncing;


		_syncing =
			true;


		_freeCamera.ButtonPressed =
			value;


		_syncing =
			previous;
	}
}
