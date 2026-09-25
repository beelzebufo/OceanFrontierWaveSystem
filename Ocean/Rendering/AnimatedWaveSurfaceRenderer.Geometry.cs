// Portions of this file are directly ported from Crest Water 4.
// Source: wave-harmonic/crest commit db0658ff0b2e93e4a9e28cc2867509658b0ecc00
// Files: OceanBuilder.cs
// Crest is licensed under the MIT License. See THIRD_PARTY_NOTICES_CREST4.txt.

using System;
using System.Collections.Generic;
using Godot;
using OceanFrontier.Water.Waves.AnimatedWaves;

namespace OceanFrontier.Water.Rendering;

public partial class AnimatedWaveSurfaceRenderer
{
	// Crest LodDataMgr.MAX_LOD_COUNT.
	private const int CrestMaxLodCount = 15;


	// Direct port of Crest Water 4 OceanBuilder.PatchType.
	//
	// The fat/slim variants are not optional decoration: together with
	// SnapAndTransitionVertLayout they provide the topology contract which
	// lets adjacent LOD rings morph without cracks or visible catch-up.
	private enum CrestPatchType
	{
		Interior,
		Fat,
		FatX,
		FatXSlimZ,
		FatXOuter,
		FatXZ,
		FatXZOuter,
		SlimX,
		SlimXZ,
		SlimXFatZ,
		Count,
	}


	private void EnsureSinglePlane(
		int resolution,
		int lodCount,
		AnimatedWaveLodSlice slice)
	{
		int geometryResolution =
			GetGeometryResolution(
				resolution);


		if (_plane == null)
		{
			_plane =
				new PlaneMesh
				{
					Size =
						new Vector2(
							slice.WorldSize,
							slice.WorldSize),

					SubdivideWidth =
						geometryResolution -
						1,

					SubdivideDepth =
						geometryResolution -
						1,
				};


			_meshInstance.Mesh =
				_plane;


			_meshResolution =
				geometryResolution;


			_meshWorldSize =
				slice.WorldSize;


			UpdateCurvatureBounds();


			GD.Print(
				$"[Ocean] Canonical surface grid ready: " +
				$"LOD {_selectedLodIndex}/{lodCount - 1}, " +
				$"{geometryResolution}x{geometryResolution} quads " +
				$"(AWF {resolution}², geometry downsample " +
				$"x{GeometryDownSampleFactor}).");
		}


		if (_meshWorldSize !=
				slice.WorldSize ||
			_meshResolution !=
				geometryResolution)
		{
			_plane.Size =
				new Vector2(
					slice.WorldSize,
					slice.WorldSize);


			_plane.SubdivideWidth =
				geometryResolution -
				1;


			_plane.SubdivideDepth =
				geometryResolution -
				1;


			_meshWorldSize =
				slice.WorldSize;


			_meshResolution =
				geometryResolution;


			_materialLod =
				-1;


			UpdateCurvatureBounds();
		}
	}


