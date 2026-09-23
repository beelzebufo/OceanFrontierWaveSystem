using System;
using Godot;
using OceanFrontier.Water.Queries;
using OceanFrontier.Water.Runtime;

namespace OceanFrontier.Water.Physics.Hydrostatics;

public enum HydrostaticWaterMode
{
	AnimatedWaveFieldAsync = 0,
	FlatSynchronous = 1,
}


/// <summary>
/// Archimedean / hydrostatic buoyancy driven by the canonical AnimatedWaveField.
///
/// Attach this node directly below the RigidBody3D which owns the object.
/// A sibling OceanBuoyancyHull supplies the immutable low-poly closed hull.
///
/// Runtime pipeline:
///
///     OceanBuoyancyHull
///         -> body-local closed triangle mesh
///
///     OceanBuoyancyWaterPatch
///         -> small batched GPU query grid
///         -> coherent water-height generation
///
///     OceanHydrostaticSolver
///         -> submerged triangle clipping
///         -> integrated hydrostatic pressure
///         -> net force + net torque
///
/// Hydrostatic pressure remains independent from the optional water-relative
/// heave damping force. This component contains no spring target depth and no
/// artificial pitch/roll modes.
/// </summary>
[GlobalClass]
public partial class OceanBuoyancy : Node
{
	private const float QueryScaleDivisor =
		4.0f;

	private const float DefaultWaterDensity =
		1027.0f;

	private const float DefaultGravity =
		9.8f;

	private const float DefaultHeaveDamping =
		1.0f;

	private const float WetAreaEpsilon =
		0.000001f;


	[Export]
	public NodePath OceanRuntimePath { get; set; }


	[Export]
	public NodePath HullPath { get; set; }


	[Export(PropertyHint.Range, "2,16,1")]
	public int PatchResolutionX { get; set; } =
		5;


	[Export(PropertyHint.Range, "2,32,1")]
	public int PatchResolutionZ { get; set; } =
		9;


	[Export(PropertyHint.Range, "0,20,0.05,or_greater")]
	public float PatchPadding { get; set; } =
		0.25f;


	/// <summary>
	/// Crest-style query filtering scale.
	///
	/// PointQueryService receives:
	///
	///     minGridSize = MinSpatialLength / 4
	///
	/// This is a sampling/filtering control, not a buoyancy spring length.
	/// </summary>
	[Export(PropertyHint.Range, "0.01,100,0.01,or_greater")]
	public float MinSpatialLength { get; set; } =
		3.5f;


	[Export(PropertyHint.Range, "1,5000,1,or_greater")]
	public float WaterDensity { get; set; } =
		DefaultWaterDensity;


	[Export]
	public float SeaLevel { get; set; }


	[Export]
	public HydrostaticWaterMode WaterMode { get; set; } =
		HydrostaticWaterMode.AnimatedWaveFieldAsync;


	[Export]
	public bool TemporalPredictionEnabled { get; set; } =
		true;


	[Export]
	public bool HeaveDampingEnabled { get; set; } =
		true;


	/// <summary>
	/// Water-relative vertical damping coefficient in inverse seconds.
	/// The resulting acceleration is -HeaveDamping * relativeVelocityY.
	/// </summary>
	[Export(PropertyHint.Range, "0,20,0.05,or_greater")]
	public float HeaveDamping { get; set; } =
		DefaultHeaveDamping;


	private RigidBody3D _body;

	private OceanRuntime _runtime;

	private OceanBuoyancyHull _hull;

	private OceanBuoyancyWaterPatch _waterPatch;


	private Vector3[] _worldVertices =
		Array.Empty<Vector3>();

	private float[] _waterHeights =
		Array.Empty<float>();


	private OceanHydrostaticResult _currentHydrostaticResult;

	private int _validVertexSamples;

	private float _gravity =
		DefaultGravity;

	private bool _hasHydrostaticResult;

	private bool _currentPatchCoverageValid;

	private bool _warnedAutomaticCenterOfMass;

	private bool _temporalPredictionUsed;

	private double _latestSampleAgeSeconds =
		double.NaN;

	private double _sampleIntervalSeconds =
		double.NaN;

	private double _predictionAgeSeconds;

	private float _lastWaterVelocityY;

	private float _lastRelativeHeaveVelocity;

	private float _lastHeaveDampingForce;

	private bool _heaveDampingApplied;


	public bool HasCompletedResult =>
		_hasHydrostaticResult;


	public long LatestCompletedGeneration =>
		WaterMode ==
			HydrostaticWaterMode.AnimatedWaveFieldAsync
				? _waterPatch?.LatestGeneration ?? 0
				: 0;


