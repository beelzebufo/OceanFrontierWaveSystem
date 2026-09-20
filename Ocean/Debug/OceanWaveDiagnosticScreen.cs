using System;
using Godot;
using OceanFrontier.Water.Rendering;
using OceanFrontier.Water.Runtime;
using OceanFrontier.Water.Waves.AnimatedWaves;

namespace OceanFrontier.Water.Debug;

/// <summary>Small runtime control surface; never calls RenderingDevice.</summary>
public partial class OceanWaveDiagnosticScreen : CanvasLayer
{
	private OceanRuntime _runtime;
	private AnimatedWaveSurfaceRenderer _surface;
	private OceanDiagnosticReferenceFrame _reference;
	private OceanDiagnosticCameraController _camera;
	private DirectionalLight3D _sun;
	private OceanDiagnosticLightDirectionGizmo _lightGizmo;
	private FftDisplacementDebugView _inset;
	private RuntimeWaveSettings _draft;
	private double _h0Debounce;
	private bool _syncing;
	private bool _framed;
	private bool _overviewRequested;
	private OceanDiagnosticCameraController.ViewMode _viewMode;
	private Label _status;
	private Label _performance;
	private ulong _lastFrameTicksUsec;
	private double _performanceSampleSeconds;
	private int _performanceSampleFrames;
	private Label _bandWavelength;
	private OptionButton _band;
	private CheckBox _bandEnabled;
	private SpinBox _bandPower;
	private SpinBox _chop;
	private SpinBox _windSpeed;
	private SpinBox _windDirection;
	private SpinBox _windTurbulence;
	private SpinBox _multiplier;
	private OptionButton _debugSource;
	private SpinBox _debugSlice;
	private OptionButton _debugChannel;
	private OptionButton _debugMode;
	private SpinBox _debugGain;
	private SpinBox _lodControl;
	private Label _structure;
	private int _selectedLod;
	private int _waveContentMode;
	private double _statusTimer;

	public override void _Ready()
	{
		_runtime = GetParent() as OceanRuntime;
		if (_runtime == null) { SetProcess(false); return; }
		_surface = _runtime.GetNodeOrNull<AnimatedWaveSurfaceRenderer>("AnimatedWaveSurfaceRenderer");
		_reference = _runtime.GetNodeOrNull<OceanDiagnosticReferenceFrame>("OceanDiagnosticReferenceFrame");
		_camera = _runtime.GetNodeOrNull<OceanDiagnosticCameraController>("Camera3D");
		_sun = _runtime.GetNodeOrNull<DirectionalLight3D>("DiagnosticSun");
		_lightGizmo = _runtime.GetNodeOrNull<OceanDiagnosticLightDirectionGizmo>("OceanDiagnosticLightDirectionGizmo");
		_inset = _runtime.GetNodeOrNull<FftDisplacementDebugView>("FftDisplacementDebugView");
		_selectedLod = _surface?.SpatialLodIndex ?? 0;
		_runtime.FocusOverrideXZ = Vector2.Zero;
		_runtime.FocusOverrideEnabled = true;
		BuildUi();
	}

	private void BuildUi()
	{
		var root = new Control();
		root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		root.MouseFilter = Control.MouseFilterEnum.Ignore;
		AddChild(root);

		var top = new HBoxContainer { Position = new Vector2(8, 8), MouseFilter = Control.MouseFilterEnum.Stop };
		root.AddChild(top);
		AddViewButton(top, "Perspective", OceanDiagnosticCameraController.ViewMode.Perspective);
		AddViewButton(top, "Top", OceanDiagnosticCameraController.ViewMode.Top);
		AddViewButton(top, "Side X", OceanDiagnosticCameraController.ViewMode.SideX);
		AddViewButton(top, "Side Z", OceanDiagnosticCameraController.ViewMode.SideZ);
		Button(top, "Overview", () => { _overviewRequested = true; _framed = false; });
		Check(top, "Free Camera", false).Toggled += value => _camera?.SetFreeCameraEnabled(value);
		var pause = Check(top, "Pause", false);
		pause.Toggled += value => _runtime.SimulationPaused = value;
		var follow = Check(top, "Follow Camera", false);
		follow.Toggled += value => _runtime.FocusOverrideEnabled = !value;

		var panel = new PanelContainer();
		panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
		panel.Position = new Vector2(-315, 44);
		panel.CustomMinimumSize = new Vector2(305, 0);
		panel.Size = new Vector2(305, 510);
		root.AddChild(panel);
		var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(300, 500) };
		panel.AddChild(scroll);
		var column = new VBoxContainer { CustomMinimumSize = new Vector2(280, 0) };
		scroll.AddChild(column);

