using Godot;
using OceanFrontier.Water.Runtime;
using ArchimedesBuoyancy =
	OceanFrontier.Water.Physics.Hydrostatics.OceanBuoyancy;
using WeirdBuoyancy =
	OceanFrontier.Water.Physics.Weird.WeirdBuoyancy;

namespace OceanFrontier.Water.Debug;

/// <summary>
/// Always-visible compact performance HUD.
/// Independent from the collapsible diagnostics panel.
/// </summary>
internal sealed class OceanDiagnosticPerformanceHud
{
	private OceanRuntime _runtime;

	private RigidBody3D _diagnosticHull;

	private Label _label;

	private double _sampleSeconds;

	private int _sampleFrames;


	internal void Initialize(
		Control root,
		OceanRuntime runtime,
		RigidBody3D diagnosticHull)
	{
		_runtime =
			runtime;

		_diagnosticHull =
			diagnosticHull;


		var panel =
			new PanelContainer
			{
				AnchorLeft =
					0.0f,

				AnchorTop =
					1.0f,

				AnchorRight =
					0.0f,

				AnchorBottom =
					1.0f,

				OffsetLeft =
					8.0f,

				OffsetTop =
					-132.0f,

				OffsetRight =
					275.0f,

				OffsetBottom =
					-8.0f,

				MouseFilter =
					Control.MouseFilterEnum.Ignore,
			};


		_label =
			new Label
			{
				Text =
					"FPS: --\n" +
					"Frame: -- ms\n" +
					"Queries: --\n" +
					"Readback: --\n" +
					"Physics: --",

				MouseFilter =
					Control.MouseFilterEnum.Ignore,
			};


		panel.AddChild(
			_label);


		root.AddChild(
			panel);
	}


	internal void Tick(
		double delta)
	{
		if (_runtime == null ||
			_label == null)
		{
			return;
		}


		_sampleSeconds +=
			delta;


		_sampleFrames++;


		if (_sampleSeconds <
			0.25)
		{
			return;
		}


		_runtime.PointQueries.GetDiagnostics(
			out int queries,
			out int readbackFrames,
			out bool hasResult);


		double fps =
			_sampleFrames /
			_sampleSeconds;


		double frameMs =
			_sampleSeconds *
			1000.0 /
			_sampleFrames;


		_label.Text =
			$"FPS: {fps:0.0}\n" +
			$"Frame: {frameMs:0.00} ms\n" +
			$"Queries: {queries}\n" +
			$"Readback: {(hasResult ? readbackFrames.ToString() : "--")} frames\n" +
			$"Physics: {GetPhysicsStatus()}";


		_sampleSeconds =
			0.0;


		_sampleFrames =
			0;
	}


	private string GetPhysicsStatus()
	{
		if (_diagnosticHull == null)
		{
			return "--";
		}


		foreach (Node child in
				 _diagnosticHull.GetChildren())
		{
			if (child is ArchimedesBuoyancy archimedes)
			{
				return
					archimedes.HasCompletedResult
						? $"Arch {archimedes.LatestCompletedGeneration}"
						: "Arch --";
			}


			if (child is WeirdBuoyancy weird)
			{
				return
					weird.HasCompletedResult
						? $"Weird {weird.LatestCompletedGeneration}"
						: "Weird --";
			}
		}


		return
			"Off";
	}
}