	public int LatestReadbackFrames =>
		WaterMode ==
			HydrostaticWaterMode.AnimatedWaveFieldAsync
				? _waterPatch?.LatestReadbackFrames ?? -1
				: -1;


	public int WaterPatchQueryCount =>
		_waterPatch?.QueryCount ??
		0;


	public int ValidVertexSamples =>
		_validVertexSamples;


	public bool CurrentPatchCoverageValid =>
		_currentPatchCoverageValid;


	public float SubmergedArea =>
		_hasHydrostaticResult
			? _currentHydrostaticResult.SubmergedArea
			: 0.0f;


	public int SubmergedTriangleCount =>
		_hasHydrostaticResult
			? _currentHydrostaticResult.SubmergedTriangleCount
			: 0;


	public Vector3 LastHydrostaticForce =>
		_hasHydrostaticResult
			? _currentHydrostaticResult.Force
			: Vector3.Zero;


	public Vector3 LastHydrostaticTorque =>
		_hasHydrostaticResult
			? _currentHydrostaticResult.Torque
			: Vector3.Zero;


	public bool TemporalPredictionUsed =>
		_temporalPredictionUsed;


	public double LatestSampleAgeSeconds =>
		_latestSampleAgeSeconds;


	public double SampleIntervalSeconds =>
		_sampleIntervalSeconds;


	public double PredictionAgeSeconds =>
		_predictionAgeSeconds;


	public float LastWaterVelocityY =>
		_lastWaterVelocityY;


	public float LastRelativeHeaveVelocity =>
		_lastRelativeHeaveVelocity;


	public float LastHeaveDampingForce =>
		_lastHeaveDampingForce;


	public bool HeaveDampingApplied =>
		_heaveDampingApplied;


	public override void _Ready()
	{
		_body =
			GetParent() as RigidBody3D;


		_runtime =
			ResolveRuntime();


		_hull =
			ResolveHull();


		if (_body == null ||
			_runtime == null ||
			_hull == null ||
			!ValidateConfiguration())
		{
			GD.PushError(
				"OceanBuoyancy requires a RigidBody3D parent, an OceanRuntime, " +
				"a valid OceanBuoyancyHull, and valid hydrostatic settings.");


			SetPhysicsProcess(
				false);


			return;
		}


		if (!_hull.IsBuilt)
		{
			try
			{
				_hull.Rebuild();
			}
			catch (Exception exception)
			{
				GD.PushError(
					$"OceanBuoyancy failed to build its physics hull:\n" +
					exception);


				SetPhysicsProcess(
					false);


				return;
			}
		}


		if (!_hull.IsBuilt ||
			_hull.VertexCount < 4 ||
			_hull.TriangleCount < 4)
		{
			GD.PushError(
				"OceanBuoyancyHull did not produce a valid closed hull.");


			SetPhysicsProcess(
				false);


			return;
		}


		_worldVertices =
			new Vector3[
				_hull.VertexCount];


		_waterHeights =
			new float[
				_hull.VertexCount];


		_gravity =
			ResolvePhysicsGravity();


		try
		{
			_waterPatch =
				new OceanBuoyancyWaterPatch(
					_runtime,
					PatchResolutionX,
					PatchResolutionZ);
		}
		catch (Exception exception)
		{
			GD.PushError(
				$"OceanBuoyancy failed to reserve its water patch queries:\n" +
				exception);


			SetPhysicsProcess(
				false);


			return;
		}


		double objectDensity =
			_hull.Volume >
				0.0
				? _body.Mass /
					_hull.Volume
				: double.PositiveInfinity;


		GD.Print(
			$"[Ocean] Archimedes buoyancy ready: " +
			$"hull={_hull.VertexCount} vertices/{_hull.TriangleCount} triangles, " +
			$"volume={_hull.Volume:0.###} m³, " +
			$"body density={objectDensity:0.###} kg/m³, " +
			$"water density={WaterDensity:0.###} kg/m³, " +
			$"patch={PatchResolutionX}x{PatchResolutionZ} " +
			$"({_waterPatch.QueryCount} GPU points).");
	}


	public override void _ExitTree()
	{
		_waterPatch?.Dispose();

		_waterPatch =
			null;
	}