		Section(column, "SURFACE");
		var surfaceLayout = Option(column, "Surface Layout", "Single LOD", "Nested LOD");
		surfaceLayout.Selected = (int)(_surface?.LayoutMode ??
			AnimatedWaveSurfaceRenderer.SurfaceLayoutMode.SingleLod);
		surfaceLayout.ItemSelected += index => _surface?.SetSurfaceLayout(
			(AnimatedWaveSurfaceRenderer.SurfaceLayoutMode)index);
		var sunProgress = Spin(column, "Sun progress", 0.35, 0, 1, 0.01);
		sunProgress.ValueChanged += value => SetSunProgress((float)value);
		SetSunProgress((float)sunProgress.Value);
		Check(column, "Lighting", true).Toggled += value => _surface?.SetLightingEnabled(value);
		Spin(column, "Roughness", 0.65, 0, 1, 0.01).ValueChanged +=
			value => _surface?.SetDiagnosticRoughness((float)value);
		Check(column, "Show Light Direction", false).Toggled += value =>
		{ if (_lightGizmo != null) _lightGizmo.Visible = value; };
		Check(column, "Show Normal Vectors", false).Toggled +=
			value => _surface?.SetNormalVectorsVisible(value);

		var normalMethod = Option(
										column,
										"Normal Method",
										"Blended XYZ Forward",
										"Crest Per-LOD Forward");

		normalMethod.Selected = _surface?.NormalMethod ?? 0;

		normalMethod.ItemSelected += index => _surface?.SetNormalMethod((int)index);
		_lodControl = Spin(column, "Spatial LOD", _selectedLod, 0, 15, 1);
		_lodControl.ValueChanged += value =>
				{
					_selectedLod = (int)value;
					_surface?.SetSpatialLod(_selectedLod);
					_reference?.SetSpatialLod(_selectedLod);
					_framed = false;
				};
		var waveContent = Option(column, "Wave Content", "Cumulative", "Own Band");
		waveContent.ItemSelected += index =>
		{
			_waveContentMode = (int)index;
			_surface?.SetWaveContent(_waveContentMode);
		};
		var display = Option(column, "Display", "XYZ", "Height Only", "Horizontal Only");
		var horizontal = Spin(column, "Horizontal scale", 1, 0, 4, 0.05);
		var vertical = Spin(column, "Vertical scale", 1, 0, 4, 0.05);
		void UpdateDisplay() => _surface?.SetDisplay(display.Selected, (float)horizontal.Value, (float)vertical.Value);
		display.ItemSelected += _ => UpdateDisplay();
		horizontal.ValueChanged += _ => UpdateDisplay();
		vertical.ValueChanged += _ => UpdateDisplay();
		var frame = Check(column, "Static reference frame", true);
		frame.Toggled += value => { if (_reference != null) _reference.Visible = value; };
		var markers = Check(column, "Surface grid + markers", true);
		markers.Toggled += value => _surface?.SetSurfaceMarkers(value, value);
		var insetToggle = Check(column, "2D inset", true);
		insetToggle.Toggled += value => { if (_inset != null) _inset.Visible = value; };
		_structure = new Label { Text = "GPU topology: waiting..." };
		column.AddChild(_structure);

		Section(column, "SEA STATE");
		Spin(column, "Time scale", 1, 0, 4, 0.05).ValueChanged += value => _runtime.SimulationTimeScale = (float)value;
		_chop = Spin(column, "Chop", 1.6, 0, 4, 0.05);
		_chop.ValueChanged += value =>
		{
			if (_syncing) return;
			var settings = _runtime.GetWaveSettingsSnapshot();
			if (settings == null) return;
			settings.Chop = (float)value;
			if (_draft != null) _draft.Chop = settings.Chop;
			_runtime.RequestWaveSettings(settings);
		};
		_windSpeed = Spin(column, "Wind speed m/s", 12, 0, 80, 0.1);
		_windDirection = Spin(column, "Wind direction °", 0, -180, 180, 1);
		_windTurbulence = Spin(column, "Turbulence", 0.145, 0, 1, 0.005);
		_windSpeed.ValueChanged += value => QueueH0(s => s.WindSpeed = (float)value);
		_windDirection.ValueChanged += value => QueueH0(s => s.WindDirectionDegrees = (float)value);
		_windTurbulence.ValueChanged += value => QueueH0(s => s.WindTurbulence = (float)value);

