using System;
using Godot;
using OceanFrontier.Water.Runtime;
using ArchimedesBuoyancy =
	OceanFrontier.Water.Physics.Hydrostatics.OceanBuoyancy;
using ArchimedesHull =
	OceanFrontier.Water.Physics.Hydrostatics.OceanBuoyancyHull;
using HydrostaticWaterMode =
	OceanFrontier.Water.Physics.Hydrostatics.HydrostaticWaterMode;
using WeirdBuoyancy =
	OceanFrontier.Water.Physics.Weird.WeirdBuoyancy;

namespace OceanFrontier.Water.Debug;

internal sealed class OceanDiagnosticPhysicsPanel
{
	private enum PhysicsModel
	{
		Off = 0,
		Weird = 1,
		Archimedes = 2,
	}


	private OceanRuntime _runtime;

	private RigidBody3D _body;

	private VBoxContainer _modelControls;

	private Label _status;

	private OptionButton _modelSelector;

	private double _statusTimer;

	private bool _syncing;


	private Transform3D _spawnTransform;

	private Vector3 _spawnLinearVelocity;

	private Vector3 _spawnAngularVelocity;


	internal void Initialize(
		VBoxContainer parent,
		OceanRuntime runtime,
		RigidBody3D diagnosticHull)
	{
		_runtime =
			runtime;

		_body =
			diagnosticHull;


		if (_body != null)
		{
			_spawnTransform =
				_body.GlobalTransform;

			_spawnLinearVelocity =
				Vector3.Zero;

			_spawnAngularVelocity =
				Vector3.Zero;
		}


		Build(
			parent);


		//
		// No physics model means "park the diagnostic hull at spawn",
		// not "let gravity drop it out of the scene".
		//

		if (_body != null &&
			DetectModel() ==
				PhysicsModel.Off)
		{
			_body.Freeze =
				true;


			Respawn(
				keepFrozen: true);
		}
	}


	private void Build(
		VBoxContainer parent)
	{
		OceanDiagnosticUi.Header(
			parent,
			"Vessel Physics");


		OceanDiagnosticUi.Info(
			parent,
			"Choose one buoyancy model. Off keeps the diagnostic vessel parked at its spawn position.");


		if (_body == null)
		{
			OceanDiagnosticUi.Info(
				parent,
				"DiagnosticBuoyancyHull was not found.");

			return;
		}


		_modelSelector =
			OceanDiagnosticUi.Option(
				parent,
				"Physics model",
				"Off",
				"Weird / spring probes",
				"Archimedes / hull pressure");


		_modelSelector.Selected =
			(int)DetectModel();


		_modelSelector.ItemSelected +=
			index =>
			{
				if (_syncing)
				{
					return;
				}


				ApplyModel(
					(PhysicsModel)index);
			};


		var actionRow =
			new HBoxContainer();


		parent.AddChild(
			actionRow);


		OceanDiagnosticUi.Button(
			actionRow,
			"Respawn vessel",
			() =>
				Respawn(
					keepFrozen:
						DetectModel() ==
						PhysicsModel.Off));


		OceanDiagnosticUi.Button(
			actionRow,
			"Stop motion",
			StopMotion);


		OceanDiagnosticUi.Spin(
			parent,
			"Mass",
			_body.Mass,
			1.0,
			1000000.0,
			10.0,
			" kg")
			.ValueChanged +=
				value =>
					_body.Mass =
						(float)value;


		OceanDiagnosticUi.Spin(
			parent,
			"Angular damping",
			_body.AngularDamp,
			0.0,
			20.0,
			0.05)
			.ValueChanged +=
				value =>
					_body.AngularDamp =
						(float)value;


		_modelControls =
			new VBoxContainer
			{
				SizeFlagsHorizontal =
					Control.SizeFlags.ExpandFill,
			};


		parent.AddChild(
			_modelControls);


		_status =
			OceanDiagnosticUi.Info(
				parent,
				"Physics: off");


		RefreshModelControls();
	}


	internal void Tick(
		double delta)
	{
		if (_status == null ||
			_body == null)
		{
			return;
		}


		_statusTimer -=
			delta;


		if (_statusTimer >
			0.0)
		{
			return;
		}


		_statusTimer =
			0.2;


		UpdateStatus();
	}


