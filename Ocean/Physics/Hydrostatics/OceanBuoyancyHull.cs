using System;
using System.Collections.Generic;
using Godot;

namespace OceanFrontier.Water.Physics.Hydrostatics;

/// <summary>
/// Immutable low-poly physics hull consumed by OceanBuoyancy.
///
/// Attach this node directly below the RigidBody3D which owns the hull,
/// then assign <see cref="HullMesh"/> to a dedicated low-poly MeshInstance3D.
///
/// Build-time responsibilities:
///
/// - extract the mesh triangle faces;
/// - transform mesh vertices into rigid-body local space;
/// - weld positional duplicates across surfaces / UV seams;
/// - reject degenerate triangles;
/// - verify a closed two-manifold topology;
/// - verify locally consistent triangle orientation;
/// - compute signed enclosed volume;
/// - canonicalize winding so (b - a) x (c - a) points outward.
///
/// Runtime water sampling and force integration do not belong here.
/// After Build(), the vertex/index arrays are immutable until Rebuild()
/// is explicitly called.
/// </summary>
[GlobalClass]
public partial class OceanBuoyancyHull : Node
{
	private const float DefaultWeldTolerance =
		0.00001f;

	private const float MinimumWeldTolerance =
		0.0000001f;

	private const double MinimumAbsoluteVolume =
		1.0e-9;

	private const float MinimumTriangleAreaSquared =
		1.0e-12f;


	[Export]
	public MeshInstance3D HullMesh { get; set; }


	[Export(PropertyHint.Range, "0.0000001,0.01,0.0000001,or_greater")]
	public float WeldTolerance { get; set; } =
		DefaultWeldTolerance;


	private RigidBody3D _body;

	private Vector3[] _localVertices =
		Array.Empty<Vector3>();

	private int[] _indices =
		Array.Empty<int>();

	private Aabb _localBounds;

	private double _volume;

	private bool _isBuilt;

	private bool _windingWasFlipped;


	public bool IsBuilt =>
		_isBuilt;


	public int VertexCount =>
		_localVertices.Length;


	public int TriangleCount =>
		_indices.Length /
		3;


	public double Volume =>
		_volume;


	public Aabb LocalBounds =>
		_localBounds;


	public bool WindingWasFlipped =>
		_windingWasFlipped;


	internal ReadOnlySpan<Vector3> LocalVertices =>
		_localVertices;


	internal ReadOnlySpan<int> Indices =>
		_indices;


	public override void _Ready()
	{
		Rebuild();
	}