	public override void _PhysicsProcess(
		double delta)
	{
		if (_body == null ||
			_hull == null ||
			_waterPatch == null)
		{
			return;
		}


		ClearCurrentHydrostatics();


		bool useAsyncWater =
			WaterMode ==
				HydrostaticWaterMode.AnimatedWaveFieldAsync;


		if (useAsyncWater)
		{
			_waterPatch.ConsumeLatest();


			double latestSampleTime =
				_waterPatch.LatestSampleTime;


			_latestSampleAgeSeconds =
				double.IsFinite(latestSampleTime)
					? Math.Max(
						0.0,
						_runtime.SimulationTime -
							latestSampleTime)
					: double.NaN;


			_sampleIntervalSeconds =
				_waterPatch.SampleInterval;
		}
		else
		{
			_latestSampleAgeSeconds =
				double.NaN;

			_sampleIntervalSeconds =
				double.NaN;
		}


		if (!useAsyncWater ||
			_waterPatch.HasLatest)
		{
			SolveCurrentHydrostatics(
				flatWater:
					!useAsyncWater);
		}


		ApplyCurrentHydrostatics();


		ApplyHeaveDamping(
			useAsyncWater);


		if (useAsyncWater)
		{
			SubmitCurrentWaterPatch();
		}
	}


	private void SolveCurrentHydrostatics(
		bool flatWater)
	{
		ReadOnlySpan<Vector3> localVertices =
			_hull.LocalVertices;


		Transform3D bodyTransform =
			_body.GlobalTransform;


		_validVertexSamples =
			0;


		_temporalPredictionUsed =
			false;

		_predictionAgeSeconds =
			0.0;


		for (int i = 0;
			 i < localVertices.Length;
			 i++)
		{
			Vector3 worldVertex =
				bodyTransform *
					localVertices[i];


			_worldVertices[i] =
				worldVertex;


			float waterHeight =
				SeaLevel;


			bool valid =
				flatWater;


			if (!flatWater)
			{
				valid =
					_waterPatch.TrySampleSurfaceAtTime(
					new Vector2(
						worldVertex.X,
						worldVertex.Z),
					_runtime.SimulationTime,
					SeaLevel,
					TemporalPredictionEnabled,
					out waterHeight,
					out bool predicted,
					out double predictionAge);


				if (predicted)
				{
					_temporalPredictionUsed =
						true;


					_predictionAgeSeconds =
						Math.Max(
							_predictionAgeSeconds,
							predictionAge);
				}
			}


			if (!valid ||
				!float.IsFinite(
					waterHeight))
			{
				_waterHeights[i] =
					float.NaN;

				continue;
			}


			_waterHeights[i] =
				waterHeight;


			_validVertexSamples++;
		}


		//
		// Partial hull pressure is unsafe: a missing patch corner or an AWF
		// validity failure can turn a symmetric hull into a one-sided force.
		//
		// Require the complete hull generation.
		//

		if (_validVertexSamples !=
			localVertices.Length)
		{
			return;
		}


		_currentPatchCoverageValid =
			true;


		OceanHydrostaticResult result =
			OceanHydrostaticSolver.Solve(
				_worldVertices,
				_hull.Indices,
				_waterHeights,
				GetWorldCenterOfMass(),
				WaterDensity,
				_gravity);


		if (!result.Force.IsFinite() ||
			!result.Torque.IsFinite())
		{
			_currentPatchCoverageValid =
				false;

			return;
		}


		_currentHydrostaticResult =
			result;

		_hasHydrostaticResult =
			true;
	}


	private void ApplyCurrentHydrostatics()
	{
		if (!_hasHydrostaticResult)
		{
			return;
		}


		Vector3 force =
			_currentHydrostaticResult.Force;

		Vector3 torque =
			_currentHydrostaticResult.Torque;


		if (!force.IsFinite() ||
			!torque.IsFinite())
		{
			ClearCurrentHydrostatics();

			return;
		}


		if (force.LengthSquared() >
			0.0f)
		{
			_body.ApplyCentralForce(
				force);
		}


		if (torque.LengthSquared() >
			0.0f)
		{
			_body.ApplyTorque(
				torque);
		}
	}


	private void ApplyHeaveDamping(
		bool useAsyncWater)
	{
		if (!HeaveDampingEnabled ||
			!float.IsFinite(HeaveDamping) ||
			HeaveDamping <=
				0.0f ||
			!_hasHydrostaticResult ||
			!_currentPatchCoverageValid ||
			_currentHydrostaticResult.SubmergedArea <=
				WetAreaEpsilon)
		{
			return;
		}


		float waterVelocityY =
			0.0f;


		if (useAsyncWater)
		{
			Vector3 bodyPosition =
				_body.GlobalPosition;


			if (!_waterPatch.TrySampleVerticalVelocity(
					new Vector2(
						bodyPosition.X,
						bodyPosition.Z),
					out waterVelocityY))
			{
				return;
			}
		}


		float relativeVelocityY =
			_body.LinearVelocity.Y -
				waterVelocityY;

		float accelerationY =
			-HeaveDamping *
				relativeVelocityY;

		float forceY =
			_body.Mass *
				accelerationY;


		if (!float.IsFinite(forceY))
		{
			return;
		}


		_lastWaterVelocityY =
			waterVelocityY;

		_lastRelativeHeaveVelocity =
			relativeVelocityY;

		_lastHeaveDampingForce =
			forceY;

		_heaveDampingApplied =
			true;


		if (forceY !=
			0.0f)
		{
			_body.ApplyCentralForce(
				Vector3.Up *
					forceY);
		}
	}