	private PhysicsModel DetectModel()
	{
		if (FindChildOfType<ArchimedesBuoyancy>() !=
			null)
		{
			return
				PhysicsModel.Archimedes;
		}


		if (FindChildOfType<WeirdBuoyancy>() !=
			null)
		{
			return
				PhysicsModel.Weird;
		}


		return
			PhysicsModel.Off;
	}


	private void ApplyModel(
		PhysicsModel model)
	{
		if (_body == null)
		{
			return;
		}


		//
		// Park the body while components are switched.
		//

		_body.Freeze =
			true;


		StopMotion();


		switch (model)
		{
			case PhysicsModel.Off:
				RemoveChildOfType<WeirdBuoyancy>();

				RemoveChildOfType<ArchimedesBuoyancy>();

				RemoveChildOfType<ArchimedesHull>();


				Respawn(
					keepFrozen: true);

				break;


			case PhysicsModel.Weird:
				RemoveChildOfType<ArchimedesBuoyancy>();

				RemoveChildOfType<ArchimedesHull>();


				if (FindChildOfType<WeirdBuoyancy>() ==
					null)
				{
					var weird =
						new WeirdBuoyancy
						{
							Name =
								"WeirdBuoyancy",

							ShowProbeVisualization =
								true,
						};


					_body.AddChild(
						weird);
				}


				Respawn(
					keepFrozen: false);

				break;


			case PhysicsModel.Archimedes:
				RemoveChildOfType<WeirdBuoyancy>();


				MeshInstance3D mesh =
					ResolvePhysicsHullMesh();


				if (mesh == null)
				{
					GD.PushError(
						"Archimedes diagnostics require a direct child MeshInstance3D " +
						"named PhysicsHullMesh or MeshInstance3D.");


					SetModelSelector(
						PhysicsModel.Off);


					Respawn(
						keepFrozen: true);


					return;
				}


				ArchimedesHull hull =
					FindChildOfType<ArchimedesHull>();


				if (hull == null)
				{
					hull =
						new ArchimedesHull
						{
							Name =
								"OceanBuoyancyHull",

							HullMesh =
								mesh,
						};


					_body.AddChild(
						hull);
				}
				else if (hull.HullMesh == null)
				{
					hull.HullMesh =
						mesh;


					hull.Rebuild();
				}


				if (FindChildOfType<ArchimedesBuoyancy>() ==
					null)
				{
					var buoyancy =
						new ArchimedesBuoyancy
						{
							Name =
								"OceanBuoyancy",
						};


					_body.AddChild(
						buoyancy);
				}


				Respawn(
					keepFrozen: false);

				break;
		}


		SetModelSelector(
			model);


		RefreshModelControls();


		UpdateStatus();
	}


	private void Respawn(
		bool keepFrozen)
	{
		if (_body == null)
		{
			return;
		}


		_body.Freeze =
			true;


		_body.GlobalTransform =
			_spawnTransform;


		_body.LinearVelocity =
			_spawnLinearVelocity;


		_body.AngularVelocity =
			_spawnAngularVelocity;


		_body.Sleeping =
			false;


		_body.Freeze =
			keepFrozen;
	}


	private void StopMotion()
	{
		if (_body == null)
		{
			return;
		}


		_body.LinearVelocity =
			Vector3.Zero;


		_body.AngularVelocity =
			Vector3.Zero;


		_body.Sleeping =
			false;
	}


	private void RefreshModelControls()
	{
		if (_modelControls == null)
		{
			return;
		}


		OceanDiagnosticUi.Clear(
			_modelControls);


		PhysicsModel model =
			DetectModel();


		switch (model)
		{
			case PhysicsModel.Off:
				OceanDiagnosticUi.Info(
					_modelControls,
					"Vessel is frozen at spawn. Select a model to release it.");

				break;


			case PhysicsModel.Weird:
				BuildWeirdControls(
					_modelControls);

				break;


			case PhysicsModel.Archimedes:
				BuildArchimedesControls(
					_modelControls);

				break;
		}
	}


