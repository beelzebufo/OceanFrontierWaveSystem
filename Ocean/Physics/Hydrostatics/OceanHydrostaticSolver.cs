using System;
using Godot;

namespace OceanFrontier.Water.Physics.Hydrostatics;

/// <summary>
/// Result of one hydrostatic solve over a triangulated physics hull.
///
/// Force and torque are world-space values.
/// Torque is accumulated about the supplied rigid-body center of mass.
///
/// This solver intentionally contains no damping, drag, slamming,
/// query management, or Godot scene orchestration.
/// </summary>
internal readonly struct OceanHydrostaticResult
{
	public OceanHydrostaticResult(
		Vector3 force,
		Vector3 torque,
		float submergedArea,
		int submergedTriangleCount)
	{
		Force =
			force;

		Torque =
			torque;

		SubmergedArea =
			submergedArea;

		SubmergedTriangleCount =
			submergedTriangleCount;
	}


	public Vector3 Force { get; }

	public Vector3 Torque { get; }

	public float SubmergedArea { get; }

	public int SubmergedTriangleCount { get; }

	public bool HasBuoyancy =>
		SubmergedTriangleCount > 0 &&
		Force.IsFinite() &&
		Torque.IsFinite();
}


/// <summary>
/// CPU hydrostatic solver for a low-poly physics hull.
///
/// Input contract:
///
/// - worldVertices contains the hull vertices in world space;
/// - indices contains triangles with OUTWARD winding;
/// - waterHeights contains one authoritative water-surface Y value
///   for each world vertex;
/// - invalid water samples are represented by NaN and cause every
///   triangle using that vertex to be skipped;
/// - the water surface is treated as linearly varying across each
///   original hull triangle.
///
/// The clipping stage follows the Kerner/Habrador 0/1/2/3 submerged
/// vertex model. Each source triangle produces 0, 1, or 2 fully wet
/// triangles.
///
/// Hydrostatic pressure is integrated analytically over each wet
/// triangle. This yields the exact resultant force and center of
/// pressure for the piecewise-linear depth field represented by the
/// three vertex water heights.
///
/// No per-frame allocations are performed.
/// </summary>
internal static class OceanHydrostaticSolver
{
	private const float SurfaceEpsilon =
		0.00001f;

	private const float AreaEpsilon =
		0.00000001f;

	private const float DepthSumEpsilon =
		0.000001f;


	public static OceanHydrostaticResult Solve(
		ReadOnlySpan<Vector3> worldVertices,
		ReadOnlySpan<int> indices,
		ReadOnlySpan<float> waterHeights,
		Vector3 centerOfMassWorld,
		float fluidDensity,
		float gravityMagnitude)
	{
		if (worldVertices.Length !=
			waterHeights.Length)
		{
			throw new ArgumentException(
				"worldVertices and waterHeights must have the same length.");
		}


		if (indices.Length % 3 != 0)
		{
			throw new ArgumentException(
				"indices length must be divisible by 3.",
				nameof(indices));
		}


		if (!centerOfMassWorld.IsFinite())
		{
			throw new ArgumentException(
				"Center of mass must be finite.",
				nameof(centerOfMassWorld));
		}


		if (!float.IsFinite(fluidDensity) ||
			fluidDensity <= 0.0f)
		{
			throw new ArgumentOutOfRangeException(
				nameof(fluidDensity));
		}


		if (!float.IsFinite(gravityMagnitude) ||
			gravityMagnitude <= 0.0f)
		{
			throw new ArgumentOutOfRangeException(
				nameof(gravityMagnitude));
		}


		Vector3 totalForce =
			Vector3.Zero;

		Vector3 totalTorque =
			Vector3.Zero;

		float submergedArea =
			0.0f;

		int submergedTriangleCount =
			0;


		for (int i = 0;
			 i < indices.Length;
			 i += 3)
		{
			int ia =
				indices[i];

			int ib =
				indices[i + 1];

			int ic =
				indices[i + 2];


			if ((uint)ia >=
					(uint)worldVertices.Length ||
				(uint)ib >=
					(uint)worldVertices.Length ||
				(uint)ic >=
					(uint)worldVertices.Length)
			{
				throw new ArgumentOutOfRangeException(
					nameof(indices),
					"Hull triangle index is outside the vertex array.");
			}


			Vector3 a =
				worldVertices[ia];

			Vector3 b =
				worldVertices[ib];

			Vector3 c =
				worldVertices[ic];


			float ha =
				waterHeights[ia];

			float hb =
				waterHeights[ib];

			float hc =
				waterHeights[ic];


			if (!a.IsFinite() ||
				!b.IsFinite() ||
				!c.IsFinite() ||
				!float.IsFinite(ha) ||
				!float.IsFinite(hb) ||
				!float.IsFinite(hc))
			{
				continue;
			}


			float da =
				a.Y -
				ha;

			float db =
				b.Y -
				hb;

			float dc =
				c.Y -
				hc;


			int wetMask =
				(da < -SurfaceEpsilon
					? 0b100
					: 0) |

				(db < -SurfaceEpsilon
					? 0b010
					: 0) |

				(dc < -SurfaceEpsilon
					? 0b001
					: 0);


			switch (wetMask)
			{
				case 0b000:
					break;


				case 0b111:
					AccumulateWetTriangle(
						a,
						b,
						c,
						ha,
						hb,
						hc,
						centerOfMassWorld,
						fluidDensity,
						gravityMagnitude,
						ref totalForce,
						ref totalTorque,
						ref submergedArea,
						ref submergedTriangleCount);

					break;


				case 0b100:
					AccumulateOneWet(
						a,
						b,
						c,
						ha,
						hb,
						hc,
						centerOfMassWorld,
						fluidDensity,
						gravityMagnitude,
						ref totalForce,
						ref totalTorque,
						ref submergedArea,
						ref submergedTriangleCount);

					break;


				case 0b010:
					AccumulateOneWet(
						b,
						c,
						a,
						hb,
						hc,
						ha,
						centerOfMassWorld,
						fluidDensity,
						gravityMagnitude,
						ref totalForce,
						ref totalTorque,
						ref submergedArea,
						ref submergedTriangleCount);

					break;


				case 0b001:
					AccumulateOneWet(
						c,
						a,
						b,
						hc,
						ha,
						hb,
						centerOfMassWorld,
						fluidDensity,
						gravityMagnitude,
						ref totalForce,
						ref totalTorque,
						ref submergedArea,
						ref submergedTriangleCount);

					break;


				case 0b011:
					AccumulateTwoWet(
						a,
						b,
						c,
						ha,
						hb,
						hc,
						centerOfMassWorld,
						fluidDensity,
						gravityMagnitude,
						ref totalForce,
						ref totalTorque,
						ref submergedArea,
						ref submergedTriangleCount);

					break;


				case 0b101:
					AccumulateTwoWet(
						b,
						c,
						a,
						hb,
						hc,
						ha,
						centerOfMassWorld,
						fluidDensity,
						gravityMagnitude,
						ref totalForce,
						ref totalTorque,
						ref submergedArea,
						ref submergedTriangleCount);

					break;


				case 0b110:
					AccumulateTwoWet(
						c,
						a,
						b,
						hc,
						ha,
						hb,
						centerOfMassWorld,
						fluidDensity,
						gravityMagnitude,
						ref totalForce,
						ref totalTorque,
						ref submergedArea,
						ref submergedTriangleCount);

					break;
			}
		}


		return
			new OceanHydrostaticResult(
				totalForce,
				totalTorque,
				submergedArea,
				submergedTriangleCount);
	}


