using Godot;
using OceanFrontier.Water.Waves.AnimatedWaves;

namespace OceanFrontier.Water.Rendering;

public partial class AnimatedWaveSurfaceRenderer
{
	public override void _Process(
		double delta)
	{
		//
		// Copy ONE coherent committed AnimatedWaveField spatial state.
		//
		// Crest contract being preserved:
		// one RenderData.Current / CascadeParams generation is consumed by
		// every ocean chunk for this renderer frame.
		//
		// No consumer-side CalculateSlice(), pending focus, or pending LOD
		// scale is read below.
		//

		if (_runtime == null ||
			_renderStateSlices == null ||
			!_runtime.TryCopyAnimatedWaveSurfaceState(
				_renderStateSlices,
				out Rid textureRid,
				out int resolution,
				out int lodCount,
				out Vector2 focusXZ,
				out _,
				out float lodScaleAlpha,
				out _))
		{
			HideSurfaceRoots();
			return;
		}


		if (lodCount <= 0 ||
			lodCount >
				_renderStateSlices.Length)
		{
			HideSurfaceRoots();
			return;
		}


		BindAwfTexture(
			textureRid);


		//
		// Renderer-only visual state.
		//

		ApplyVisualMicroNormalParameters();
		ApplyVisualTime();


		//
		// The altitude transition alpha belongs to the SAME committed AWF
		// generation as focusXZ and all slices above.
		//

		if (!float.IsFinite(
				_lodScaleAlpha) ||
			!Mathf.IsEqualApprox(
				_lodScaleAlpha,
				lodScaleAlpha))
		{
			_lodScaleAlpha =
				lodScaleAlpha;


			SetAllSamplingParameter(
				"lod_scale_alpha",
				lodScaleAlpha);
		}


		if (_materialNormalMethod !=
			NormalMethod)
		{
			SetAllSamplingParameter(
				"normal_method",
				NormalMethod);


			_materialNormalMethod =
				NormalMethod;
		}


		if (LayoutMode ==
			SurfaceLayoutMode.NestedLod)
		{
			EnsureNestedResources(
				resolution,
				lodCount);


			UpdateNestedMaterials(
				lodCount,
				focusXZ,
				_renderStateSlices);


			_singleRoot.Visible =
				false;


			if (_normalMeshInstance != null)
			{
				_normalMeshInstance.Visible =
					false;
			}


			_nestedRoot.Visible =
				true;


			return;
		}


		_nestedRoot.Visible =
			false;


		//
		// Single-LOD mode reads the SAME snapshot already copied above.
		//

		if (_selectedLodIndex < 0 ||
			_selectedLodIndex >=
				lodCount)
		{
			_singleRoot.Visible =
				false;

			return;
		}


		AnimatedWaveLodSlice slice =
			_renderStateSlices[
				_selectedLodIndex];


		bool hasNext =
			_selectedLodIndex + 1 <
			lodCount;


		AnimatedWaveLodSlice nextSlice =
			hasNext
				? _renderStateSlices[
					_selectedLodIndex + 1]
				: slice;


		EnsureSinglePlane(
			resolution,
			lodCount,
			slice);


		ApplySurfaceSamplingParameters(
			resolution,
			lodCount,
			slice,
			nextSlice,
			focusXZ);


		_meshInstance.Visible =
			true;


		if (_normalMeshInstance != null)
		{
			_normalMeshInstance.Visible =
				_showNormalVectors;
		}


		_singleRoot.Visible =
			true;
	}


	private void HideSurfaceRoots()
	{
		if (_singleRoot != null)
		{
			_singleRoot.Visible =
				false;
		}


		if (_nestedRoot != null)
		{
			_nestedRoot.Visible =
				false;
		}
	}


	private void BindAwfTexture(
		Rid textureRid)
	{
		if (!_boundRid.IsValid ||
			_boundRid.Id !=
			textureRid.Id)
		{
			_textureArray.TextureRdRid =
				textureRid;


			_boundRid =
				textureRid;
		}
	}