	private void BuildWeirdControls(
		VBoxContainer parent)
	{
		WeirdBuoyancy weird =
			FindChildOfType<WeirdBuoyancy>();


		if (weird == null)
		{
			return;
		}


		OceanDiagnosticUi.Header(
			parent,
			"Weird Spring Model");


		var motion =
			OceanDiagnosticUi.Option(
				parent,
				"Motion",
				"Heave only",
				"Heave + Pitch",
				"Heave + Roll",
				"Heave + Pitch + Roll");


		motion.Selected =
			(int)weird.Mode;


		motion.ItemSelected +=
			index =>
				weird.Mode =
					(WeirdBuoyancy.BuoyancyMode)index;


		OceanDiagnosticUi.Spin(
			parent,
			"Target submersion",
			weird.TargetSubmersion,
			0.05,
			3.0,
			0.05,
			" m")
			.ValueChanged +=
				value =>
					weird.TargetSubmersion =
						(float)value;


		OceanDiagnosticUi.Spin(
			parent,
			"Damping ratio",
			weird.DampingRatio,
			0.0,
			2.0,
			0.05)
			.ValueChanged +=
				value =>
					weird.DampingRatio =
						(float)value;


		OceanDiagnosticUi.Spin(
			parent,
			"Max buoyancy",
			weird.MaximumBuoyancyFactor,
			1.0,
			10.0,
			0.1,
			" ×mg")
			.ValueChanged +=
				value =>
					weird.MaximumBuoyancyFactor =
						(float)value;


		OceanDiagnosticUi.Spin(
			parent,
			"Forward drag",
			weird.ForwardDrag,
			0.0,
			10.0,
			0.05)
			.ValueChanged +=
				value =>
					weird.ForwardDrag =
						(float)value;


		OceanDiagnosticUi.Spin(
			parent,
			"Lateral drag",
			weird.LateralDrag,
			0.0,
			10.0,
			0.05)
			.ValueChanged +=
				value =>
					weird.LateralDrag =
						(float)value;


		OceanDiagnosticUi.Spin(
			parent,
			"Probe plane Y",
			weird.ProbePlaneY,
			-3.0,
			3.0,
			0.05,
			" m")
			.ValueChanged +=
				value =>
					weird.ProbePlaneY =
						(float)value;


		OceanDiagnosticUi.Info(
			parent,
			"This is the intentionally non-volumetric spring/probe model.");
	}


	private void BuildArchimedesControls(
		VBoxContainer parent)
	{
		ArchimedesBuoyancy buoyancy =
			FindChildOfType<ArchimedesBuoyancy>();


		ArchimedesHull hull =
			FindChildOfType<ArchimedesHull>();


		if (buoyancy == null ||
			hull == null)
		{
			return;
		}


		OceanDiagnosticUi.Header(
			parent,
			"Archimedes Hydrostatics");


		var waterMode =
			OceanDiagnosticUi.Option(
				parent,
				"Water mode",
				"AnimatedWaveField async",
				"Flat synchronous");


		waterMode.Selected =
			(int)buoyancy.WaterMode;


		waterMode.ItemSelected +=
			index =>
				buoyancy.WaterMode =
					(HydrostaticWaterMode)index;


		OceanDiagnosticUi.Spin(
			parent,
			"Water density",
			buoyancy.WaterDensity,
			1.0,
			5000.0,
			1.0,
			" kg/m³")
			.ValueChanged +=
				value =>
					buoyancy.WaterDensity =
						(float)value;


		OceanDiagnosticUi.Spin(
			parent,
			"Min spatial length",
			buoyancy.MinSpatialLength,
			0.01,
			100.0,
			0.05,
			" m")
			.ValueChanged +=
				value =>
					buoyancy.MinSpatialLength =
						(float)value;


		OceanDiagnosticUi.Spin(
			parent,
			"Patch padding",
			buoyancy.PatchPadding,
			0.0,
			20.0,
			0.05,
			" m")
			.ValueChanged +=
				value =>
					buoyancy.PatchPadding =
						(float)value;


		OceanDiagnosticUi.Spin(
			parent,
			"Sea level",
			buoyancy.SeaLevel,
			-100.0,
			100.0,
			0.05,
			" m")
			.ValueChanged +=
				value =>
					buoyancy.SeaLevel =
						(float)value;


		OceanDiagnosticUi.Button(
			parent,
			"Rebuild physics hull",
			() =>
			{
				try
				{
					hull.Rebuild();
				}
				catch (Exception exception)
				{
					GD.PushError(
						$"Buoyancy hull rebuild failed:\n{exception}");
				}
			});


		OceanDiagnosticUi.Info(
			parent,
			$"Water patch: {buoyancy.PatchResolutionX} × {buoyancy.PatchResolutionZ}. " +
			"Patch resolution is fixed when the model is attached.");
	}