	/// <summary>
	/// Rebuilds the immutable physics-hull representation from HullMesh.
	///
	/// This is intentionally not called per frame. If the source mesh or its
	/// transform relative to the RigidBody3D changes at runtime, the caller
	/// must explicitly request a rebuild.
	/// </summary>
	public void Rebuild()
	{
		_isBuilt =
			false;

		_windingWasFlipped =
			false;

		_localVertices =
			Array.Empty<Vector3>();

		_indices =
			Array.Empty<int>();

		_localBounds =
			default;

		_volume =
			0.0;


		_body =
			GetParent() as RigidBody3D;


		if (_body == null)
		{
			throw new InvalidOperationException(
				$"{nameof(OceanBuoyancyHull)} must be a direct child of a RigidBody3D.");
		}


		if (HullMesh == null)
		{
			throw new InvalidOperationException(
				$"{nameof(OceanBuoyancyHull)} requires an assigned {nameof(MeshInstance3D)}.");
		}


		Mesh mesh =
			HullMesh.Mesh;


		if (mesh == null)
		{
			throw new InvalidOperationException(
				$"{nameof(HullMesh)} has no Mesh resource.");
		}


		float weldTolerance =
			MathF.Max(
				MinimumWeldTolerance,
				WeldTolerance);


		Transform3D meshToBody =
			_body.GlobalTransform
				.AffineInverse() *
			HullMesh.GlobalTransform;


		var vertices =
			new List<Vector3>();


		var indices =
			new List<int>();


		var weldedVertices =
			new Dictionary<VertexKey, int>();


		Vector3[] faceVertices =
			mesh.GetFaces();


		if (faceVertices == null ||
			faceVertices.Length == 0)
		{
			throw new InvalidOperationException(
				"Buoyancy hull mesh contains no triangle faces.");
		}


		if (faceVertices.Length %
			3 !=
			0)
		{
			throw new InvalidOperationException(
				"Buoyancy hull face vertex count is not divisible by three.");
		}


		for (int i = 0;
			 i < faceVertices.Length;
			 i += 3)
		{
			int ia =
				GetOrAddWeldedVertex(
					meshToBody *
						faceVertices[i],
					weldTolerance,
					vertices,
					weldedVertices,
					i);


			int ib =
				GetOrAddWeldedVertex(
					meshToBody *
						faceVertices[i + 1],
					weldTolerance,
					vertices,
					weldedVertices,
					i + 1);


			int ic =
				GetOrAddWeldedVertex(
					meshToBody *
						faceVertices[i + 2],
					weldTolerance,
					vertices,
					weldedVertices,
					i + 2);


			AddTriangle(
				ia,
				ib,
				ic,
				vertices,
				indices,
				0,
				i /
					3);
		}

		if (vertices.Count < 4)
		{
			throw new InvalidOperationException(
				"Buoyancy hull must contain at least four unique vertices.");
		}


		if (indices.Count < 12)
		{
			throw new InvalidOperationException(
				"Buoyancy hull must contain at least four triangles.");
		}


		ValidateClosedTwoManifold(
			indices);


		double signedVolume =
			ComputeSignedVolume(
				vertices,
				indices);


		if (!double.IsFinite(signedVolume) ||
			Math.Abs(signedVolume) <=
				MinimumAbsoluteVolume)
		{
			throw new InvalidOperationException(
				"Buoyancy hull has zero or invalid enclosed volume. " +
				"The physics hull must be a closed watertight mesh.");
		}


		//
		// Godot renders triangle front faces using clockwise winding.
		// Hydrostatics instead needs the mathematical cross product
		//
		//     (b - a) x (c - a)
		//
		// to point outward. For a closed right-handed mesh this corresponds
		// to positive signed volume. Flip every triangle once if required.
		//

		if (signedVolume < 0.0)
		{
			FlipAllTriangles(
				indices);


			signedVolume =
				-signedVolume;


			_windingWasFlipped =
				true;
		}


		//
		// Re-check orientation after canonicalization. Edge consistency is
		// unchanged by a global flip, but keeping this check here protects
		// future modifications to the build pipeline.
		//

		ValidateClosedTwoManifold(
			indices);


		_localVertices =
			vertices.ToArray();

		_indices =
			indices.ToArray();

		_localBounds =
			ComputeBounds(
				_localVertices);

		_volume =
			signedVolume;

		_isBuilt =
			true;


		GD.Print(
			$"[Ocean] Buoyancy hull ready: " +
			$"{VertexCount} welded vertices, " +
			$"{TriangleCount} triangles, " +
			$"volume {Volume:0.###} m³, " +
			$"winding flip={_windingWasFlipped}.");
	}




	private static int GetOrAddWeldedVertex(
		Vector3 bodyLocal,
		float weldTolerance,
		List<Vector3> vertices,
		Dictionary<VertexKey, int> weldedVertices,
		int faceVertexIndex)
	{
		if (!bodyLocal.IsFinite())
		{
			throw new InvalidOperationException(
				$"Buoyancy hull contains a non-finite face vertex at index {faceVertexIndex}.");
		}


		VertexKey key =
			VertexKey.From(
				bodyLocal,
				weldTolerance);


		if (weldedVertices.TryGetValue(
				key,
				out int canonicalIndex))
		{
			return canonicalIndex;
		}


		canonicalIndex =
			vertices.Count;


		weldedVertices.Add(
			key,
			canonicalIndex);


		vertices.Add(
			bodyLocal);


		return canonicalIndex;
	}


