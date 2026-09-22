using Godot;
using OceanFrontier.Water.Rendering;
using OceanFrontier.Water.Runtime;

namespace OceanFrontier.Water.Debug;

/// <summary>
/// Modular runtime diagnostics host.
///
/// Subsystem panels own their controls and runtime interaction.
/// This class only resolves shared scene services, lays out the tabs,
/// and ticks the panels.
/// </summary>
public partial class OceanWaveDiagnosticScreen : CanvasLayer
{
	private OceanRuntime _runtime;

	private AnimatedWaveSurfaceRenderer _surface;

	private OceanDiagnosticReferenceFrame _reference;

	private OceanPointQueryDiagnostic _pointQueries;

	private OceanDiagnosticCameraController _camera;

	private DirectionalLight3D _sun;

	private RigidBody3D _diagnosticHull;

	private OceanDiagnosticLightDirectionGizmo _lightGizmo;

	private FftDisplacementDebugView _inset;


	private OceanDiagnosticOceanPanel _oceanPanel;

	private OceanDiagnosticPhysicsPanel _physicsPanel;

	private OceanDiagnosticCameraPanel _cameraPanel;

	private OceanDiagnosticLightingPanel _lightingPanel;

	private OceanDiagnosticDiagnosticsPanel _diagnosticsPanel;

	private OceanDiagnosticPerformanceHud _performanceHud;


	private PanelContainer _controlsPanel;

	private Button _controlsToggle;


	public override void _Ready()
	{
		_runtime =
			GetParent() as OceanRuntime;


		if (_runtime == null)
		{
			GD.PushError(
				"OceanWaveDiagnosticScreen must be a direct child of OceanRuntime.");


			SetProcess(
				false);


			return;
		}


		ResolveSceneServices();


		ConfigureDiagnosticRuntimeDefaults();


		BuildUi();
	}


	private void ResolveSceneServices()
	{
		_surface =
			_runtime.GetNodeOrNull<AnimatedWaveSurfaceRenderer>(
				"AnimatedWaveSurfaceRenderer");


		_reference =
			_runtime.GetNodeOrNull<OceanDiagnosticReferenceFrame>(
				"OceanDiagnosticReferenceFrame");


		_pointQueries =
			_runtime.GetNodeOrNull<OceanPointQueryDiagnostic>(
				"OceanPointQueryDiagnostic");


		_camera =
			_runtime.GetNodeOrNull<OceanDiagnosticCameraController>(
				"Camera3D");


		_sun =
			_runtime.GetNodeOrNull<DirectionalLight3D>(
				"DiagnosticSun");


		_diagnosticHull =
			_runtime.GetNodeOrNull<RigidBody3D>(
				"DiagnosticBuoyancyHull");


		_lightGizmo =
			_runtime.GetNodeOrNull<OceanDiagnosticLightDirectionGizmo>(
				"OceanDiagnosticLightDirectionGizmo");


		_inset =
			_runtime.GetNodeOrNull<FftDisplacementDebugView>(
				"FftDisplacementDebugView");
	}


	private void ConfigureDiagnosticRuntimeDefaults()
	{
		_runtime.FocusOverrideXZ =
			Vector2.Zero;


		_runtime.FocusOverrideEnabled =
			true;


		_runtime.LodScaleOverride =
			1.0f;


		_runtime.LodScaleOverrideEnabled =
			true;


		if (_inset != null)
		{
			_inset.Visible =
				false;
		}
	}


	private void BuildUi()
	{
		var root =
			new Control
			{
				MouseFilter =
					Control.MouseFilterEnum.Ignore,
			};


		root.SetAnchorsPreset(
			Control.LayoutPreset.FullRect);


		AddChild(
			root);


		_controlsPanel =
			new PanelContainer
			{
				AnchorLeft =
					1.0f,

				AnchorTop =
					0.0f,

				AnchorRight =
					1.0f,

				AnchorBottom =
					1.0f,

				OffsetLeft =
					-430.0f,

				OffsetTop =
					42.0f,

				OffsetRight =
					-8.0f,

				OffsetBottom =
					-36.0f,

				MouseFilter =
					Control.MouseFilterEnum.Stop,
			};


		root.AddChild(
			_controlsPanel);


		_controlsToggle =
			new Button
			{
				AnchorLeft =
					1.0f,

				AnchorTop =
					0.0f,

				AnchorRight =
					1.0f,

				AnchorBottom =
					0.0f,

				OffsetLeft =
					-156.0f,

				OffsetTop =
					8.0f,

				OffsetRight =
					-8.0f,

				OffsetBottom =
					36.0f,

				Text =
					"Hide controls",

				MouseFilter =
					Control.MouseFilterEnum.Stop,
			};


		_controlsToggle.Pressed +=
			ToggleControls;


		root.AddChild(
			_controlsToggle);


		var tabs =
			new TabContainer
			{
				SizeFlagsHorizontal =
					Control.SizeFlags.ExpandFill,

				SizeFlagsVertical =
					Control.SizeFlags.ExpandFill,
			};


		_controlsPanel.AddChild(
			tabs);


		VBoxContainer oceanTab =
			OceanDiagnosticUi.AddTab(
				tabs,
				"Ocean");


		VBoxContainer physicsTab =
			OceanDiagnosticUi.AddTab(
				tabs,
				"Physics");


		VBoxContainer cameraTab =
			OceanDiagnosticUi.AddTab(
				tabs,
				"Camera");


		VBoxContainer lightingTab =
			OceanDiagnosticUi.AddTab(
				tabs,
				"Lighting");


		VBoxContainer diagnosticsTab =
			OceanDiagnosticUi.AddTab(
				tabs,
				"Diagnostics");


		_oceanPanel =
			new OceanDiagnosticOceanPanel();


		_oceanPanel.Initialize(
			oceanTab,
			_runtime,
			_surface,
			_reference,
			_pointQueries);


		_physicsPanel =
			new OceanDiagnosticPhysicsPanel();


		_physicsPanel.Initialize(
			physicsTab,
			_runtime,
			_diagnosticHull);


		_cameraPanel =
			new OceanDiagnosticCameraPanel();


		_cameraPanel.Initialize(
			cameraTab,
			_runtime,
			_surface,
			_camera,
			_diagnosticHull,
			() =>
				_oceanPanel.SelectedLod);


		_lightingPanel =
			new OceanDiagnosticLightingPanel();


		_lightingPanel.Initialize(
			lightingTab,
			_runtime,
			_surface,
			_sun,
			_lightGizmo,
			() =>
				_oceanPanel.SelectedLod);


		_diagnosticsPanel =
			new OceanDiagnosticDiagnosticsPanel();


		_diagnosticsPanel.Initialize(
			diagnosticsTab,
			_runtime,
			_surface,
			_inset,
			_diagnosticHull);


		_performanceHud =
			new OceanDiagnosticPerformanceHud();


		_performanceHud.Initialize(
			root,
			_runtime,
			_diagnosticHull);
	}


	public override void _Process(
		double delta)
	{
		_oceanPanel?.Tick(
			delta);


		_physicsPanel?.Tick(
			delta);


		_cameraPanel?.Tick(
			delta);


		_lightingPanel?.Tick(
			delta);


		_diagnosticsPanel?.Tick(
			delta);


		_performanceHud?.Tick(
			delta);
	}


	private void ToggleControls()
	{
		if (_controlsPanel == null ||
			_controlsToggle == null)
		{
			return;
		}


		_controlsPanel.Visible =
			!_controlsPanel.Visible;


		_controlsToggle.Text =
			_controlsPanel.Visible
				? "Hide controls"
				: "Show controls";
	}
}