	private void UpdateStatus()
	{
		if (_status == null ||
			_body == null)
		{
			return;
		}


		WeirdBuoyancy weird =
			FindChildOfType<WeirdBuoyancy>();


		if (weird != null)
		{
			_status.Text =
				$"Weird · generation " +
				$"{(weird.HasCompletedResult ? weird.LatestCompletedGeneration.ToString() : "--")}\n" +
				$"Mass {_body.Mass:0.##} kg · angular damp {_body.AngularDamp:0.##}";

			return;
		}


		ArchimedesBuoyancy archimedes =
			FindChildOfType<ArchimedesBuoyancy>();


		ArchimedesHull hull =
			FindChildOfType<ArchimedesHull>();


		if (archimedes != null &&
			hull != null)
		{
			double bodyDensity =
				hull.Volume >
					0.0
					? _body.Mass /
						hull.Volume
					: double.PositiveInfinity;


			bool asyncWater =
				archimedes.WaterMode ==
					HydrostaticWaterMode.AnimatedWaveFieldAsync;


			string generation =
				asyncWater &&
				archimedes.LatestCompletedGeneration > 0
					? archimedes.LatestCompletedGeneration.ToString()
					: "--";


			string readback =
				asyncWater &&
				archimedes.LatestReadbackFrames >= 0
					? $"{archimedes.LatestReadbackFrames} frames"
					: "--";


			_status.Text =
				$"Archimedes · {(asyncWater ? "AWF async" : "flat synchronous")} · " +
				$"generation {generation} · readback {readback}\n" +
				$"Hull {hull.Volume:0.###} m³ · body density {bodyDensity:0.##} kg/m³\n" +
				$"Coverage {(archimedes.CurrentPatchCoverageValid ? "valid" : "invalid")} · " +
				$"{archimedes.ValidVertexSamples}/{hull.VertexCount} vertices · " +
				$"force {archimedes.LastHydrostaticForce.Length():0.##} N\n" +
				$"Body Y {_body.GlobalPosition.Y:0.###} m · " +
				$"vertical velocity {_body.LinearVelocity.Y:0.###} m/s\n" +
				$"Wet area {archimedes.SubmergedArea:0.###} m² · " +
				$"{archimedes.SubmergedTriangleCount} wet triangles · " +
				$"{archimedes.WaterPatchQueryCount} GPU points";

			return;
		}


		_status.Text =
			$"Physics off · vessel parked at spawn · mass {_body.Mass:0.##} kg";
	}


	private MeshInstance3D ResolvePhysicsHullMesh()
	{
		MeshInstance3D preferred =
			_body.GetNodeOrNull<MeshInstance3D>(
				"PhysicsHullMesh");


		if (preferred != null)
		{
			return preferred;
		}


		MeshInstance3D legacy =
			_body.GetNodeOrNull<MeshInstance3D>(
				"MeshInstance3D");


		if (legacy != null)
		{
			return legacy;
		}


		foreach (Node child in
				 _body.GetChildren())
		{
			if (child is MeshInstance3D mesh)
			{
				return mesh;
			}
		}


		return null;
	}


	private T FindChildOfType<T>()
		where T : Node
	{
		if (_body == null)
		{
			return null;
		}


		foreach (Node child in
				 _body.GetChildren())
		{
			if (child is T typed)
			{
				return typed;
			}
		}


		return null;
	}


	private void RemoveChildOfType<T>()
		where T : Node
	{
		T node =
			FindChildOfType<T>();


		if (node == null)
		{
			return;
		}


		_body.RemoveChild(
			node);


		node.QueueFree();
	}


	private void SetModelSelector(
		PhysicsModel model)
	{
		if (_modelSelector == null)
		{
			return;
		}


		bool previous =
			_syncing;


		_syncing =
			true;


		_modelSelector.Selected =
			(int)model;


		_syncing =
			previous;
	}
}
