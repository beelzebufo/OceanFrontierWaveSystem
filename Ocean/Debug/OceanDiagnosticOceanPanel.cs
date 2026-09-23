using System;
using Godot;
using OceanFrontier.Water.Rendering;
using OceanFrontier.Water.Runtime;
using OceanFrontier.Water.Waves.AnimatedWaves;

namespace OceanFrontier.Water.Debug;

internal sealed class OceanDiagnosticOceanPanel
{
	private OceanRuntime _runtime;

	private AnimatedWaveSurfaceRenderer _surface;

	private OceanDiagnosticReferenceFrame _reference;

	private OceanPointQueryDiagnostic _pointQueries;


	private RuntimeWaveSettings _draft;

	private double _h0Debounce;

	private double _statusTimer;

	private bool _syncing;

	private bool _initialized;


	private SpinBox _chop;

	private SpinBox _windSpeed;

	private SpinBox _windDirection;

	private SpinBox _windTurbulence;

	private SpinBox _multiplier;
	private SpinBox _shallowAttenuation;
	private SpinBox _shallowMaximumDepth;

	private SpinBox _lodControl;

	private OptionButton _band;

	private CheckBox _bandEnabled;

	private SpinBox _bandPower;

	private Label _bandWavelength;

	private Label _structure;

	private Label _status;


	private int _selectedLod;

	private int _waveContentMode;


	internal int SelectedLod =>
		_selectedLod;


	internal void Initialize(
		VBoxContainer parent,
		OceanRuntime runtime,
		AnimatedWaveSurfaceRenderer surface,
		OceanDiagnosticReferenceFrame reference,
		OceanPointQueryDiagnostic pointQueries)
	{
		_runtime =
			runtime;

		_surface =
			surface;

		_reference =
			reference;

		_pointQueries =
			pointQueries;


		_selectedLod =
			_surface?.SpatialLodIndex ??
			0;


		Build(
			parent);
	}


