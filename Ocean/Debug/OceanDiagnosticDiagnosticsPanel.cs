using System;
using Godot;
using OceanFrontier.Water.Runtime;
using OceanFrontier.Water.Waves.AnimatedWaves;
using OceanFrontier.Water.Waves.Foam;

namespace OceanFrontier.Water.Debug;

internal sealed class OceanDiagnosticDiagnosticsPanel
{
	private OceanRuntime _runtime;

	private FftDisplacementDebugView _inset;


	private OptionButton _debugSource;

	private SpinBox _debugSlice;

	private OptionButton _debugChannel;

	private OptionButton _debugMode;

	private SpinBox _debugGain;


	internal void Initialize(
		VBoxContainer parent,
		OceanRuntime runtime,
		OceanFrontier.Water.Rendering.AnimatedWaveSurfaceRenderer surface,
		FftDisplacementDebugView inset,
		RigidBody3D diagnosticHull)
	{
		_runtime =
			runtime;

		_inset =
			inset;


		if (_inset != null)
		{
			_inset.Visible =
				false;
		}


		Build(
			parent);
	}


	private void Build(
		VBoxContainer parent)
	{
		OceanDiagnosticUi.Header(
			parent,
			"Diagnostics");


		OceanDiagnosticUi.Info(
			parent,
			"FPS, frame time and GPU query latency stay visible in the lower-left performance HUD even when this panel is hidden.");


		var pointQueries =
			OceanDiagnosticUi.Check(
				parent,
				"Show GPU point-query markers",
				false);


		pointQueries.Toggled +=
			value =>
			{
				OceanPointQueryDiagnostic diagnostic =
					_runtime.GetNodeOrNull<OceanPointQueryDiagnostic>(
						"OceanPointQueryDiagnostic");


				if (diagnostic != null)
				{
					diagnostic.Visible =
						value;
				}
			};


		OceanPointQueryDiagnostic pointQueryDiagnostic =
			_runtime.GetNodeOrNull<OceanPointQueryDiagnostic>(
				"OceanPointQueryDiagnostic");


		if (pointQueryDiagnostic != null)
		{
			pointQueryDiagnostic.Visible =
				pointQueries.ButtonPressed;
		}


		VBoxContainer waves2D =
			OceanDiagnosticUi.Foldout(
				parent,
				"2D Waves",
				expanded: false);


		Build2DWaveControls(
			waves2D);
		BuildPacketControls(parent);


		OceanDiagnosticUi.Info(
			parent,
			"2D wave inspection is diagnostic-only. It never becomes the production render or physics path.");
	}


	private void BuildPacketControls(VBoxContainer parent)
	{
		var panel = OceanDiagnosticUi.Foldout(parent, "Radial Gerstner packet", false);
		var x = OceanDiagnosticUi.Spin(panel, "Origin X", 0, -100000, 100000, 1);
		var z = OceanDiagnosticUi.Spin(panel, "Origin Z", 0, -100000, 100000, 1);
		var isolate = OceanDiagnosticUi.Check(panel, "Isolate packets (disable FFT)", false);
		float savedMultiplier = 1;
		isolate.Toggled += enabled =>
		{
			RuntimeWaveSettings settings = _runtime.GetWaveSettingsSnapshot();
			if (enabled) savedMultiplier = settings.Multiplier;
			settings.Multiplier = enabled ? 0 : savedMultiplier;
			_runtime.RequestWaveSettings(settings);
		};
		var one = new Button { Text = "Trigger one packet" };
		var two = new Button { Text = "Trigger two overlapping packets" };
		var clear = new Button { Text = "Clear all packets" };
		panel.AddChild(one); panel.AddChild(two); panel.AddChild(clear);
		one.Pressed += () => _runtime.TriggerGerstnerPacket(new Vector2((float)x.Value, (float)z.Value));
		two.Pressed += () =>
		{
			_runtime.TriggerGerstnerPacket(new Vector2((float)x.Value - 8, (float)z.Value));
			_runtime.TriggerGerstnerPacket(new Vector2((float)x.Value + 8, (float)z.Value));
		};
		clear.Pressed += _runtime.ClearGerstnerPackets;
		var direction = OceanDiagnosticUi.Spin(panel, "Direction angle (degrees)", 0, 0, 360, 1);
		var halfAngle = OceanDiagnosticUi.Spin(panel, "Sector half-angle", 45, 1, 180, 1);
		var feather = OceanDiagnosticUi.Spin(panel, "Angular feather", 10, 0, 45, 1);
		halfAngle.ValueChanged += value => { feather.MaxValue = value; feather.Value = Math.Min(feather.Value, value); };
		var directional = new Button { Text = "Trigger directional packet" };
		panel.AddChild(directional);
		directional.Pressed += () =>
		{
			float angle = Mathf.DegToRad((float)direction.Value);
			_runtime.CreateGerstnerPacket(new GerstnerWavePacketInput
			{
				WorldPositionXZ = new Vector2((float)x.Value, (float)z.Value), StartTime = _runtime.OceanTime,
				DirectionXZ = new Vector2(MathF.Cos(angle), MathF.Sin(angle)),
				SectorHalfAngleDegrees = (float)halfAngle.Value, AngularFeatherDegrees = (float)feather.Value,
			});
		};
		OceanDiagnosticUi.Info(panel, "Direction: 0° +X, 90° +Z. Half-angle 180° is a full circle; 45° gives a 90° sector.");
		OceanDiagnosticUi.Info(panel,
			"4 crests, wavelength 8 m, lifetime 40 s; full train emerges after about 18 s. " +
			"Use ocean pause/time scale, AWF Height view and GPU query markers above. " +
			"Move the camera to check snapped LOD shifts. Uncheck isolation to restore FFT.");
	}