	/// <summary>
	/// Exactly one source vertex is wet.
	///
	/// Argument order is cyclic relative to the original triangle:
	///
	///     wet -> dry1 -> dry2
	///
	/// so the generated wet triangle preserves source winding.
	/// </summary>
	private static void AccumulateOneWet(
		Vector3 wet,
		Vector3 dry1,
		Vector3 dry2,
		float hWet,
		float hDry1,
		float hDry2,
		Vector3 centerOfMassWorld,
		float fluidDensity,
		float gravityMagnitude,
		ref Vector3 totalForce,
		ref Vector3 totalTorque,
		ref float submergedArea,
		ref int submergedTriangleCount)
	{
		if (!TryClipEdge(
				wet,
				dry1,
				hWet,
				hDry1,
				out Vector3 p1,
				out float hp1) ||
			!TryClipEdge(
				wet,
				dry2,
				hWet,
				hDry2,
				out Vector3 p2,
				out float hp2))
		{
			return;
		}


		AccumulateWetTriangle(
			wet,
			p1,
			p2,
			hWet,
			hp1,
			hp2,
			centerOfMassWorld,
			fluidDensity,
			gravityMagnitude,
			ref totalForce,
			ref totalTorque,
			ref submergedArea,
			ref submergedTriangleCount);
	}


	/// <summary>
	/// Exactly two source vertices are wet.
	///
	/// Argument order is cyclic relative to the original triangle:
	///
	///     dry -> wet1 -> wet2
	///
	/// The submerged quad is split into two triangles while preserving
	/// source winding.
	/// </summary>
	private static void AccumulateTwoWet(
		Vector3 dry,
		Vector3 wet1,
		Vector3 wet2,
		float hDry,
		float hWet1,
		float hWet2,
		Vector3 centerOfMassWorld,
		float fluidDensity,
		float gravityMagnitude,
		ref Vector3 totalForce,
		ref Vector3 totalTorque,
		ref float submergedArea,
		ref int submergedTriangleCount)
	{
		if (!TryClipEdge(
				wet1,
				dry,
				hWet1,
				hDry,
				out Vector3 p1,
				out float hp1) ||
			!TryClipEdge(
				wet2,
				dry,
				hWet2,
				hDry,
				out Vector3 p2,
				out float hp2))
		{
			return;
		}


		AccumulateWetTriangle(
			wet1,
			wet2,
			p1,
			hWet1,
			hWet2,
			hp1,
			centerOfMassWorld,
			fluidDensity,
			gravityMagnitude,
			ref totalForce,
			ref totalTorque,
			ref submergedArea,
			ref submergedTriangleCount);


		AccumulateWetTriangle(
			wet2,
			p2,
			p1,
			hWet2,
			hp2,
			hp1,
			centerOfMassWorld,
			fluidDensity,
			gravityMagnitude,
			ref totalForce,
			ref totalTorque,
			ref submergedArea,
			ref submergedTriangleCount);
	}