	private void Build(
		VBoxContainer parent)
	{
		OceanDiagnosticUi.Header(
			parent,
			"Sea State");


		var pause =
			OceanDiagnosticUi.Check(
				parent,
				"Pause ocean simulation",
				false);


		pause.Toggled +=
			value =>
				_runtime.SimulationPaused =
					value;


		OceanDiagnosticUi.Spin(
			parent,
			"Time scale",
			1.0,
			0.0,
			4.0,
			0.05)
			.ValueChanged +=
				value =>
					_runtime.SimulationTimeScale =
						(float)value;


		_chop =
			OceanDiagnosticUi.Spin(
				parent,
				"Chop",
				1.6,
				0.0,
				4.0,
				0.05);


		_chop.ValueChanged +=
			value =>
			{
				if (_syncing)
				{
					return;
				}


				RuntimeWaveSettings settings =
					_runtime.GetWaveSettingsSnapshot();


				if (settings == null)
				{
					return;
				}


				settings.Chop =
					(float)value;


				if (_draft != null)
				{
					_draft.Chop =
						settings.Chop;
				}


				_runtime.RequestWaveSettings(
					settings);
			};


		_windSpeed =
			OceanDiagnosticUi.Spin(
				parent,
				"Wind speed",
				12.0,
				0.0,
				80.0,
				0.1,
				" m/s");


		_windDirection =
			OceanDiagnosticUi.Spin(
				parent,
				"Wind direction",
				0.0,
				-180.0,
				180.0,
				1.0,
				"°");


		_windTurbulence =
			OceanDiagnosticUi.Spin(
				parent,
				"Turbulence",
				0.145,
				0.0,
				1.0,
				0.005);


		_multiplier =
			OceanDiagnosticUi.Spin(
				parent,
				"Spectrum multiplier",
				1.0,
				0.0,
				10.0,
				0.05);

		_shallowAttenuation =
			OceanDiagnosticUi.Spin(
				parent,
				"Shallow attenuation",
				0.95,
				0.0,
				1.0,
				0.01);

		_shallowMaximumDepth =
			OceanDiagnosticUi.Spin(
				parent,
				"Shallow maximum depth",
				1000.0,
				1.0,
				1000.0,
				1.0,
				" m");

		void UpdateShallowSettings()
		{
			if (_syncing) return;
			RuntimeWaveSettings settings = _runtime.GetWaveSettingsSnapshot();
			if (settings == null) return;
			settings.ShallowWaterAttenuation = (float)_shallowAttenuation.Value;
			settings.ShallowWaterMaximumDepth = (float)_shallowMaximumDepth.Value;
			if (_draft != null)
			{
				_draft.ShallowWaterAttenuation = settings.ShallowWaterAttenuation;
				_draft.ShallowWaterMaximumDepth = settings.ShallowWaterMaximumDepth;
			}
			_runtime.RequestWaveSettings(settings);
		}

		_shallowAttenuation.ValueChanged += _ => UpdateShallowSettings();
		_shallowMaximumDepth.ValueChanged += _ => UpdateShallowSettings();


		_windSpeed.ValueChanged +=
			value =>
				QueueH0(
					settings =>
						settings.WindSpeed =
							(float)value);


		_windDirection.ValueChanged +=
			value =>
				QueueH0(
					settings =>
						settings.WindDirectionDegrees =
							(float)value);


		_windTurbulence.ValueChanged +=
			value =>
				QueueH0(
					settings =>
						settings.WindTurbulence =
							(float)value);


		_multiplier.ValueChanged +=
			value =>
				QueueH0(
					settings =>
						settings.Multiplier =
							(float)value);


		OceanDiagnosticUi.Button(
			parent,
			"Reset wave settings",
			() =>
			{
				_draft =
					null;


				_h0Debounce =
					0.0;


				_runtime.ResetWaveSettings();


				SyncWaveControls(
					_runtime.GetWaveSettingsSnapshot());
			});


		_status =
			OceanDiagnosticUi.Info(
				parent,
				"Ocean: waiting for GPU...");


		VBoxContainer lod =
			OceanDiagnosticUi.Foldout(
				parent,
				"Surface / LOD",
				expanded: false);


		BuildLodControls(
			lod);


		VBoxContainer bands =
			OceanDiagnosticUi.Foldout(
				parent,
				"Spectrum Bands",
				expanded: false);


		BuildBandControls(
			bands);
	}


	private void BuildLodControls(
		VBoxContainer parent)
	{
		var layout =
			OceanDiagnosticUi.Option(
				parent,
				"Surface layout",
				"Single LOD",
				"Nested LOD");


		layout.Selected =
			(int)(
				_surface?.LayoutMode ??
				AnimatedWaveSurfaceRenderer.SurfaceLayoutMode.SingleLod);


		layout.ItemSelected +=
			index =>
				_surface?.SetSurfaceLayout(
					(AnimatedWaveSurfaceRenderer.SurfaceLayoutMode)index);


		_lodControl =
			OceanDiagnosticUi.Spin(
				parent,
				"Spatial LOD",
				_selectedLod,
				0.0,
				15.0,
				1.0);


		_lodControl.ValueChanged +=
			value =>
			{
				_selectedLod =
					(int)value;


				_surface?.SetSpatialLod(
					_selectedLod);


				_pointQueries?.SetSpatialLod(
					_selectedLod);


				_reference?.SetSpatialLod(
					_selectedLod);
			};


		var waveContent =
			OceanDiagnosticUi.Option(
				parent,
				"Wave content",
				"Cumulative",
				"Own band");


		waveContent.ItemSelected +=
			index =>
			{
				_waveContentMode =
					(int)index;


				_surface?.SetWaveContent(
					_waveContentMode);
			};


		var display =
			OceanDiagnosticUi.Option(
				parent,
				"Display",
				"XYZ",
				"Height only",
				"Horizontal only");


		var horizontal =
			OceanDiagnosticUi.Spin(
				parent,
				"Horizontal scale",
				1.0,
				0.0,
				4.0,
				0.05);


		var vertical =
			OceanDiagnosticUi.Spin(
				parent,
				"Vertical scale",
				1.0,
				0.0,
				4.0,
				0.05);


		void UpdateDisplay()
		{
			_surface?.SetDisplay(
				display.Selected,
				(float)horizontal.Value,
				(float)vertical.Value);
		}


		display.ItemSelected +=
			_ =>
				UpdateDisplay();


		horizontal.ValueChanged +=
			_ =>
				UpdateDisplay();


		vertical.ValueChanged +=
			_ =>
				UpdateDisplay();


		var frame =
			OceanDiagnosticUi.Check(
				parent,
				"Static reference frame",
				true);


		frame.Toggled +=
			value =>
			{
				if (_reference != null)
				{
					_reference.Visible =
						value;
				}
			};


		var markers =
			OceanDiagnosticUi.Check(
				parent,
				"Surface grid + markers",
				false);


		markers.Toggled +=
			value =>
				_surface?.SetSurfaceMarkers(
					value,
					value);


		_structure =
			OceanDiagnosticUi.Info(
				parent,
				"GPU topology: waiting...");
	}