	private void SubmitCurrentWaterPatch()
	{
		if (!_waterPatch.CanSubmit)
		{
			return;
		}


		float minGridSize =
			MinSpatialLength /
				QueryScaleDivisor;


		_waterPatch.Submit(
			_body,
			_hull,
			PatchPadding,
			minGridSize);
	}


	private OceanRuntime ResolveRuntime()
	{
		if (OceanRuntimePath != null &&
			!OceanRuntimePath.IsEmpty)
		{
			return
				GetNodeOrNull<OceanRuntime>(
					OceanRuntimePath);
		}


		Node node =
			GetParent();


		while (node != null)
		{
			if (node is OceanRuntime runtime)
			{
				return runtime;
			}


			node =
				node.GetParent();
		}


		return null;
	}


	private OceanBuoyancyHull ResolveHull()
	{
		if (HullPath != null &&
			!HullPath.IsEmpty)
		{
			return
				GetNodeOrNull<OceanBuoyancyHull>(
					HullPath);
		}


		RigidBody3D body =
			GetParent() as RigidBody3D;


		if (body == null)
		{
			return null;
		}


		foreach (Node child in
				 body.GetChildren())
		{
			if (child is OceanBuoyancyHull hull)
			{
				return hull;
			}
		}


		return null;
	}


	private bool ValidateConfiguration()
	{
		long queryCount =
			(long)PatchResolutionX *
			PatchResolutionZ;


		return
			PatchResolutionX >=
				2 &&
			PatchResolutionZ >=
				2 &&
			queryCount <=
				OceanPointQueryService.Capacity &&
			float.IsFinite(
				PatchPadding) &&
			PatchPadding >=
				0.0f &&
			float.IsFinite(
				MinSpatialLength) &&
			MinSpatialLength >
				0.0f &&
			float.IsFinite(
				WaterDensity) &&
			WaterDensity >
				0.0f &&
			float.IsFinite(
				HeaveDamping) &&
			HeaveDamping >=
				0.0f &&
			float.IsFinite(
				SeaLevel);
	}


	private Vector3 GetWorldCenterOfMass()
	{
		if (_body.CenterOfMassMode ==
			RigidBody3D.CenterOfMassModeEnum.Custom)
		{
			return
				_body.GlobalTransform *
					_body.CenterOfMass;
		}


		//
		// Godot does not expose the automatically solved center of mass as a
		// world-space value. For a centered rigid body the origin is correct.
		// Boats with an intentionally offset mass distribution should use
		// CenterOfMassMode.Custom so hydrostatic torque uses the same COM as
		// the physics body.
		//

		if (!_warnedAutomaticCenterOfMass)
		{
			_warnedAutomaticCenterOfMass =
				true;


			GD.PushWarning(
				"OceanBuoyancy: RigidBody3D uses automatic center of mass. " +
				"Hydrostatic torque assumes the body origin is the COM. " +
				"Use Custom center of mass for offset-mass vessels.");
		}


		return
			_body.GlobalPosition;
	}


	private void ClearCurrentHydrostatics()
	{
		_currentHydrostaticResult =
			default;

		_hasHydrostaticResult =
			false;

		_currentPatchCoverageValid =
			false;

		_validVertexSamples =
			0;

		_temporalPredictionUsed =
			false;

		_predictionAgeSeconds =
			0.0;

		_lastWaterVelocityY =
			0.0f;

		_lastRelativeHeaveVelocity =
			0.0f;

		_lastHeaveDampingForce =
			0.0f;

		_heaveDampingApplied =
			false;
	}


	private static float ResolvePhysicsGravity()
	{
		Variant setting =
			ProjectSettings.GetSetting(
				"physics/3d/default_gravity",
				DefaultGravity);


		float gravity =
			setting.AsSingle();


		return
			float.IsFinite(gravity) &&
			gravity >
				0.0f
				? gravity
				: DefaultGravity;
	}
}