	private static void AddTriangle(
		int ia,
		int ib,
		int ic,
		List<Vector3> vertices,
		List<int> indices,
		int surface,
		int triangle)
	{
		if (ia == ib ||
			ib == ic ||
			ic == ia)
		{
			throw new InvalidOperationException(
				$"Buoyancy hull contains a degenerate triangle after vertex welding " +
				$"(surface {surface}, triangle {triangle}).");
		}


		Vector3 a =
			vertices[ia];

		Vector3 b =
			vertices[ib];

		Vector3 c =
			vertices[ic];


		float areaSquaredTimesFour =
			(b - a)
			.Cross(
				c - a)
			.LengthSquared();


		if (!float.IsFinite(
				areaSquaredTimesFour) ||
			areaSquaredTimesFour <=
				MinimumTriangleAreaSquared)
		{
			throw new InvalidOperationException(
				$"Buoyancy hull contains a zero-area triangle " +
				$"(surface {surface}, triangle {triangle}).");
		}


		indices.Add(
			ia);

		indices.Add(
			ib);

		indices.Add(
			ic);
	}


	/// <summary>
	/// A valid hydrostatic hull is a consistently-oriented closed
	/// two-manifold: every undirected edge occurs exactly twice and the two
	/// directed occurrences run in opposite directions.
	///
	/// This rejects holes, T-junction/non-manifold topology and isolated
	/// reversed faces before they can produce invalid hydrostatic forces.
	/// </summary>
	private static void ValidateClosedTwoManifold(
		List<int> indices)
	{
		var edges =
			new Dictionary<EdgeKey, EdgeUse>(
				indices.Count);


		for (int i = 0;
			 i < indices.Count;
			 i += 3)
		{
			AccumulateEdge(
				indices[i],
				indices[i + 1],
				edges);


			AccumulateEdge(
				indices[i + 1],
				indices[i + 2],
				edges);


			AccumulateEdge(
				indices[i + 2],
				indices[i],
				edges);
		}


		foreach (KeyValuePair<EdgeKey, EdgeUse> pair in
				 edges)
		{
			EdgeUse use =
				pair.Value;


			if (use.Count !=
				2)
			{
				throw new InvalidOperationException(
					$"Buoyancy hull is not watertight/two-manifold: edge " +
					$"{pair.Key.A}-{pair.Key.B} is used {use.Count} time(s), expected 2.");
			}


			if (use.DirectionBalance !=
				0)
			{
				throw new InvalidOperationException(
					$"Buoyancy hull has inconsistent local winding around edge " +
					$"{pair.Key.A}-{pair.Key.B}. Adjacent triangles must traverse " +
					"the shared edge in opposite directions.");
			}
		}
	}


	private static void AccumulateEdge(
		int from,
		int to,
		Dictionary<EdgeKey, EdgeUse> edges)
	{
		EdgeKey key =
			new(
				from,
				to);


		int direction =
			from < to
				? 1
				: -1;


		if (edges.TryGetValue(
				key,
				out EdgeUse current))
		{
			current.Count++;

			current.DirectionBalance +=
				direction;


			edges[key] =
				current;
		}
		else
		{
			edges.Add(
				key,
				new EdgeUse
				{
					Count =
						1,

					DirectionBalance =
						direction,
				});
		}
	}