	/// <summary>
	/// Finds the zero signed-depth point on an edge.
	///
	/// signedDepth:
	///
	///     vertex.Y - waterHeight
	///
	/// is negative below water and positive above water.
	///
	/// The returned water height is forced to point.Y because the
	/// intersection lies on the linearly approximated water surface.
	/// </summary>
	private static bool TryClipEdge(
		Vector3 wet,
		Vector3 dry,
		float hWet,
		float hDry,
		out Vector3 point,
		out float waterHeight)
	{
		float wetSignedDepth =
			wet.Y -
				hWet;

		float drySignedDepth =
			dry.Y -
				hDry;


		float denominator =
			wetSignedDepth -
				drySignedDepth;


		if (!float.IsFinite(denominator) ||
			Mathf.Abs(denominator) <=
				SurfaceEpsilon)
		{
			point =
				default;

			waterHeight =
				float.NaN;

			return false;
		}


		float t =
			Mathf.Clamp(
				wetSignedDepth /
					denominator,
				0.0f,
				1.0f);


		point =
			wet.Lerp(
				dry,
				t);


		//
		// Interpolation of hWet/hDry should produce the same value,
		// but point.Y is numerically self-consistent with the clipped
		// geometry and gives exactly zero depth at the waterline.
		//

		waterHeight =
			point.Y;


		return
			point.IsFinite();
	}


	/// <summary>
	/// Analytically integrates hydrostatic pressure over one fully wet
	/// planar triangle.
	///
	/// For vertex depths d0,d1,d2:
	///
	///     integral(depth dA)
	///         = area * (d0 + d1 + d2) / 3
	///
	/// Pressure acts opposite the outward surface normal.
	///
	/// The exact center of pressure for a linearly varying depth field is:
	///
	///                 sum(ri * (D + di))
	///     r_cp =      --------------------
	///                         4D
	///
	/// where D = d0 + d1 + d2.
	///
	/// This is equivalent to integrating pressure moments directly and
	/// avoids the low-poly residual torque produced by applying the force
	/// at the geometric triangle centroid.
	/// </summary>
	private static void AccumulateWetTriangle(
		Vector3 a,
		Vector3 b,
		Vector3 c,
		float ha,
		float hb,
		float hc,
		Vector3 centerOfMassWorld,
		float fluidDensity,
		float gravityMagnitude,
		ref Vector3 totalForce,
		ref Vector3 totalTorque,
		ref float submergedArea,
		ref int submergedTriangleCount)
	{
		float depthA =
			Mathf.Max(
				ha -
					a.Y,
				0.0f);

		float depthB =
			Mathf.Max(
				hb -
					b.Y,
				0.0f);

		float depthC =
			Mathf.Max(
				hc -
					c.Y,
				0.0f);


		float depthSum =
			depthA +
			depthB +
			depthC;


		if (!float.IsFinite(depthSum) ||
			depthSum <=
				DepthSumEpsilon)
		{
			return;
		}


		//
		// areaVector magnitude equals triangle area.
		//
		// Contract: triangle winding is outward.
		//

		Vector3 areaVector =
			0.5f *
			(b - a).Cross(
				c - a);


		float area =
			areaVector.Length();


		if (!float.IsFinite(area) ||
			area <=
				AreaEpsilon)
		{
			return;
		}


		//
		// Hydrostatic pressure force:
		//
		//     dF = -rho * g * depth * n dA
		//
		// areaVector = n * area.
		//

		float forceScale =
			-fluidDensity *
			gravityMagnitude *
			(depthSum /
			 3.0f);


		Vector3 force =
			areaVector *
				forceScale;


		if (!force.IsFinite())
		{
			return;
		}


		//
		// Exact pressure-weighted center for a linear depth field.
		//

		float inverseCenterDenominator =
			1.0f /
			(4.0f *
			 depthSum);


		Vector3 centerOfPressure =
			(
				a *
					(depthSum +
					 depthA) +

				b *
					(depthSum +
					 depthB) +

				c *
					(depthSum +
					 depthC)
			) *
			inverseCenterDenominator;


		if (!centerOfPressure.IsFinite())
		{
			return;
		}


		Vector3 torque =
			(centerOfPressure -
			 centerOfMassWorld)
			.Cross(
				force);


		if (!torque.IsFinite())
		{
			return;
		}


		totalForce +=
			force;

		totalTorque +=
			torque;

		submergedArea +=
			area;

		submergedTriangleCount++;
	}
}
