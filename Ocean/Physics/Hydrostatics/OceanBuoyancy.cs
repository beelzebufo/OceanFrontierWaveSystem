using System;
using Godot;
using OceanFrontier.Water.Queries;
using OceanFrontier.Water.Runtime;

namespace OceanFrontier.Water.Physics.Hydrostatics;

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
/// This component intentionally contains no spring buoyancy, no target
/// submersion, no artificial pitch/roll modes, and no hydrodynamic drag.
/// Those are separate models/stages.
/// </summary>
[GlobalClass]
public partial class OceanBuoyancy : Node
{
	private const int SnapshotCapacity =
		32;

	private const float QueryScaleDivisor =
		4.0f;

	private const float DefaultWaterDensity =
		1027.0f;

	private const float DefaultGravity =
		9.8f;


	private struct BodySnapshot
	{
		public bool Valid;

		public long Generation;

		public Transform3D BodyTransform;

		public Vector3 CenterOfMassWorld;

		public float SeaLevel;
	}


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


	private readonly BodySnapshot[] _bodySnapshots =
		new BodySnapshot[
			SnapshotCapacity];


	private RigidBody3D _body;

	private OceanRuntime _runtime;

	private OceanBuoyancyHull _hull;

	private OceanBuoyancyWaterPatch _waterPatch;


	private Vector3[] _worldVertices =
		Array.Empty<Vector3>();

	private float[] _waterHeights =
		Array.Empty<float>();


	private OceanHydrostaticResult _latestHydrostaticResult;

	private long _latestSolvedGeneration;

	private int _latestReadbackFrames =
		-1;

	private int _validVertexSamples;

	private float _gravity =
		DefaultGravity;

	private bool _hasHydrostaticResult;

	private bool _warnedAutomaticCenterOfMass;


	public bool HasCompletedResult =>
		_hasHydrostaticResult;


	public long LatestCompletedGeneration =>
		_latestSolvedGeneration;


	public int LatestReadbackFrames =>
		_latestReadbackFrames;


	public int WaterPatchQueryCount =>
		_waterPatch?.QueryCount ??
		0;


	public int ValidVertexSamples =>
		_validVertexSamples;


	public float SubmergedArea =>
		_hasHydrostaticResult
			? _latestHydrostaticResult.SubmergedArea
			: 0.0f;


	public int SubmergedTriangleCount =>
		_hasHydrostaticResult
			? _latestHydrostaticResult.SubmergedTriangleCount
			: 0;


	public Vector3 LastHydrostaticForce =>
		_hasHydrostaticResult
			? _latestHydrostaticResult.Force
			: Vector3.Zero;


	public Vector3 LastHydrostaticTorque =>
		_hasHydrostaticResult
			? _latestHydrostaticResult.Torque
			: Vector3.Zero;


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


		ConsumeCompletedWaterPatch();


		ApplyLatestHydrostatics();


		SubmitCurrentWaterPatch();
	}


	private void ConsumeCompletedWaterPatch()
	{
		long generationBefore =
			_waterPatch.LatestGeneration;


		bool consumed =
			_waterPatch.ConsumeLatest();


		long completedGeneration =
			_waterPatch.LatestGeneration;


		if (!consumed)
		{
			//
			// LatestGeneration advances without ConsumeLatest() succeeding
			// only when a completed GPU result could not be paired with its
			// patch metadata. Do not keep applying stale buoyancy forever.
			//

			if (completedGeneration >
				generationBefore)
			{
				ClearHydrostaticResult();
			}


			return;
		}


		ref BodySnapshot snapshot =
			ref _bodySnapshots[
				SnapshotIndex(
					completedGeneration)];


		if (!snapshot.Valid ||
			snapshot.Generation !=
				completedGeneration)
		{
			ClearHydrostaticResult();

			return;
		}


		ReadOnlySpan<Vector3> localVertices =
			_hull.LocalVertices;


		_validVertexSamples =
			0;


		for (int i = 0;
			 i < localVertices.Length;
			 i++)
		{
			Vector3 worldVertex =
				snapshot.BodyTransform *
					localVertices[i];


			_worldVertices[i] =
				worldVertex;


			bool valid =
				_waterPatch.TrySampleSurface(
					new Vector2(
						worldVertex.X,
						worldVertex.Z),
					snapshot.SeaLevel,
					out float waterHeight,
					out _,
					out _);


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
			ClearHydrostaticResult();

			return;
		}


		OceanHydrostaticResult result =
			OceanHydrostaticSolver.Solve(
				_worldVertices,
				_hull.Indices,
				_waterHeights,
				snapshot.CenterOfMassWorld,
				WaterDensity,
				_gravity);


		if (!result.Force.IsFinite() ||
			!result.Torque.IsFinite())
		{
			ClearHydrostaticResult();

			return;
		}


		_latestHydrostaticResult =
			result;

		_latestSolvedGeneration =
			completedGeneration;

		_latestReadbackFrames =
			_waterPatch.LatestReadbackFrames;

		_hasHydrostaticResult =
			true;


		snapshot.Valid =
			false;
	}


	private void ApplyLatestHydrostatics()
	{
		if (!_hasHydrostaticResult)
		{
			return;
		}


		Vector3 force =
			_latestHydrostaticResult.Force;

		Vector3 torque =
			_latestHydrostaticResult.Torque;


		if (!force.IsFinite() ||
			!torque.IsFinite())
		{
			ClearHydrostaticResult();

			return;
		}


		//
		// Apply the equivalent resultant every physics tick until a newer
		// coherent async generation replaces it.
		//

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


	private void SubmitCurrentWaterPatch()
	{
		float minGridSize =
			MinSpatialLength /
				QueryScaleDivisor;


		long generation =
			_waterPatch.Submit(
				_body,
				_hull,
				PatchPadding,
				minGridSize);


		ref BodySnapshot snapshot =
			ref _bodySnapshots[
				SnapshotIndex(
					generation)];


		snapshot =
			new BodySnapshot
			{
				Valid =
					true,

				Generation =
					generation,

				BodyTransform =
					_body.GlobalTransform,

				CenterOfMassWorld =
					GetWorldCenterOfMass(),

				SeaLevel =
					SeaLevel,
			};
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


	private void ClearHydrostaticResult()
	{
		_latestHydrostaticResult =
			default;

		_hasHydrostaticResult =
			false;

		_validVertexSamples =
			0;

		_latestReadbackFrames =
			-1;
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


	private static int SnapshotIndex(
		long generation)
	{
		return
			(int)(
				generation %
				SnapshotCapacity);
	}
}