		Section(column, "SPECTRUM");
		_multiplier = Spin(column, "Multiplier", 1, 0, 10, 0.05);
		_multiplier.ValueChanged += value => QueueH0(s => s.Multiplier = (float)value);
		_band = new OptionButton();
		column.AddChild(_band);
		for (int i = 0; i < 14; i++) _band.AddItem($"Band {i}", i);
		_band.ItemSelected += _ => SyncBandControls();
		_bandWavelength = new Label();
		column.AddChild(_bandWavelength);
		_bandEnabled = Check(column, "Band enabled", true);
		_bandEnabled.Toggled += value =>
		{
			if (_band.Selected >= 0) QueueH0(s => s.Disabled[_band.Selected] = !value);
		};
		_bandPower = Spin(column, "PowerLog10", -5.71, -8, 5, 0.01);
		_bandPower.ValueChanged += value =>
		{
			if (_band.Selected >= 0) QueueH0(s => s.PowerLog10[_band.Selected] = (float)value);
		};
		Button(column, "Reset Wave Settings", () =>
		{
			_draft = null;
			_h0Debounce = 0;
			_runtime.ResetWaveSettings();
			SyncWaveControls(_runtime.GetWaveSettingsSnapshot());
		});

		Section(column, "DEBUG TEXTURE");
		_debugSource = Option(column, "Source", "Raw FFT", "AnimatedWaveField");
		_debugSlice = Spin(column, "Slice", 0, 0, 15, 1);
		_debugChannel = Option(column, "Channel", "X", "Height", "Z");
		_debugMode = Option(column, "Mode", "Grayscale", "Sign", "Magnitude");
		_debugGain = Spin(column, "Gain", 0.5, 0.000001, 10, 0.01);
		if (_inset != null)
		{
			_debugSource.Selected = (int)_inset.Source;
			_debugSlice.Value = _inset.CascadeIndex;
			_debugChannel.Selected = (int)_inset.Channel;
			_debugMode.Selected = Math.Min((int)_inset.Mode, 2);
			_debugGain.Value = _inset.Gain;
		}
		_debugSource.ItemSelected += _ => { UpdateDebugSliceRange(); UpdateInset(); };
		_debugSlice.ValueChanged += _ => UpdateInset();
		_debugChannel.ItemSelected += _ => UpdateInset();
		_debugMode.ItemSelected += _ => UpdateInset();
		_debugGain.ValueChanged += _ => UpdateInset();

