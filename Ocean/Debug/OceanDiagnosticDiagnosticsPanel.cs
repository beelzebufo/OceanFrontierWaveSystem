using System;
using Godot;
using OceanFrontier.Water.Runtime;
using OceanFrontier.Water.Waves.AnimatedWaves;

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
				true);


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


		VBoxContainer waves2D =
			OceanDiagnosticUi.Foldout(
				parent,
				"2D Waves",
				expanded: false);


		Build2DWaveControls(
			waves2D);


		OceanDiagnosticUi.Info(
			parent,
			"2D wave inspection is diagnostic-only. It never becomes the production render or physics path.");
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


		_debugSource =
			OceanDiagnosticUi.Option(
				parent,
				"Source",
				"Raw FFT",
				"AnimatedWaveField");


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
				"Magnitude");


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
				Math.Min(
					(int)_inset.Mode,
					2);


			_debugGain.Value =
				_inset.Gain;
		}


		_debugSource.ItemSelected +=
			_ =>
			{
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