	private void UpdateNestedMaterials(
		int lodCount,
		Vector2 focusXZ,
		AnimatedWaveLodSlice[] slices)
	{
		if (slices == null ||
			lodCount <= 0 ||
			slices.Length <
				lodCount)
		{
			return;
		}


		int geometryResolution =
			_nestedGeometryResolution >
			0
				? _nestedGeometryResolution
				: GetGeometryResolution(
					_nestedResolution);


		// Crest OceanRenderer.Root follows the viewpoint continuously.
		// Vertex snapping is NOT done by moving this root to a snapped
		// position. SnapAndTransitionVertLayout() performs the exact
		// per-patch grid snap in the vertex shader using MODEL_MATRIX.
		if (_nestedFocus !=
			focusXZ)
		{
			_nestedFocus =
				focusXZ;


			_nestedRoot.Position =
				new Vector3(
					focusXZ.X,
					0.0f,
					focusXZ.Y);


			foreach (ShaderMaterial material in
					 _nestedMaterials)
			{
				material.SetShaderParameter(
					"lod_focus_xz",
					focusXZ);
			}
		}


		for (int lod = 0;
			 lod < lodCount;
			 lod++)
		{
			AnimatedWaveLodSlice slice =
				slices[lod];


			bool hasNext =
				lod + 1 <
				lodCount;


			AnimatedWaveLodSlice nextSlice =
				hasNext
					? slices[
						lod + 1]
					: slice;


			ShaderMaterial material =
				_nestedMaterials[
					lod];


			if (_nestedWorldSizes[lod] !=
				slice.WorldSize)
			{
				// Crest relation:
				//
				//     LOD texture diameter = 4 * cascade scale
				//     one patch width      = cascade scale
				//
				// Our patch meshes are authored as 1x1 local units.
				float tileWorldSize =
					slice.WorldSize /
					TilesPerSide;


				_nestedLodRoots[lod].Scale =
					new Vector3(
						tileWorldSize,
						1.0f,
						tileWorldSize);


				// Exact Crest PerCascadeInstanceData._geoGridWidth:
				//
				//     cascadeScale / tileResolution
				//
				// which is equivalently:
				//
				//     slice.WorldSize / fullGeometryResolution
				float geometryGridWidth =
					slice.WorldSize /
					geometryResolution;


				material.SetShaderParameter(
					"geometry_grid_width",
					geometryGridWidth);


				material.SetShaderParameter(
					"selected_lod",
					(float)lod);


				material.SetShaderParameter(
					"lod_world_size",
					slice.WorldSize);


				material.SetShaderParameter(
					"current_lod_texel_width",
					slice.TexelWidth);


				material.SetShaderParameter(
					"has_next_lod",
					hasNext);


				material.SetShaderParameter(
					"next_lod",
					(float)(
						hasNext
							? lod + 1
							: lod));


				material.SetShaderParameter(
					"next_lod_world_size",
					hasNext
						? nextSlice.WorldSize
						: slice.WorldSize);


				material.SetShaderParameter(
					"next_lod_texel_width",
					hasNext
						? nextSlice.TexelWidth
						: slice.TexelWidth);


				_nestedWorldSizes[lod] =
					slice.WorldSize;
			}


			if (_nestedCenters[lod] !=
				slice.CenterXZ)
			{
				_nestedCenters[lod] =
					slice.CenterXZ;


				material.SetShaderParameter(
					"lod_center_xz",
					slice.CenterXZ);
			}


			Vector2 nextCenter =
				hasNext
					? nextSlice.CenterXZ
					: slice.CenterXZ;


			if (_nestedNextCenters[lod] !=
				nextCenter)
			{
				_nestedNextCenters[lod] =
					nextCenter;


				material.SetShaderParameter(
					"next_lod_center_xz",
					nextCenter);
			}
		}
	}


	/// <summary>
	/// Single surface and its normal overlay share selected-slice metadata.
	/// </summary>
	private void ApplySurfaceSamplingParameters(
		int resolution,
		int lodCount,
		AnimatedWaveLodSlice slice,
		AnimatedWaveLodSlice nextSlice,
		Vector2 focusXZ)
	{
		int geometryResolution =
			GetGeometryResolution(
				resolution);


		if (_materialLod !=
				_selectedLodIndex ||
			_materialLodCount !=
				lodCount)
		{
			SetSamplingParameter(
				"lod_world_size",
				slice.WorldSize);


			float geometryGridWidth =
				slice.WorldSize /
				geometryResolution;


			SetSamplingParameter(
				"geometry_grid_width",
				geometryGridWidth);


			SetSamplingParameter(
				"current_lod_texel_width",
				slice.TexelWidth);


			SetSamplingParameter(
				"selected_lod",
				(float)_selectedLodIndex);


			bool hasNext =
				_selectedLodIndex +
					1 <
				lodCount;


			SetSamplingParameter(
				"has_next_lod",
				hasNext);


			SetSamplingParameter(
				"next_lod",
				(float)(
					hasNext
						? _selectedLodIndex + 1
						: _selectedLodIndex));


			SetSamplingParameter(
				"next_lod_world_size",
				hasNext
					? nextSlice.WorldSize
					: slice.WorldSize);


			SetSamplingParameter(
				"next_lod_texel_width",
				hasNext
					? nextSlice.TexelWidth
					: slice.TexelWidth);


			_normalMaterial?.SetShaderParameter(
				"diagnostic_normal_length",
				slice.WorldSize /
					(NormalGridResolution - 1) *
					NormalVectorScale);


			if (_normalMeshInstance != null)
			{
				_normalMeshInstance.Scale =
					new Vector3(
						slice.WorldSize,
						1.0f,
						slice.WorldSize);
			}


			_materialLod =
				_selectedLodIndex;


			_materialLodCount =
				lodCount;
		}


		if (_lastCenter !=
			slice.CenterXZ)
		{
			_lastCenter =
				slice.CenterXZ;


			SetSamplingParameter(
				"lod_center_xz",
				slice.CenterXZ);
		}


		Vector2 nextCenter =
			_selectedLodIndex +
				1 <
			lodCount
				? nextSlice.CenterXZ
				: slice.CenterXZ;


		if (_lastNextCenter !=
			nextCenter)
		{
			_lastNextCenter =
				nextCenter;


			SetSamplingParameter(
				"next_lod_center_xz",
				nextCenter);
		}


		if (_lastFocus !=
			focusXZ)
		{
			_lastFocus =
				focusXZ;


			// Crest ocean root follows the viewpoint continuously.
			_singleRoot.Position =
				new Vector3(
					focusXZ.X,
					0.0f,
					focusXZ.Y);


			SetSamplingParameter(
				"lod_focus_xz",
				focusXZ);
		}
	}
}
