using System;
using Godot;
using OceanFrontier.Water.Queries;
using OceanFrontier.Water.Runtime;

namespace OceanFrontier.Water.Physics.Hydrostatics;

/// <summary>
/// Small persistent GPU-sampled water patch used by OceanBuoyancy.
///
/// The patch is a horizontal XZ grid aligned to the rigid body's yaw.
/// It follows the projected footprint of the immutable OceanBuoyancyHull.
///
/// Query results are asynchronous. Every submitted generation therefore
/// stores its own patch transform (origin / axes / spacing). A completed
/// readback is accepted only when matching metadata for that exact
/// generation is still available.
///
/// The patch exposes bilinear water-height and water-velocity sampling
/// for arbitrary world XZ positions inside the completed grid.
///
/// No GPU resources are owned here. All queries go through the shared
/// OceanPointQueryService.
/// </summary>
internal sealed class OceanBuoyancyWaterPatch :
	IDisposable
{
	private const int SnapshotCapacity =
		32;

	private const float AxisEpsilonSquared =
		0.000001f;

	private const float StepEpsilon =
		0.000001f;


	private struct PatchSnapshot
	{
		public bool Valid;

		public long Generation;

		public Vector2 OriginXZ;

		public Vector2 AxisX;

		public Vector2 AxisZ;

		public float StepX;

		public float StepZ;
	}


	private readonly OceanRuntime _runtime;

	private readonly OceanPointQueryService.OwnerHandle _queryOwner;

	private readonly int _resolutionX;

	private readonly int _resolutionZ;

	private readonly int _queryCount;

	private readonly Vector2[] _queryPositions;

	private readonly Vector4[] _readbackResults;

	private readonly Vector4[] _latestResults;

	private readonly Vector4[] _readbackVelocities;

	private readonly Vector4[] _latestVelocities;

	private readonly PatchSnapshot[] _snapshots =
		new PatchSnapshot[
			SnapshotCapacity];


	private PatchSnapshot _latestSnapshot;

	private long _latestGeneration;

	private int _latestReadbackFrames =
		-1;

	private bool _hasLatest;

	private bool _hasLatestVelocity;

	private bool _disposed;


	public OceanBuoyancyWaterPatch(
		OceanRuntime runtime,
		int resolutionX,
		int resolutionZ)
	{
		_runtime =
			runtime ??
			throw new ArgumentNullException(
				nameof(runtime));


		if (resolutionX < 2)
		{
			throw new ArgumentOutOfRangeException(
				nameof(resolutionX),
				"Water patch X resolution must be at least 2.");
		}


		if (resolutionZ < 2)
		{
			throw new ArgumentOutOfRangeException(
				nameof(resolutionZ),
				"Water patch Z resolution must be at least 2.");
		}


		long queryCount =
			(long)resolutionX *
			resolutionZ;


		if (queryCount >
			OceanPointQueryService.Capacity)
		{
			throw new ArgumentOutOfRangeException(
				nameof(resolutionZ),
				$"Water patch requires {queryCount} points, " +
				$"but the shared query service limit is " +
				$"{OceanPointQueryService.Capacity}.");
		}


		_resolutionX =
			resolutionX;

		_resolutionZ =
			resolutionZ;

		_queryCount =
			(int)queryCount;


		_queryPositions =
			new Vector2[
				_queryCount];

		_readbackResults =
			new Vector4[
				_queryCount];

		_latestResults =
			new Vector4[
				_queryCount];

		_readbackVelocities =
			new Vector4[
				_queryCount];

		_latestVelocities =
			new Vector4[
				_queryCount];


		_queryOwner =
			_runtime.PointQueries.RegisterOwner(
				_queryCount);
	}


	public int ResolutionX =>
		_resolutionX;


	public int ResolutionZ =>
		_resolutionZ;


	public int QueryCount =>
		_queryCount;


	public bool HasLatest =>
		_hasLatest;


	public bool HasLatestVelocity =>
		_hasLatestVelocity;


	public long LatestGeneration =>
		_latestGeneration;


	public int LatestReadbackFrames =>
		_latestReadbackFrames;


	/// <summary>
	/// Consumes the newest completed GPU result, if any.
	///
	/// Results whose generation metadata has already fallen out of the
	/// fixed snapshot ring are discarded rather than interpreted against
	/// the wrong patch geometry.
	/// </summary>
	public bool ConsumeLatest()
	{
		ThrowIfDisposed();


		if (!_runtime.PointQueries.TryCopyLatest(
				_queryOwner,
				_readbackResults,
				out int count,
				out long generation,
				out _,
				out int readbackFrames) ||
			generation <=
				_latestGeneration ||
			count !=
				_queryCount)
		{
			return false;
		}


		ref PatchSnapshot snapshot =
			ref _snapshots[
				SnapshotIndex(
					generation)];


		if (!snapshot.Valid ||
			snapshot.Generation !=
				generation)
		{
			//
			// Never reinterpret a GPU result using geometry from a
			// different submission.
			//

			_latestGeneration =
				generation;

			_latestReadbackFrames =
				readbackFrames;

			_hasLatest =
				false;

			_hasLatestVelocity =
				false;

			return false;
		}


		_readbackResults
			.AsSpan()
			.CopyTo(
				_latestResults);


		bool hasMatchingVelocity =
			_runtime.PointQueries.TryCopyLatestVelocities(
				_queryOwner,
				_readbackVelocities,
				out int velocityCount,
				out long velocityGeneration) &&
			velocityCount ==
				_queryCount &&
			velocityGeneration ==
				generation;


		if (hasMatchingVelocity)
		{
			_readbackVelocities
				.AsSpan()
				.CopyTo(
					_latestVelocities);
		}


		_latestSnapshot =
			snapshot;

		_latestGeneration =
			generation;

		_latestReadbackFrames =
			readbackFrames;

		_hasLatest =
			true;

		_hasLatestVelocity =
			hasMatchingVelocity;


		return true;
	}


	/// <summary>
	/// Builds and submits a new yaw-aligned horizontal patch covering the
	/// current projected hull footprint.
	///
	/// padding is added independently on all four horizontal sides.
	///
	/// minGridSize is passed unchanged to the Crest-style AWF query LOD
	/// selector. The owning OceanBuoyancy decides the physics filtering
	/// policy; this class only owns spatial sampling.
	/// </summary>
	public long Submit(
		RigidBody3D body,
		OceanBuoyancyHull hull,
		float padding,
		float minGridSize)
	{
		ThrowIfDisposed();


		if (body == null)
		{
			throw new ArgumentNullException(
				nameof(body));
		}


		if (hull == null ||
			!hull.IsBuilt)
		{
			throw new ArgumentException(
				"Water patch requires a built OceanBuoyancyHull.",
				nameof(hull));
		}


		if (!float.IsFinite(padding) ||
			padding < 0.0f)
		{
			throw new ArgumentOutOfRangeException(
				nameof(padding));
		}


		if (!float.IsFinite(minGridSize) ||
			minGridSize < 0.0f)
		{
			throw new ArgumentOutOfRangeException(
				nameof(minGridSize));
		}


		BuildHorizontalAxes(
			body,
			out Vector2 axisX,
			out Vector2 axisZ);


		Transform3D bodyTransform =
			body.GlobalTransform;


		Vector3 bodyOrigin3 =
			bodyTransform.Origin;


		Vector2 bodyOrigin =
			new(
				bodyOrigin3.X,
				bodyOrigin3.Z);


		float minX =
			float.PositiveInfinity;

		float maxX =
			float.NegativeInfinity;

		float minZ =
			float.PositiveInfinity;

		float maxZ =
			float.NegativeInfinity;


		ReadOnlySpan<Vector3> localVertices =
			hull.LocalVertices;


		for (int i = 0;
			 i < localVertices.Length;
			 i++)
		{
			Vector3 world =
				bodyTransform *
				localVertices[i];


			Vector2 delta =
				new Vector2(
					world.X,
					world.Z) -
				bodyOrigin;


			float x =
				delta.Dot(
					axisX);

			float z =
				delta.Dot(
					axisZ);


			minX =
				MathF.Min(
					minX,
					x);

			maxX =
				MathF.Max(
					maxX,
					x);

			minZ =
				MathF.Min(
					minZ,
					z);

			maxZ =
				MathF.Max(
					maxZ,
					z);
		}


		if (!float.IsFinite(minX) ||
			!float.IsFinite(maxX) ||
			!float.IsFinite(minZ) ||
			!float.IsFinite(maxZ))
		{
			throw new InvalidOperationException(
				"Projected buoyancy hull bounds are invalid.");
		}


		minX -=
			padding;

		maxX +=
			padding;

		minZ -=
			padding;

		maxZ +=
			padding;


		float spanX =
			maxX -
				minX;

		float spanZ =
			maxZ -
				minZ;


		if (spanX <=
				StepEpsilon ||
			spanZ <=
				StepEpsilon)
		{
			throw new InvalidOperationException(
				"Projected buoyancy hull footprint is too small to build a water patch.");
		}


		float stepX =
			spanX /
			(_resolutionX -
			 1);

		float stepZ =
			spanZ /
			(_resolutionZ -
			 1);


		Vector2 origin =
			bodyOrigin +
			axisX *
				minX +
			axisZ *
				minZ;


		int index =
			0;


		for (int z = 0;
			 z < _resolutionZ;
			 z++)
		{
			Vector2 rowOrigin =
				origin +
				axisZ *
					(stepZ *
					 z);


			for (int x = 0;
				 x < _resolutionX;
				 x++)
			{
				_queryPositions[index++] =
					rowOrigin +
					axisX *
						(stepX *
						 x);
			}
		}


		long generation =
			_runtime.PointQueries.SubmitBatch(
				_queryOwner,
				_queryPositions,
				minGridSize);


		ref PatchSnapshot snapshot =
			ref _snapshots[
				SnapshotIndex(
					generation)];


		snapshot =
			new PatchSnapshot
			{
				Valid =
					true,

				Generation =
					generation,

				OriginXZ =
					origin,

				AxisX =
					axisX,

				AxisZ =
					axisZ,

				StepX =
					stepX,

				StepZ =
					stepZ,
			};


		return generation;
	}


	/// <summary>
	/// Bilinearly samples the latest complete patch.
	///
	/// surfaceHeight is absolute world Y:
	///
	///     seaLevel + AnimatedWaveField displacement.Y
	///
	/// waterVelocity follows OceanPointQueryService finite-difference
	/// semantics. hasVelocity is false when no coherent velocity generation
	/// exists, while the height sample may still be valid.
	/// </summary>
	public bool TrySampleSurface(
		Vector2 worldXZ,
		float seaLevel,
		out float surfaceHeight,
		out Vector3 waterVelocity,
		out bool hasVelocity)
	{
		ThrowIfDisposed();


		surfaceHeight =
			float.NaN;

		waterVelocity =
			Vector3.Zero;

		hasVelocity =
			false;


		if (!_hasLatest ||
			!float.IsFinite(worldXZ.X) ||
			!float.IsFinite(worldXZ.Y) ||
			!float.IsFinite(seaLevel))
		{
			return false;
		}


		if (!TryGetGridCoordinates(
				worldXZ,
				_latestSnapshot,
				out int x0,
				out int x1,
				out int z0,
				out int z1,
				out float tx,
				out float tz))
		{
			return false;
		}


		int i00 =
			GridIndex(
				x0,
				z0);

		int i10 =
			GridIndex(
				x1,
				z0);

		int i01 =
			GridIndex(
				x0,
				z1);

		int i11 =
			GridIndex(
				x1,
				z1);


		Vector4 r00 =
			_latestResults[i00];

		Vector4 r10 =
			_latestResults[i10];

		Vector4 r01 =
			_latestResults[i01];

		Vector4 r11 =
			_latestResults[i11];


		if (!IsValidResult(
				r00) ||
			!IsValidResult(
				r10) ||
			!IsValidResult(
				r01) ||
			!IsValidResult(
				r11))
		{
			return false;
		}


		float y0 =
			Mathf.Lerp(
				r00.Y,
				r10.Y,
				tx);

		float y1 =
			Mathf.Lerp(
				r01.Y,
				r11.Y,
				tx);


		float displacementY =
			Mathf.Lerp(
				y0,
				y1,
				tz);


		surfaceHeight =
			seaLevel +
				displacementY;


		if (!float.IsFinite(
				surfaceHeight))
		{
			surfaceHeight =
				float.NaN;

			return false;
		}


		if (!_hasLatestVelocity)
		{
			return true;
		}


		Vector4 v00 =
			_latestVelocities[i00];

		Vector4 v10 =
			_latestVelocities[i10];

		Vector4 v01 =
			_latestVelocities[i01];

		Vector4 v11 =
			_latestVelocities[i11];


		if (!IsValidVelocity(
				v00) ||
			!IsValidVelocity(
				v10) ||
			!IsValidVelocity(
				v01) ||
			!IsValidVelocity(
				v11))
		{
			return true;
		}


		Vector3 velocity00 =
			new(
				v00.X,
				v00.Y,
				v00.Z);

		Vector3 velocity10 =
			new(
				v10.X,
				v10.Y,
				v10.Z);

		Vector3 velocity01 =
			new(
				v01.X,
				v01.Y,
				v01.Z);

		Vector3 velocity11 =
			new(
				v11.X,
				v11.Y,
				v11.Z);


		Vector3 velocity0 =
			velocity00.Lerp(
				velocity10,
				tx);

		Vector3 velocity1 =
			velocity01.Lerp(
				velocity11,
				tx);


		waterVelocity =
			velocity0.Lerp(
				velocity1,
				tz);


		hasVelocity =
			waterVelocity.IsFinite();


		if (!hasVelocity)
		{
			waterVelocity =
				Vector3.Zero;
		}


		return true;
	}


	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}


		_disposed =
			true;


		_runtime.PointQueries.UnregisterOwner(
			_queryOwner);


		_hasLatest =
			false;

		_hasLatestVelocity =
			false;

		_latestGeneration =
			0;

		_latestReadbackFrames =
			-1;


		Array.Clear(
			_snapshots,
			0,
			_snapshots.Length);

		Array.Clear(
			_latestResults,
			0,
			_latestResults.Length);

		Array.Clear(
			_latestVelocities,
			0,
			_latestVelocities.Length);
	}


	private bool TryGetGridCoordinates(
		Vector2 worldXZ,
		PatchSnapshot snapshot,
		out int x0,
		out int x1,
		out int z0,
		out int z1,
		out float tx,
		out float tz)
	{
		x0 =
			0;

		x1 =
			0;

		z0 =
			0;

		z1 =
			0;

		tx =
			0.0f;

		tz =
			0.0f;


		if (!snapshot.Valid ||
			snapshot.StepX <=
				StepEpsilon ||
			snapshot.StepZ <=
				StepEpsilon)
		{
			return false;
		}


		Vector2 delta =
			worldXZ -
				snapshot.OriginXZ;


		float gx =
			delta.Dot(
				snapshot.AxisX) /
				snapshot.StepX;

		float gz =
			delta.Dot(
				snapshot.AxisZ) /
				snapshot.StepZ;


		//
		// No spatial extrapolation.
		//
		// The owning buoyancy component should provide enough patch padding
		// to absorb body movement during asynchronous readback latency.
		//

		if (gx < 0.0f ||
			gz < 0.0f ||
			gx >
				_resolutionX -
					1 ||
			gz >
				_resolutionZ -
					1)
		{
			return false;
		}


		x0 =
			Math.Min(
				(int)MathF.Floor(
					gx),
				_resolutionX -
					2);

		z0 =
			Math.Min(
				(int)MathF.Floor(
					gz),
				_resolutionZ -
					2);


		x1 =
			x0 +
			1;

		z1 =
			z0 +
			1;


		tx =
			Mathf.Clamp(
				gx -
					x0,
				0.0f,
				1.0f);

		tz =
			Mathf.Clamp(
				gz -
					z0,
				0.0f,
				1.0f);


		return true;
	}


	private int GridIndex(
		int x,
		int z)
	{
		return
			x +
			z *
				_resolutionX;
	}


	private static bool IsValidResult(
		Vector4 value)
	{
		return
			value.W >
				0.5f &&
			float.IsFinite(
				value.X) &&
			float.IsFinite(
				value.Y) &&
			float.IsFinite(
				value.Z);
	}


	private static bool IsValidVelocity(
		Vector4 value)
	{
		return
			value.W >
				0.5f &&
			float.IsFinite(
				value.X) &&
			float.IsFinite(
				value.Y) &&
			float.IsFinite(
				value.Z);
	}


	private static void BuildHorizontalAxes(
		RigidBody3D body,
		out Vector2 axisX,
		out Vector2 axisZ)
	{
		Vector3 forward =
			body.GlobalBasis.Z;


		Vector2 forwardXZ =
			new(
				forward.X,
				forward.Z);


		if (forwardXZ.LengthSquared() >
			AxisEpsilonSquared)
		{
			axisZ =
				forwardXZ.Normalized();


			//
			// +Y up, +Z identity forward-axis gives +X here.
			//

			axisX =
				new Vector2(
					axisZ.Y,
					-axisZ.X);


			return;
		}


		Vector3 right =
			body.GlobalBasis.X;


		Vector2 rightXZ =
			new(
				right.X,
				right.Z);


		if (rightXZ.LengthSquared() <=
			AxisEpsilonSquared)
		{
			//
			// A rigid body can only reach this state at a pathological
			// orientation where both projected local X and Z are unusable.
			//

			axisX =
				Vector2.Right;

			axisZ =
				Vector2.Down;

			return;
		}


		axisX =
			rightXZ.Normalized();


		axisZ =
			new Vector2(
				-axisX.Y,
				axisX.X);
	}


	private static int SnapshotIndex(
		long generation)
	{
		return
			(int)(
				generation %
				SnapshotCapacity);
	}


	private void ThrowIfDisposed()
	{
		if (_disposed)
		{
			throw new ObjectDisposedException(
				nameof(
					OceanBuoyancyWaterPatch));
		}
	}
}