		var performancePanel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
		performancePanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft);
		performancePanel.OffsetLeft = 8;
		performancePanel.OffsetTop = -132;
		performancePanel.OffsetRight = 210;
		performancePanel.OffsetBottom = -36;
		root.AddChild(performancePanel);
		_performance = new Label
		{
			Text = "FPS: --\nFrame: -- ms\nQueries: 0\nQuery readback: -- frames",
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		performancePanel.AddChild(_performance);

		_status = new Label();
		_status.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide);
		_status.OffsetTop = -30;
		_status.OffsetBottom = 0;
		root.AddChild(_status);
	}

	private void QueueH0(Action<RuntimeWaveSettings> edit)
	{
		if (_syncing) return;
		_draft ??= _runtime.GetWaveSettingsSnapshot();
		if (_draft == null) return;
		edit(_draft);
		_h0Debounce = 0.13;
	}

	private void SyncWaveControls(RuntimeWaveSettings settings)
	{
		if (settings == null) return;
		_syncing = true;
		_chop.Value = settings.Chop;
		_windSpeed.Value = settings.WindSpeed;
		_windDirection.Value = settings.WindDirectionDegrees;
		_windTurbulence.Value = settings.WindTurbulence;
		_multiplier.Value = settings.Multiplier;
		SyncBandControls(settings);
		_syncing = false;
	}

	private void SyncBandControls() => SyncBandControls(_draft ?? _runtime.GetWaveSettingsSnapshot());
	private void SyncBandControls(RuntimeWaveSettings settings)
	{
		if (settings == null || _band.Selected < 0 || _band.Selected >= settings.PowerLog10.Length) return;
		bool previous = _syncing;
		_syncing = true;
		int index = _band.Selected;
		_bandPower.Value = settings.PowerLog10[index];
		_bandEnabled.ButtonPressed = !settings.Disabled[index];
		float wavelength = MathF.Pow(2.0f, settings.SmallestWavelengthPowerOfTwo + index);
		_bandWavelength.Text = $"Approx wavelength: {wavelength:0.###} m";
		_syncing = previous;
	}

	private void UpdateInset()
	{
		if (_inset == null) return;
		_inset.Source = (FftDisplacementDebugView.DebugSource)_debugSource.Selected;
		_inset.CascadeIndex = (int)_debugSlice.Value;
		_inset.Channel = (FftDisplacementDebugView.DebugChannel)_debugChannel.Selected;
		_inset.Mode = (FftDisplacementDebugView.DebugMode)_debugMode.Selected;
		_inset.Gain = (float)_debugGain.Value;
	}

	private void AddViewButton(HBoxContainer row, string title, OceanDiagnosticCameraController.ViewMode mode)
		=> Button(row, title, () => { _viewMode = mode; _framed = false; });

	private void SetSunProgress(float progress)
	{
		if (_sun == null) return;
		float azimuth = Mathf.Lerp(-Mathf.Pi * 0.5f, Mathf.Pi * 0.5f, progress);
		float elevation = Mathf.DegToRad(8.0f + 57.0f * Mathf.Sin(Mathf.Pi * progress));
		Vector3 direction = new(
			Mathf.Cos(elevation) * Mathf.Cos(azimuth),
			-Mathf.Sin(elevation),
			Mathf.Cos(elevation) * Mathf.Sin(azimuth));
		_sun.LookAt(_sun.GlobalPosition + direction, Vector3.Up);
	}

	public override void _Process(double delta)
	{
		UpdatePerformance();
		if (_runtime == null) return;
		if (!_syncing && !_framed && _runtime.TryGetAnimatedWaveSurface(
			_selectedLod, out _, out _, out _, out AnimatedWaveLodSlice slice))
		{
			if (_overviewRequested)
				_camera?.FrameOverview(_viewMode, slice);
			else
				_camera?.Frame(_viewMode, slice);
			_overviewRequested = false;
			_framed = true;
		}
		if (_lightGizmo != null && _lightGizmo.Visible && _sun != null &&
			_runtime.TryGetAnimatedWaveSurface(_selectedLod, out _, out _, out _, out AnimatedWaveLodSlice lightSlice))
			_lightGizmo.UpdateFrom(_sun, lightSlice);
		if (_chop != null && !_initializedControls &&
			_runtime.TryGetAnimatedWaveSurface(0, out _, out _, out _, out _) &&
			_runtime.GetWaveSettingsSnapshot() is { } startup)
		{
			_initializedControls = true;
			SyncWaveControls(startup);
			_lodControl.MaxValue = Math.Max(0, _runtime.RuntimeAnimatedWaveLodCount - 1);
			_structure.Text = $"FFT {_runtime.FftResolution}² × {_runtime.RuntimeFftCascadeCount}; " +
				$"AWF {_runtime.RuntimeAnimatedWaveResolution}² × {_runtime.RuntimeAnimatedWaveLodCount}; " +
				$"sampling ×{_runtime.RuntimeResolutionMultiplier:0.##}";
			UpdateDebugSliceRange();
		}
		if (_draft != null)
		{
			_h0Debounce -= delta;
			if (_h0Debounce <= 0)
			{
				_runtime.RequestWaveSettings(_draft);
				_draft = null;
			}
		}
		_statusTimer -= delta;
		if (_statusTimer <= 0) { _statusTimer = 0.15; UpdateStatus(); }
	}
	private bool _initializedControls;

	private void UpdatePerformance()
	{
		ulong now = Time.GetTicksUsec();
		if (_lastFrameTicksUsec != 0)
		{
			_performanceSampleSeconds += (now - _lastFrameTicksUsec) / 1_000_000.0;
			_performanceSampleFrames++;
			if (_performanceSampleSeconds >= 0.25)
			{
				_runtime.PointQueries.GetDiagnostics(out int queries,
					out int readbackFrames, out bool hasResult);
				_performance.Text = $"FPS: {_performanceSampleFrames / _performanceSampleSeconds:0.0}\n" +
					$"Frame: {_performanceSampleSeconds * 1000.0 / _performanceSampleFrames:0.00} ms\n" +
					$"Queries: {queries}\n" +
					$"Query readback: {(hasResult ? readbackFrames.ToString() : "--")} frames";
				_performanceSampleSeconds = 0;
				_performanceSampleFrames = 0;
			}
		}
		_lastFrameTicksUsec = now;
	}

	private void UpdateDebugSliceRange()
	{
		int count = _debugSource.Selected == 0
			? _runtime.RuntimeFftCascadeCount
			: _runtime.RuntimeAnimatedWaveLodCount;
		if (count > 0) _debugSlice.MaxValue = count - 1;
	}

	private void UpdateStatus()
	{
		bool nested = _surface?.LayoutMode == AnimatedWaveSurfaceRenderer.SurfaceLayoutMode.NestedLod;
		int diagnosticLod = nested
			? Math.Clamp(_selectedLod, 0, Math.Max(0, _runtime.RuntimeAnimatedWaveLodCount - 1))
			: _selectedLod;
		if (!_runtime.TryGetAnimatedWaveSurface(diagnosticLod, out _, out _, out _, out AnimatedWaveLodSlice slice))
		{ _status.Text = "Ocean diagnostics: waiting for GPU"; return; }
		RuntimeWaveSettings settings = _runtime.GetWaveSettingsSnapshot();
		string content = _waveContentMode == 0 ? "Cumulative" :
			_selectedLod + 1 < _runtime.RuntimeAnimatedWaveLodCount
				? $"Own Band L{_selectedLod}-L{_selectedLod + 1}"
				: "Own Band: coarsest remainder";
		string layoutStatus = $"AWF LOD{diagnosticLod} | size {slice.WorldSize:0.##}m";
		if (nested && _runtime.TryGetAnimatedWaveSurface(
			_runtime.RuntimeAnimatedWaveLodCount - 1, out _, out _, out _, out AnimatedWaveLodSlice outerSlice))
			layoutStatus = $"Nested LOD0-{_runtime.RuntimeAnimatedWaveLodCount - 1} | " +
				$"{_surface.NestedTileCount} tiles | outer size {outerSlice.WorldSize:0.##}m | " +
				$"diagnostic LOD{diagnosticLod}";
		_status.Text = $"{layoutStatus} | texel {slice.TexelWidth:0.###}m | {content} | " +
			$"center ({slice.CenterXZ.X:0.##},{slice.CenterXZ.Y:0.##}) | t {_runtime.SimulationTime:0.0}s | " +
			$"chop {settings.Chop:0.00} | H0 rev {_runtime.AppliedH0Revision}" +
			(_runtime.H0Pending || _draft != null ? " pending" : "");
		if (_inset == null) return;
		int index = Math.Max(0, _inset.CascadeIndex);
		if (_inset.Source == FftDisplacementDebugView.DebugSource.RawFft)
		{
			float domain = 0.5f * MathF.Pow(2.0f, index);
			_inset.PhysicalMetadata = $"domain {domain:0.###}m | minλ {domain / 8.0f:0.###}m";
		}
		else if (_runtime.TryGetAnimatedWaveSurface(index, out _, out _, out _, out AnimatedWaveLodSlice debugSlice))
			_inset.PhysicalMetadata = $"size {debugSlice.WorldSize:0.###}m | texel {debugSlice.TexelWidth:0.###}m | λ {debugSlice.MinWavelength:0.###}-{debugSlice.MaxWavelength:0.###}m";
	}

	private static void Section(VBoxContainer parent, string text) => parent.AddChild(new Label { Text = text });
	private static Button Button(Node parent, string text, Action action)
	{
		var button = new Button { Text = text };
		button.Pressed += action;
		parent.AddChild(button);
		return button;
	}
	private static CheckBox Check(Node parent, string text, bool selected)
	{
		var box = new CheckBox { Text = text, ButtonPressed = selected };
		parent.AddChild(box);
		return box;
	}
	private static OptionButton Option(VBoxContainer parent, string label, params string[] items)
	{
		parent.AddChild(new Label { Text = label });
		var option = new OptionButton();
		foreach (string item in items) option.AddItem(item);
		parent.AddChild(option);
		return option;
	}
	private static SpinBox Spin(VBoxContainer parent, string label, double initial, double min, double max, double step)
	{
		var row = new HBoxContainer();
		row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(145, 0) });
		var spin = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = initial,
			CustomMinimumSize = new Vector2(110, 0) };
		row.AddChild(spin);
		parent.AddChild(row);
		return spin;
	}
}