	/// <summary>
	/// Direct Godot port of Crest Water 4 OceanBuilder.BuildOceanPatch().
	///
	/// The patch is authored in a 1x1 local square. LOD/tile world scale is
	/// applied by the Node3D hierarchy, exactly as Crest scales its patch
	/// transforms.
	/// </summary>
	private ArrayMesh BuildCrestOceanPatch(
		CrestPatchType patchType,
		int vertDensity,
		int lodCount)
	{
		vertDensity =
			Math.Max(
				1,
				vertDensity);


		float dx =
			1.0f /
			vertDensity;


		// Crest skirt widths on left, right, bottom and top.
		int skirtXMinus = 0;
		int skirtXPlus = 0;
		int skirtZMinus = 0;
		int skirtZPlus = 0;


		switch (patchType)
		{
			case CrestPatchType.Fat:
				skirtXMinus = 1;
				skirtXPlus = 1;
				skirtZMinus = 1;
				skirtZPlus = 1;
				break;

			case CrestPatchType.FatX:
			case CrestPatchType.FatXOuter:
				skirtXPlus = 1;
				break;

			case CrestPatchType.FatXZ:
			case CrestPatchType.FatXZOuter:
				skirtXPlus = 1;
				skirtZPlus = 1;
				break;

			case CrestPatchType.FatXSlimZ:
				skirtXPlus = 1;
				skirtZPlus = -1;
				break;

			case CrestPatchType.SlimX:
				skirtXPlus = -1;
				break;

			case CrestPatchType.SlimXZ:
				skirtXPlus = -1;
				skirtZPlus = -1;
				break;

			case CrestPatchType.SlimXFatZ:
				skirtXPlus = -1;
				skirtZPlus = 1;
				break;
		}


		int sideLengthVertsX =
			1 +
			vertDensity +
			skirtXMinus +
			skirtXPlus;


		int sideLengthVertsZ =
			1 +
			vertDensity +
			skirtZMinus +
			skirtZPlus;


		float startX =
			-0.5f -
			skirtXMinus *
			dx;


		float startZ =
			-0.5f -
			skirtZMinus *
			dx;


		float endX =
			0.5f +
			skirtXPlus *
			dx;


		float endZ =
			0.5f +
			skirtZPlus *
			dx;


		// Direct Crest relation:
		// extentsMultiplier = _extentsSizeMultiplier *
		//     (MAX_LOD_COUNT + 1 - CurrentLodCount)
		float extentsMultiplier =
			ExtentsSizeMultiplier *
			(CrestMaxLodCount +
			 1 -
			 lodCount);


		var vertices =
			new List<Vector3>(
				sideLengthVertsX *
				sideLengthVertsZ);


		for (int j = 0;
			 j < sideLengthVertsZ;
			 j++)
		{
			float z =
				Mathf.Lerp(
					startZ,
					endZ,
					(float)j /
					(sideLengthVertsZ - 1));


			if (patchType ==
					CrestPatchType.FatXZOuter &&
				j ==
					sideLengthVertsZ - 1)
			{
				z *=
					extentsMultiplier;
			}


			for (int i = 0;
				 i < sideLengthVertsX;
				 i++)
			{
				float x =
					Mathf.Lerp(
						startX,
						endX,
						(float)i /
						(sideLengthVertsX - 1));


				if (i ==
						sideLengthVertsX - 1 &&
					(patchType ==
						 CrestPatchType.FatXOuter ||
					 patchType ==
						 CrestPatchType.FatXZOuter))
				{
					x *=
						extentsMultiplier;
				}


				vertices.Add(
					new Vector3(
						x,
						0.0f,
						z));
			}
		}


		int sideLengthSquaresX =
			sideLengthVertsX -
			1;


		int sideLengthSquaresZ =
			sideLengthVertsZ -
			1;


		var indices =
			new List<int>(
				sideLengthSquaresX *
				sideLengthSquaresZ *
				6);


		// Direct Crest checkerboard diagonal layout. Reverse each triangle's
		// winding for Godot so the ocean top surface is front-facing from +Y.
		for (int j = 0;
			 j < sideLengthSquaresZ;
			 j++)
		{
			for (int i = 0;
				 i < sideLengthSquaresX;
				 i++)
			{
				bool flipEdge =
					false;


				if ((i & 1) != 0)
				{
					flipEdge =
						!flipEdge;
				}


				if ((j & 1) != 0)
				{
					flipEdge =
						!flipEdge;
				}


				int i0 =
					i +
					j *
					(sideLengthSquaresX + 1);

				int i1 =
					i0 +
					1;

				int i2 =
					i0 +
					(sideLengthSquaresX + 1);

				int i3 =
					i2 +
					1;


				if (!flipEdge)
				{
					indices.Add(i3);
					indices.Add(i0);
					indices.Add(i1);

					indices.Add(i0);
					indices.Add(i3);
					indices.Add(i2);
				}
				else
				{
					indices.Add(i3);
					indices.Add(i2);
					indices.Add(i1);

					indices.Add(i0);
					indices.Add(i1);
					indices.Add(i2);
				}
			}
		}


		Vector3[] vertexArray =
			vertices.ToArray();


		var normals =
			new Vector3[
				vertexArray.Length];


		Array.Fill(
			normals,
			Vector3.Up);


		var arrays =
			new Godot.Collections.Array();


		arrays.Resize(
			(int)Mesh.ArrayType.Max);


		arrays[
			(int)Mesh.ArrayType.Vertex] =
			vertexArray;


		arrays[
			(int)Mesh.ArrayType.Normal] =
			normals;


		arrays[
			(int)Mesh.ArrayType.Index] =
			indices.ToArray();


		var mesh =
			new ArrayMesh
			{
				ResourceName =
					patchType.ToString(),
			};


		mesh.AddSurfaceFromArrays(
			Mesh.PrimitiveType.Triangles,
			arrays);


		return mesh;
	}


