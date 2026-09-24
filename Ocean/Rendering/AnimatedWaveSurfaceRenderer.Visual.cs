using System;
using Godot;

namespace OceanFrontier.Water.Rendering;

public partial class AnimatedWaveSurfaceRenderer
{
	private void ApplyDisplayParameters()
	{
		if (_material == null)
		{
			return;
		}


		SetAllSamplingParameter(
			"display_mode",
			_displayMode);


		SetAllSamplingParameter(
			"horizontal_display_scale",
			_horizontalDisplayScale);


		SetAllSamplingParameter(
			"vertical_display_scale",
			_verticalDisplayScale);


		SetAllSamplingParameter(
			"wave_content_mode",
			_waveContentMode);


		SetAllSamplingParameter(
			"planet_curvature_enabled",
			PlanetCurvatureEnabled);


		SetAllSamplingParameter(
			"planet_radius",
			Mathf.Max(
				10000.0f,
				PlanetRadius));


		SetSurfaceParameter(
			"show_surface_grid",
			_showGrid);


		SetSurfaceParameter(
			"show_surface_markers",
			_showMarkers);


		SetSurfaceParameter(
			"lighting_enabled",
			_lightingEnabled);


		SetSurfaceParameter(
			"diagnostic_roughness",
			_diagnosticRoughness);
	}


	private void ApplyPrimarySunParameters()
	{
		if (_material == null)
		{
			return;
		}


		SetSurfaceParameter(
			"primary_sun_ray_direction_world",
			_primarySunRayDirectionWorld);


		SetSurfaceParameter(
			"primary_sun_radiance",
			_primarySunRadiance);


		SetSurfaceParameter(
			"water_sun_scatter_strength",
			_waterSunScatterStrength);
	}


	/// <summary>
	/// Pushes renderer-only visual micro-normal settings to the actual
	/// water materials.
	///
	/// The normal-vector diagnostic material deliberately does not receive
	/// these parameters because its job is to display the geometric AWF
	/// normal, not the fragment-only visual normal.
	/// </summary>
	private void ApplyVisualMicroNormalParameters(
		bool force = false)
	{
		if (_material == null)
		{
			return;
		}


		float scale =
			Mathf.Max(
				0.01f,
				VisualNormalScale);


		float strength =
			Mathf.Max(
				0.0f,
				VisualNormalStrength);


		bool enabled =
			VisualMicroNormalsEnabled &&
			VisualNormalMap != null;


		Texture2D textureToBind =
			VisualNormalMap ??
			_visualNormalFallback;


		bool textureChanged =
			!ReferenceEquals(
				_appliedVisualNormalMap,
				VisualNormalMap);


		if (force ||
			!_visualNormalStateInitialized ||
			_appliedVisualMicroNormalsEnabled != enabled)
		{
			SetSurfaceParameter(
				"visual_micro_normals_enabled",
				enabled);
		}


		// Keep the sampler valid even while the optional source map is null.
		// Rebind immediately when the source changes in either direction.

		if (textureToBind != null &&
			(force ||
			 !_visualNormalStateInitialized ||
			 textureChanged))
		{
			SetSurfaceParameter(
				"visual_normal_map",
				textureToBind);
		}


		if (force ||
			!_visualNormalStateInitialized ||
			!Mathf.IsEqualApprox(
				_appliedVisualNormalScale,
				scale))
		{
			SetSurfaceParameter(
				"visual_normal_scale",
				scale);
		}


		if (force ||
			!_visualNormalStateInitialized ||
			!Mathf.IsEqualApprox(
				_appliedVisualNormalStrength,
				strength))
		{
			SetSurfaceParameter(
				"visual_normal_strength",
				strength);
		}


		_appliedVisualMicroNormalsEnabled =
			enabled;

		_appliedVisualNormalMap =
			VisualNormalMap;

		_appliedVisualNormalScale =
			scale;

		_appliedVisualNormalStrength =
			strength;

		_visualNormalStateInitialized =
			true;
	}

	private void ApplyVisualTime()
	{
		if (_runtime == null)
		{
			return;
		}


		//
		// Renderer-only normal-map animation follows authoritative
		// ocean simulation time instead of Godot shader TIME.
		//
		// Therefore:
		//
		// Pause = ON
		//     -> visual normal animation freezes.
		//
		// Time scale
		//     -> affects visual normal animation consistently
		//        with FFT wave evolution.
		//

		SetSurfaceParameter(
			"visual_time",
			_runtime.SimulationTime);
	}


	private void SetSamplingParameter(
		string name,
		Variant value)
	{
		_material?.SetShaderParameter(
			name,
			value);


		_normalMaterial?.SetShaderParameter(
			name,
			value);
	}


	private void SetAllSamplingParameter(
		string name,
		Variant value)
	{
		SetSamplingParameter(
			name,
			value);


		if (_nestedMaterials == null)
		{
			return;
		}


		foreach (ShaderMaterial material in _nestedMaterials)
		{
			material.SetShaderParameter(
				name,
				value);
		}
	}


	/// <summary>
	/// Surface shader only.
	///
	/// Does not update the normal-vector debug material.
	/// </summary>
	private void SetSurfaceParameter(
		string name,
		Variant value)
	{
		_material?.SetShaderParameter(
			name,
			value);


		if (_nestedMaterials == null)
		{
			return;
		}


		foreach (ShaderMaterial material in _nestedMaterials)
		{
			material.SetShaderParameter(
				name,
				value);
		}
	}


	private static ArrayMesh CreateNormalLineMesh()
	{
		//
		// COLOR.r marks each static anchor's base (0)
		// and tip (1).
		//

		var vertices =
			new Vector3[
				NormalGridResolution *
					NormalGridResolution *
					2];


		var endpoints =
			new Color[
				vertices.Length];


		int index =
			0;


		for (int z = 0;
			 z < NormalGridResolution;
			 z++)
		{
			for (int x = 0;
				 x < NormalGridResolution;
				 x++)
			{
				var anchor =
					new Vector3(
						(float)x /
						(NormalGridResolution - 1) -
						0.5f,

						0.0f,

						(float)z /
						(NormalGridResolution - 1) -
						0.5f);


				vertices[index] =
					anchor;


				endpoints[index++] =
					new Color(
						0,
						0,
						0);


				vertices[index] =
					anchor;


				endpoints[index++] =
					new Color(
						1,
						0,
						0);
			}
		}


		var arrays =
			new Godot.Collections.Array();


		arrays.Resize(
			(int)Mesh.ArrayType.Max);


		arrays[
			(int)Mesh.ArrayType.Vertex] =
			vertices;


		arrays[
			(int)Mesh.ArrayType.Color] =
			endpoints;


		var mesh =
			new ArrayMesh();


		mesh.AddSurfaceFromArrays(
			Mesh.PrimitiveType.Lines,
			arrays);


		return mesh;
	}
}