	private void Build2DWaveControls(
		VBoxContainer parent)
	{
		var visible =
			OceanDiagnosticUi.Check(
				parent,
				"Show 2D wave view",
				false);


		visible.Toggled +=
			value =>
			{
				if (_inset != null)
				{
					_inset.Visible =
						value;
				}
			};


		VBoxContainer foamRuntime =
			OceanDiagnosticUi.Foldout(
				parent,
				"Foam Runtime",
				expanded: false);


		BuildFoamRuntimeControls(
			foamRuntime);


		_debugSource =
			OceanDiagnosticUi.Option(
				parent,
				"Source",
				"Raw FFT",
				"AnimatedWaveField",
				"AnimatedWaveDerivativeField",
				"OceanFoamField (Foam-1A)");


		_debugSlice =
			OceanDiagnosticUi.Spin(
				parent,
				"Slice",
				0.0,
				0.0,
				15.0,
				1.0);


		_debugChannel =
			OceanDiagnosticUi.Option(
				parent,
				"Channel",
				"X",
				"Height",
				"Z");


		_debugMode =
			OceanDiagnosticUi.Option(
				parent,
				"Display mode",
				"Grayscale",
				"Sign",
				"Magnitude",
				"UV/material diagnostic",
				"Normal RGB",
				"Jacobian",
				"Scalar [0,1]");


		_debugGain =
			OceanDiagnosticUi.Spin(
				parent,
				"Gain",
				0.5,
				0.000001,
				10.0,
				0.01);


		if (_inset != null)
		{
			_debugSource.Selected =
				(int)_inset.Source;


			_debugSlice.Value =
				_inset.CascadeIndex;


			_debugChannel.Selected =
				(int)_inset.Channel;


			_debugMode.Selected =
				(int)_inset.Mode;


			_debugGain.Value =
				_inset.Gain;
		}


		_debugSource.ItemSelected +=
			_ =>
			{
				if (_debugSource.Selected ==
					(int)FftDisplacementDebugView.DebugSource.OceanFoamField)
				{
					_debugChannel.Selected =
						(int)FftDisplacementDebugView.DebugChannel.DisplacementX;

					_debugMode.Selected =
						(int)FftDisplacementDebugView.DebugMode.Scalar01;
				}

				UpdateDebugSliceRange();

				UpdateInset();
			};


		_debugSlice.ValueChanged +=
			_ =>
				UpdateInset();


		_debugChannel.ItemSelected +=
			_ =>
				UpdateInset();


		_debugMode.ItemSelected +=
			_ =>
				UpdateInset();


		_debugGain.ValueChanged +=
			_ =>
				UpdateInset();


		UpdateDebugSliceRange();


		UpdateInset();
	}