	/// <summary>
	/// Direct Godot port of Crest Water 4 OceanBuilder.CreateLOD().
	///
	/// LOD0 contains the central 4 patches (16 total).
	/// Every coarser LOD is a 12-patch ring.
	/// Patch types and rotations match Crest's fat/slim topology contract.
	/// </summary>
	private void CreateCrestLodTiles(
		int lodIndex,
		int lodCount,
		Node3D lodRoot,
		ShaderMaterial material)
	{
		bool isBiggestLod =
			lodIndex ==
			lodCount -
			1;


		bool generateSkirt =
			isBiggestLod;


		CrestPatchType leadSideType =
			generateSkirt
				? CrestPatchType.FatXOuter
				: CrestPatchType.SlimX;


		CrestPatchType trailSideType =
			generateSkirt
				? CrestPatchType.FatXOuter
				: CrestPatchType.FatX;


		CrestPatchType leadCornerType =
			generateSkirt
				? CrestPatchType.FatXZOuter
				: CrestPatchType.SlimXZ;


		CrestPatchType trailCornerType =
			generateSkirt
				? CrestPatchType.FatXZOuter
				: CrestPatchType.FatXZ;


		CrestPatchType topLeftCornerType =
			generateSkirt
				? CrestPatchType.FatXZOuter
				: CrestPatchType.SlimXFatZ;


		CrestPatchType bottomRightCornerType =
			generateSkirt
				? CrestPatchType.FatXZOuter
				: CrestPatchType.FatXSlimZ;


		Vector2[] offsets;
		CrestPatchType[] patchTypes;


		if (lodIndex != 0)
		{
			// Crest ring layout:
			//
			//    0  1  2  3
			//    4        5
			//    6        7
			//    8  9 10 11
			offsets =
				new[]
				{
					new Vector2(-1.5f,  1.5f),
					new Vector2(-0.5f,  1.5f),
					new Vector2( 0.5f,  1.5f),
					new Vector2( 1.5f,  1.5f),
					new Vector2(-1.5f,  0.5f),
					new Vector2( 1.5f,  0.5f),
					new Vector2(-1.5f, -0.5f),
					new Vector2( 1.5f, -0.5f),
					new Vector2(-1.5f, -1.5f),
					new Vector2(-0.5f, -1.5f),
					new Vector2( 0.5f, -1.5f),
					new Vector2( 1.5f, -1.5f),
				};


			patchTypes =
				new[]
				{
					topLeftCornerType,
					leadSideType,
					leadSideType,
					leadCornerType,
					trailSideType,
					leadSideType,
					trailSideType,
					leadSideType,
					trailCornerType,
					trailSideType,
					trailSideType,
					bottomRightCornerType,
				};
		}
		else
		{
			// Crest LOD0 layout:
			//
			//     0  1  2  3
			//     4  5  6  7
			//     8  9 10 11
			//    12 13 14 15
			offsets =
				new[]
				{
					new Vector2(-1.5f,  1.5f),
					new Vector2(-0.5f,  1.5f),
					new Vector2( 0.5f,  1.5f),
					new Vector2( 1.5f,  1.5f),
					new Vector2(-1.5f,  0.5f),
					new Vector2(-0.5f,  0.5f),
					new Vector2( 0.5f,  0.5f),
					new Vector2( 1.5f,  0.5f),
					new Vector2(-1.5f, -0.5f),
					new Vector2(-0.5f, -0.5f),
					new Vector2( 0.5f, -0.5f),
					new Vector2( 1.5f, -0.5f),
					new Vector2(-1.5f, -1.5f),
					new Vector2(-0.5f, -1.5f),
					new Vector2( 0.5f, -1.5f),
					new Vector2( 1.5f, -1.5f),
				};


			patchTypes =
				new[]
				{
					topLeftCornerType,
					leadSideType,
					leadSideType,
					leadCornerType,
					trailSideType,
					CrestPatchType.Interior,
					CrestPatchType.Interior,
					leadSideType,
					trailSideType,
					CrestPatchType.Interior,
					CrestPatchType.Interior,
					leadSideType,
					trailCornerType,
					trailSideType,
					trailSideType,
					bottomRightCornerType,
				};
		}


		for (int index = 0;
			 index < offsets.Length;
			 index++)
		{
			Vector2 position =
				offsets[index];


			CrestPatchType patchType =
				patchTypes[index];


			var tile =
				new MeshInstance3D
				{
					Name =
						$"Tile_L{lodIndex}_{patchType}_{index}",

					Mesh =
						_nestedPatchMeshes[
							(int)patchType],

					MaterialOverride =
						material,

					Position =
						new Vector3(
							position.X,
							0.0f,
							position.Y),

					ExtraCullMargin =
						128.0f,

					CastShadow =
						GeometryInstance3D
							.ShadowCastingSetting
							.Off,
				};


			ApplyCrestPatchRotation(
				tile,
				patchType,
				position);


			lodRoot.AddChild(
				tile);


			_nestedCurvatureTiles.Add(
				(tile, lodIndex));


			_nestedTileCount++;
		}
	}