	/// <summary>
	/// Signed volume of an oriented closed triangle mesh:
	///
	///     V = Σ dot(a, b × c) / 6
	///
	/// Positive volume corresponds to mathematical outward winding in
	/// Godot's right-handed coordinate system.
	/// </summary>
	private static double ComputeSignedVolume(
		List<Vector3> vertices,
		List<int> indices)
	{
		double sixVolume =
			0.0;


		for (int i = 0;
			 i < indices.Count;
			 i += 3)
		{
			Vector3 a =
				vertices[
					indices[i]];

			Vector3 b =
				vertices[
					indices[i + 1]];

			Vector3 c =
				vertices[
					indices[i + 2]];


			sixVolume +=
				(double)a.X *
					(
						(double)b.Y *
							c.Z -
						(double)b.Z *
							c.Y
					) +

				(double)a.Y *
					(
						(double)b.Z *
							c.X -
						(double)b.X *
							c.Z
					) +

				(double)a.Z *
					(
						(double)b.X *
							c.Y -
						(double)b.Y *
							c.X
					);
		}


		return
			sixVolume /
			6.0;
	}


	private static void FlipAllTriangles(
		List<int> indices)
	{
		for (int i = 0;
			 i < indices.Count;
			 i += 3)
		{
			(indices[i + 1],
			 indices[i + 2]) =
				(indices[i + 2],
				 indices[i + 1]);
		}
	}


	private static Aabb ComputeBounds(
		Vector3[] vertices)
	{
		Vector3 min =
			vertices[0];

		Vector3 max =
			vertices[0];


		for (int i = 1;
			 i < vertices.Length;
			 i++)
		{
			Vector3 p =
				vertices[i];


			min =
				new Vector3(
					MathF.Min(
						min.X,
						p.X),

					MathF.Min(
						min.Y,
						p.Y),

					MathF.Min(
						min.Z,
						p.Z));


			max =
				new Vector3(
					MathF.Max(
						max.X,
						p.X),

					MathF.Max(
						max.Y,
						p.Y),

					MathF.Max(
						max.Z,
						p.Z));
		}


		return
			new Aabb(
				min,
				max -
					min);
	}


	private readonly struct VertexKey :
		IEquatable<VertexKey>
	{
		private VertexKey(
			long x,
			long y,
			long z)
		{
			X =
				x;

			Y =
				y;

			Z =
				z;
		}


		public long X { get; }

		public long Y { get; }

		public long Z { get; }


		public static VertexKey From(
			Vector3 vertex,
			float tolerance)
		{
			double inverse =
				1.0 /
				tolerance;


			return
				new VertexKey(
					Quantize(
						vertex.X,
						inverse),

					Quantize(
						vertex.Y,
						inverse),

					Quantize(
						vertex.Z,
						inverse));
		}


		public bool Equals(
			VertexKey other)
		{
			return
				X ==
					other.X &&
				Y ==
					other.Y &&
				Z ==
					other.Z;
		}


		public override bool Equals(
			object obj)
		{
			return
				obj is VertexKey other &&
				Equals(
					other);
		}


		public override int GetHashCode()
		{
			return
				HashCode.Combine(
					X,
					Y,
					Z);
		}


		private static long Quantize(
			float value,
			double inverseTolerance)
		{
			double scaled =
				value *
				inverseTolerance;


			if (!double.IsFinite(
					scaled) ||
				scaled >
					long.MaxValue ||
				scaled <
					long.MinValue)
			{
				throw new InvalidOperationException(
					"Buoyancy hull vertex is outside the supported welding range.");
			}


			return
				(long)Math.Round(
					scaled,
					MidpointRounding.AwayFromZero);
		}
	}


	private readonly struct EdgeKey :
		IEquatable<EdgeKey>
	{
		public EdgeKey(
			int a,
			int b)
		{
			if (a < b)
			{
				A =
					a;

				B =
					b;
			}
			else
			{
				A =
					b;

				B =
					a;
			}
		}


		public int A { get; }

		public int B { get; }


		public bool Equals(
			EdgeKey other)
		{
			return
				A ==
					other.A &&
				B ==
					other.B;
		}


		public override bool Equals(
			object obj)
		{
			return
				obj is EdgeKey other &&
				Equals(
					other);
		}


		public override int GetHashCode()
		{
			return
				HashCode.Combine(
					A,
					B);
		}
	}


	private struct EdgeUse
	{
		public int Count;

		public int DirectionBalance;
	}
}