	private void BuildBandControls(
		VBoxContainer parent)
	{
		_band =
			OceanDiagnosticUi.Option(
				parent,
				"Band",
				"Band 0");


		_band.Clear();


		for (int i = 0;
			 i < 14;
			 i++)
		{
			_band.AddItem(
				$"Band {i}",
				i);
		}


		_band.ItemSelected +=
			_ =>
				SyncBandControls();


		_bandWavelength =
			OceanDiagnosticUi.Info(
				parent,
				"Approx wavelength: --");


		_bandEnabled =
			OceanDiagnosticUi.Check(
				parent,
				"Band enabled",
				true);


		_bandEnabled.Toggled +=
			value =>
			{
				if (_band.Selected >=
					0)
				{
					QueueH0(
						settings =>
							settings.Disabled[
								_band.Selected] =
								!value);
				}
			};


		_bandPower =
			OceanDiagnosticUi.Spin(
				parent,
				"Power log10",
				-5.71,
				-8.0,
				5.0,
				0.01);


		_bandPower.ValueChanged +=
			value =>
			{
				if (_band.Selected >=
					0)
				{
					QueueH0(
						settings =>
							settings.PowerLog10[
								_band.Selected] =
								(float)value);
				}
			};
	}


	internal void Tick(
		double delta)
	{
		if (!_initialized &&
			_runtime.TryGetAnimatedWaveSurface(
				0,
				out _,
				out _,
				out _,
				out _) &&
			_runtime.GetWaveSettingsSnapshot() is
				{ } startup)
		{
			_initialized =
				true;


			SyncWaveControls(
				startup);


			if (_lodControl != null)
			{
				_lodControl.MaxValue =
					Math.Max(
						0,
						_runtime.RuntimeAnimatedWaveLodCount -
							1);
			}


			if (_structure != null)
			{
				_structure.Text =
					$"FFT {_runtime.FftResolution}² × " +
					$"{_runtime.RuntimeFftCascadeCount}\n" +
					$"AWF {_runtime.RuntimeAnimatedWaveResolution}² × " +
					$"{_runtime.RuntimeAnimatedWaveLodCount}\n" +
					$"Sampling ×{_runtime.RuntimeResolutionMultiplier:0.##}";
			}
		}


		if (_draft != null)
		{
			_h0Debounce -=
				delta;


			if (_h0Debounce <=
				0.0)
			{
				_runtime.RequestWaveSettings(
					_draft);


				_draft =
					null;
			}
		}


		_statusTimer -=
			delta;


		if (_statusTimer <=
			0.0)
		{
			_statusTimer =
				0.15;


			UpdateStatus();
		}
	}