	/// <summary>
	/// Godot equivalent of the rotation block in Crest OceanBuilder.CreateLOD().
	/// </summary>
	private static void ApplyCrestPatchRotation(
		MeshInstance3D tile,
		CrestPatchType patchType,
		Vector2 position)
	{
		bool rotateXOutwards =
			patchType == CrestPatchType.FatX ||
			patchType == CrestPatchType.FatXOuter ||
			patchType == CrestPatchType.SlimX ||
			patchType == CrestPatchType.SlimXFatZ;


		if (rotateXOutwards)
		{
			float rotationY;


			if (MathF.Abs(position.Y) >=
				MathF.Abs(position.X))
			{
				rotationY =
					-MathF.PI *
					0.5f *
					MathF.Sign(
						position.Y);
			}
			else
			{
				rotationY =
					position.X < 0.0f
						? MathF.PI
						: 0.0f;
			}


			tile.Rotation =
				new Vector3(
					0.0f,
					rotationY,
					0.0f);


			return;
		}


		bool rotateXzOutwards =
			patchType == CrestPatchType.FatXZ ||
			patchType == CrestPatchType.SlimXZ ||
			patchType == CrestPatchType.FatXSlimZ ||
			patchType == CrestPatchType.FatXZOuter;


		if (!rotateXzOutwards)
		{
			return;
		}


		// Crest's unrotated corner points toward local (+X,+Z).
		// Convert that FromToRotation to a pure Y rotation for the
		// axis-aligned ocean plane.
		float sourceAngle =
			MathF.PI *
			0.25f;


		float targetAngle =
			MathF.Atan2(
				position.Y,
				position.X);


		float rotation =
			sourceAngle -
			targetAngle;


		if (rotation > MathF.PI)
		{
			rotation -=
				MathF.PI *
				2.0f;
		}
		else if (rotation < -MathF.PI)
		{
			rotation +=
				MathF.PI *
				2.0f;
		}


		tile.Rotation =
			new Vector3(
				0.0f,
				rotation,
				0.0f);
	}


