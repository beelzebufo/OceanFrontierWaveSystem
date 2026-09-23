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

	private ulong _nextSampleTimeUsec;

	private const ulong SampleIntervalUsec =
		250_000;


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
					-166.0f,

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
					"Render FPS: --\n" +
					"Render frame: -- ms\n" +
					"Physics TPS: --\n" +
					"Time scale: --\n" +
					"Queries: --\n" +
					"Readback: --\n" +
					"Physics model: --",

				MouseFilter =
					Control.MouseFilterEnum.Ignore,
			};


		panel.AddChild(
			_label);


		root.AddChild(
			panel);
	}


	internal void Tick(
		double _)
	{
		if (_runtime == null ||
			_label == null)
		{
			return;
		}


		ulong currentTimeUsec =
			Time.GetTicksUsec();


		if (currentTimeUsec <
			_nextSampleTimeUsec)
		{
			return;
		}


		_nextSampleTimeUsec =
			currentTimeUsec +
			SampleIntervalUsec;


		_runtime.PointQueries.GetDiagnostics(
			out int queries,
			out int readbackFrames,
			out bool hasResult);


		double fps =
			Engine.GetFramesPerSecond();


		double frameMs =
			fps > 0.0
				? 1000.0 / fps
				: 0.0;


		_label.Text =
			$"Render FPS: {fps:0.0}\n" +
			$"Render frame: {frameMs:0.00} ms\n" +
			$"Physics TPS: {Engine.PhysicsTicksPerSecond}\n" +
			$"Time scale: {Engine.TimeScale:0.##}x\n" +
			$"Queries: {queries}\n" +
			$"Readback: {(hasResult ? readbackFrames.ToString() : "--")} frames\n" +
			$"Physics model: {GetPhysicsStatus()}";
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