	private void QueueH0(
		Action<RuntimeWaveSettings> edit)
	{
		if (_syncing)
		{
			return;
		}


		_draft ??=
			_runtime.GetWaveSettingsSnapshot();


		if (_draft == null)
		{
			return;
		}


		edit(
			_draft);


		_h0Debounce =
			0.13;
	}


	private void SyncWaveControls(
		RuntimeWaveSettings settings)
	{
		if (settings == null ||
			_chop == null)
		{
			return;
		}


		_syncing =
			true;


		_chop.Value =
			settings.Chop;


		_windSpeed.Value =
			settings.WindSpeed;


		_windDirection.Value =
			settings.WindDirectionDegrees;


		_windTurbulence.Value =
			settings.WindTurbulence;


		_multiplier.Value =
			settings.Multiplier;

		_shallowAttenuation.Value =
			settings.ShallowWaterAttenuation;

		_shallowMaximumDepth.Value =
			settings.ShallowWaterMaximumDepth;


		SyncBandControls(
			settings);


		_syncing =
			false;
	}


	private void SyncBandControls()
	{
		SyncBandControls(
			_draft ??
			_runtime.GetWaveSettingsSnapshot());
	}


	private void SyncBandControls(
		RuntimeWaveSettings settings)
	{
		if (settings == null ||
			_band == null ||
			_band.Selected <
				0 ||
			_band.Selected >=
				settings.PowerLog10.Length)
		{
			return;
		}


		bool previous =
			_syncing;


		_syncing =
			true;


		int index =
			_band.Selected;


		_bandPower.Value =
			settings.PowerLog10[
				index];


		_bandEnabled.ButtonPressed =
			!settings.Disabled[
				index];


		float wavelength =
			MathF.Pow(
				2.0f,
				settings.SmallestWavelengthPowerOfTwo +
					index);


		_bandWavelength.Text =
			$"Approx wavelength: {wavelength:0.###} m";


		_syncing =
			previous;
	}


	private void UpdateStatus()
	{
		if (_status == null)
		{
			return;
		}


		bool nested =
			_surface?.LayoutMode ==
			AnimatedWaveSurfaceRenderer.SurfaceLayoutMode.NestedLod;


		int diagnosticLod =
			nested
				? Math.Clamp(
					_selectedLod,
					0,
					Math.Max(
						0,
						_runtime.RuntimeAnimatedWaveLodCount -
							1))
				: _selectedLod;


		if (!_runtime.TryGetAnimatedWaveSurface(
				diagnosticLod,
				out _,
				out _,
				out _,
				out AnimatedWaveLodSlice slice))
		{
			_status.Text =
				"Ocean: waiting for GPU...";

			return;
		}


		RuntimeWaveSettings settings =
			_runtime.GetWaveSettingsSnapshot();


		if (settings == null)
		{
			_status.Text =
				"Ocean: waiting for wave settings...";

			return;
		}


		string content =
			_waveContentMode ==
			0
				? "Cumulative"
				: _selectedLod +
					1 <
				  _runtime.RuntimeAnimatedWaveLodCount
					? $"Own band L{_selectedLod}-L{_selectedLod + 1}"
					: "Own band: coarsest remainder";


		_status.Text =
			$"LOD {diagnosticLod} · " +
			$"{slice.WorldSize:0.##} m · " +
			$"texel {slice.TexelWidth:0.###} m\n" +
			$"Scale ×{_runtime.RuntimeLodScale:0.#} · " +
			$"alpha {_runtime.RuntimeLodScaleAlpha:0.00} · " +
			$"{content}\n" +
			$"t {_runtime.SimulationTime:0.0}s · " +
			$"chop {settings.Chop:0.00} · " +
			$"H0 {_runtime.AppliedH0Revision}" +
			(_runtime.H0Pending ||
			 _draft != null
				? " pending"
				: "");
	}
}