	private void EnsureNestedResources(
		int resolution,
		int lodCount)
	{
		int geometryResolution =
			GetGeometryResolution(
				resolution);


		if (_nestedPatchMeshes != null &&
			_nestedResolution ==
				resolution &&
			_nestedGeometryResolution ==
				geometryResolution &&
			_nestedLodCount ==
				lodCount)
		{
			return;
		}


		foreach (Node child in
				 _nestedRoot.GetChildren())
		{
			_nestedRoot.RemoveChild(
				child);


			child.QueueFree();
		}


		int tileResolution =
			GetCrestTileResolution(
				resolution);


		// Direct Crest GenerateMesh(): one mesh per PatchType.
		_nestedPatchMeshes =
			new Mesh[
				(int)CrestPatchType.Count];


		for (int patchType = 0;
			 patchType <
				 (int)CrestPatchType.Count;
			 patchType++)
		{
			_nestedPatchMeshes[patchType] =
				BuildCrestOceanPatch(
					(CrestPatchType)patchType,
					tileResolution,
					lodCount);
		}


		_nestedMaterials =
			new ShaderMaterial[
				lodCount];


		_nestedLodRoots =
			new Node3D[
				lodCount];


		_nestedCenters =
			new Vector2[
				lodCount];


		_nestedNextCenters =
			new Vector2[
				lodCount];


		_nestedWorldSizes =
			new float[
				lodCount];


		_nestedTileCount =
			0;


		_nestedCurvatureTiles.Clear();


		_nestedFocus =
			new Vector2(
				float.NaN,
				float.NaN);


		for (int lod = 0;
			 lod < lodCount;
			 lod++)
		{
			var material =
				new ShaderMaterial
				{
					Shader =
						_surfaceShader,
				};


			material.SetShaderParameter(
				"animated_wave_field",
				_animatedWaveTexture);


			material.SetShaderParameter(
				"animated_wave_derivative_field",
				_derivativeTexture);


			material.SetShaderParameter(
				"sea_floor_depth_field",
				_seaFloorDepthTexture);


			_nestedMaterials[lod] =
				material;


			var lodRoot =
				new Node3D
				{
					Name =
						$"LOD{lod}",
				};


			_nestedRoot.AddChild(
				lodRoot);


			_nestedLodRoots[lod] =
				lodRoot;


			_nestedCenters[lod] =
				new Vector2(
					float.NaN,
					float.NaN);


			_nestedNextCenters[lod] =
				new Vector2(
					float.NaN,
					float.NaN);


			_nestedWorldSizes[lod] =
				float.NaN;


			CreateCrestLodTiles(
				lod,
				lodCount,
				lodRoot,
				material);
		}


		_nestedResolution =
			resolution;


		_nestedGeometryResolution =
			geometryResolution;


		_nestedLodCount =
			lodCount;


		ApplyDisplayParameters();


		// New nested materials must receive the renderer-owned primary-sun
		// state even when the light itself has not changed.
		ApplyPrimarySunParameters(
			force: true);


		ApplyShallowWaterParameters();


		// New nested materials share the same renderer-owned caustics texture
		// and must receive the complete cached optical state.
		ApplyCausticsParameters(
			force: true);


		SetAllSamplingParameter(
			"normal_method",
			NormalMethod);


		SetAllSamplingParameter(
			"lod_scale_alpha",
			float.IsFinite(
				_lodScaleAlpha)
					? _lodScaleAlpha
					: 0.0f);


		// Direct Crest OceanRenderer.Rebuild() constants.
		float baseMeshDensity =
			resolution *
			0.25f /
			GetGeometryDownSampleFactor();


		float lodAlphaBlackPointFade =
			0.4f /
			(baseMeshDensity /
			 8.0f);


		float lodAlphaBlackPointWhitePointFade =
			1.0f -
			lodAlphaBlackPointFade -
			lodAlphaBlackPointFade;


		SetAllSamplingParameter(
			"lod_alpha_black_point_fade",
			lodAlphaBlackPointFade);


		SetAllSamplingParameter(
			"lod_alpha_black_point_white_point_fade",
			lodAlphaBlackPointWhitePointFade);


		ApplyVisualMicroNormalParameters(
			force: true);


		ApplyVisualTime();


		GD.Print(
			$"[Ocean] Crest4 nested surface ready: " +
			$"{lodCount} LODs, {_nestedTileCount} tiles, " +
			$"AWF {resolution}², geometry {geometryResolution}², " +
			$"tile density {tileResolution}, " +
			$"{_nestedPatchMeshes.Length} Crest patch types, " +
			$"LOD alpha black={lodAlphaBlackPointFade:0.######}, " +
			$"range={lodAlphaBlackPointWhitePointFade:0.######}.");
	}