	private void BuildFoamRuntimeControls(
		VBoxContainer parent)
	{
		OceanFoamSettings settings =
			_runtime.Foam;


		var enabled =
			OceanDiagnosticUi.Check(
				parent,
				"Foam Simulation Enabled",
				settings?.Enabled ?? true);


		var strength =
			OceanDiagnosticUi.Spin(
				parent,
				"Foam Strength",
				settings?.WaveFoamStrength ??
					OceanFoamSettings.DefaultWaveFoamStrength,
				0.0,
				5.0,
				0.01);


		var coverage =
			OceanDiagnosticUi.Spin(
				parent,
				"Foam Coverage",
				settings?.WaveFoamCoverage ??
					OceanFoamSettings.DefaultWaveFoamCoverage,
				0.0,
				1.0,
				0.01);


		var fadeRate =
			OceanDiagnosticUi.Spin(
				parent,
				"Foam Fade Rate",
				settings?.FadeRate ??
					OceanFoamSettings.DefaultFadeRate,
				0.0,
				20.0,
				0.01);


		var simulationFrequency =
			OceanDiagnosticUi.Spin(
				parent,
				"Foam Simulation Hz",
				settings?.SimulationFrequency ??
					OceanFoamSettings.DefaultSimulationFrequency,
				1.0,
				200.0,
				1.0,
				" Hz");


		var injectFoamSpot =
			OceanDiagnosticUi.Check(
				parent,
				"Inject diagnostic foam spot at camera XZ",
				settings?.InjectWorldSpaceSpot ?? false);


		enabled.Toggled +=
			value =>
				EnsureFoamSettings().Enabled = value;


		strength.ValueChanged +=
			value =>
				EnsureFoamSettings().WaveFoamStrength = (float)value;


		coverage.ValueChanged +=
			value =>
				EnsureFoamSettings().WaveFoamCoverage = (float)value;


		fadeRate.ValueChanged +=
			value =>
				EnsureFoamSettings().FadeRate = (float)value;


		simulationFrequency.ValueChanged +=
			value =>
				EnsureFoamSettings().SimulationFrequency = (float)value;


		injectFoamSpot.Toggled +=
			value =>
			{
				OceanFoamSettings foam =
					EnsureFoamSettings();

				if (value)
				{
					Camera3D camera =
						_runtime.GetViewport()?.GetCamera3D();

					if (camera != null)
					{
						Vector3 position = camera.GlobalPosition;
						foam.DiagnosticSpotWorldXZ =
							new Vector2(position.X, position.Z);
					}
				}

				foam.InjectWorldSpaceSpot = value;
			};


		OceanDiagnosticUi.Info(
			parent,
			"The diagnostic spot is injected once on enable. Toggle off/on to re-arm it; then move the camera or change the whole-stack LOD scale.");
	}


	private OceanFoamSettings EnsureFoamSettings()
	{
		_runtime.Foam ??=
			new OceanFoamSettings();


		return _runtime.Foam;
	}


	internal void Tick(
		double delta)
	{
		UpdateInsetMetadata();
	}


	private void UpdateInset()
	{
		if (_inset == null ||
			_debugSource == null)
		{
			return;
		}


		_inset.Source =
			(FftDisplacementDebugView.DebugSource)
			_debugSource.Selected;


		_inset.CascadeIndex =
			(int)_debugSlice.Value;


		_inset.Channel =
			(FftDisplacementDebugView.DebugChannel)
			_debugChannel.Selected;


		_inset.Mode =
			(FftDisplacementDebugView.DebugMode)
			_debugMode.Selected;


		_inset.Gain =
			(float)_debugGain.Value;
	}


	private void UpdateDebugSliceRange()
	{
		if (_debugSource == null ||
			_debugSlice == null ||
			_runtime == null)
		{
			return;
		}


		int count =
			_debugSource.Selected ==
			0
				? _runtime.RuntimeFftCascadeCount
				: _runtime.RuntimeAnimatedWaveLodCount;


		_debugSlice.MaxValue =
			Math.Max(
				0,
				count -
					1);
	}


	private void UpdateInsetMetadata()
	{
		if (_inset == null ||
			!_inset.Visible ||
			_runtime == null)
		{
			return;
		}


		int index =
			Math.Max(
				0,
				_inset.CascadeIndex);


		if (_inset.Source ==
			FftDisplacementDebugView.DebugSource.RawFft)
		{
			float domain =
				0.5f *
				MathF.Pow(
					2.0f,
					index);


			_inset.PhysicalMetadata =
				$"domain {domain:0.###}m | " +
				$"minλ {domain / 8.0f:0.###}m";

			return;
		}


		if (_runtime.TryGetAnimatedWaveSurface(
				index,
				out _,
				out _,
				out _,
				out AnimatedWaveLodSlice slice))
		{
			_inset.PhysicalMetadata =
				$"size {slice.WorldSize:0.###}m | " +
				$"texel {slice.TexelWidth:0.###}m | " +
				$"λ {slice.MinWavelength:0.###}-" +
				$"{slice.MaxWavelength:0.###}m";
		}
	}
}