	private void UpdateCurvatureBounds()
	{
		UpdateSingleCurvatureBounds();


		if (_nestedCurvatureTiles.Count ==
			0)
		{
			return;
		}


		if (!PlanetCurvatureEnabled ||
			_nestedWorldSizes == null ||
			_nestedWorldSizes.Length ==
				0)
		{
			foreach ((MeshInstance3D Tile, int Lod) entry in
					 _nestedCurvatureTiles)
			{
				entry.Tile.CustomAabb =
					default;
			}


			return;
		}


		foreach ((MeshInstance3D Tile, int Lod) entry in
				 _nestedCurvatureTiles)
		{
			if (entry.Lod < 0 ||
				entry.Lod >= _nestedWorldSizes.Length)
			{
				continue;
			}


			float worldSize =
				_nestedWorldSizes[entry.Lod];


			if (!float.IsFinite(worldSize) ||
				worldSize <= 0.0f)
			{
				continue;
			}


			float tileWorldSize =
				worldSize /
				TilesPerSide;


			MeshInstance3D tile =
				entry.Tile;


			Aabb meshBounds =
				tile.Mesh.GetAabb();


			float maximumDistance =
				CalculateTileMaximumDistance(
					tile,
					meshBounds,
					tileWorldSize);


			float maximumSag =
				CalculatePlanetSag(
					maximumDistance);


			const float displacementMargin =
				128.0f;


			tile.CustomAabb =
				new Aabb(
					new Vector3(
						meshBounds.Position.X,
						-maximumSag -
							displacementMargin,
						meshBounds.Position.Z),

					new Vector3(
						meshBounds.Size.X,
						maximumSag +
							displacementMargin *
							2.0f,
						meshBounds.Size.Z));
		}
	}


	private void UpdateSingleCurvatureBounds()
	{
		if (_meshInstance == null ||
			_plane == null ||
			!PlanetCurvatureEnabled)
		{
			if (_meshInstance != null)
			{
				_meshInstance.CustomAabb =
					default;
			}


			return;
		}


		Aabb meshBounds =
			_plane.GetAabb();


		float maximumDistance =
			_meshWorldSize *
			0.5f *
			Mathf.Sqrt(2.0f);


		float maximumSag =
			CalculatePlanetSag(
				maximumDistance);


		const float displacementMargin =
			128.0f;


		_meshInstance.CustomAabb =
			new Aabb(
				new Vector3(
					meshBounds.Position.X,
					-maximumSag -
						displacementMargin,
					meshBounds.Position.Z),

				new Vector3(
					meshBounds.Size.X,
					maximumSag +
						displacementMargin *
						2.0f,
					meshBounds.Size.Z));
	}


	private static float CalculateTileMaximumDistance(
		MeshInstance3D tile,
		Aabb meshBounds,
		float tileWorldSize)
	{
		float minX =
			meshBounds.Position.X;

		float maxX =
			meshBounds.End.X;

		float minZ =
			meshBounds.Position.Z;

		float maxZ =
			meshBounds.End.Z;

		float maximumDistanceSquared =
			0.0f;


		for (int z = 0;
			 z < 2;
			 z++)
		{
			for (int x = 0;
				 x < 2;
				 x++)
			{
				Vector3 rotated =
					tile.Basis *
					new Vector3(
						x == 0
							? minX
							: maxX,
						0.0f,
						z == 0
							? minZ
							: maxZ);


				var offset =
					new Vector2(
						tile.Position.X +
							rotated.X,
						tile.Position.Z +
							rotated.Z) *
					tileWorldSize;


				maximumDistanceSquared =
					Mathf.Max(
						maximumDistanceSquared,
						offset.LengthSquared());
			}
		}


		return Mathf.Sqrt(
			maximumDistanceSquared);
	}


	private float CalculatePlanetSag(
		float distance)
	{
		double radius =
			Math.Max(
				10000.0,
				PlanetRadius);

		double radiusSquared =
			radius *
			radius;

		double distanceSquared =
			Math.Min(
				(double)distance *
					distance,
				radiusSquared *
					0.999999);


		return (float)(
			distanceSquared /
			(
				radius +
				Math.Sqrt(
					Math.Max(
						radiusSquared -
							distanceSquared,
						0.0))
			));
	}


	private int GetGeometryDownSampleFactor()
	{
		int factor =
			GeometryDownSampleFactor;


		if (factor != 1 &&
			factor != 2 &&
			factor != 4 &&
			factor != 8)
		{
			factor =
				2;
		}


		return factor;
	}


	/// <summary>
	/// Direct Crest GenerateMesh tile density:
	///
	///     round(0.25 * LodDataResolution / GeometryDownSampleFactor)
	/// </summary>
	private int GetCrestTileResolution(
		int animatedWaveResolution)
	{
		return Math.Max(
			1,
			(int)MathF.Round(
				0.25f *
				animatedWaveResolution /
				GetGeometryDownSampleFactor()));
	}


	private int GetGeometryResolution(
		int animatedWaveResolution)
	{
		return TilesPerSide *
			GetCrestTileResolution(
				animatedWaveResolution);
	}
}
