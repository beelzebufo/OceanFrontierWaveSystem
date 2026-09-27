using System;
using Godot;
using OceanFrontier.Water.Lighting;
using OceanFrontier.Water.Optics;
using OceanFrontier.Water.Runtime;

namespace OceanFrontier.Water.Rendering;

public partial class AnimatedWaveSurfaceRenderer
{
	private void ApplyDisplayParameters()
	{
		if (_material == null)
		{
			return;
		}


		// Also seeds newly created nested materials with the authoritative
		// physical medium state.
		ApplyOpticsParameters(
			force: true);


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
			"debug_underwater_transmission",
			_debugUnderwaterTransmission);


		SetSurfaceParameter(
			"diagnostic_roughness",
			_diagnosticRoughness);


		// The planar diagnostic can bind its viewport texture before nested LOD
		// materials exist. Reapply the cached state whenever display parameters
		// seed newly constructed materials.
		ApplyPlanarReflectionParameters();
	}


	private void ApplyPlanarReflectionParameters()
	{
		if (_material == null)
		{
			return;
		}


		if (_planarReflectionTexture != null)
		{
			SetSurfaceParameter(
				"planar_reflection_texture",
				_planarReflectionTexture);
		}


		SetSurfaceParameter(
			"planar_reflection_weight",
			_planarReflectionWeight);


		SetSurfaceParameter(
			"planar_reflection_distortion",
			_planarReflectionDistortion);


		SetSurfaceParameter(
			"planar_reflection_diagnostic_mode",
			_planarReflectionDiagnosticMode);


		if (_planarReflectionViewProjectionInitialized)
		{
			SetSurfaceParameter(
				"planar_reflection_view_projection",
				_planarReflectionViewProjection);
		}
	}


	private int _appliedOpticsRevision = -1;


	private void ApplyOpticsParameters(
		bool force = false)
	{
		if (_material == null ||
			_runtime == null)
		{
			return;
		}


		_runtime.GetOpticsState(
			out OceanOpticsState state,
			out int revision);


		if (!force &&
			_appliedOpticsRevision ==
				revision)
		{
			return;
		}


		SetSurfaceParameter(
			"water_ior",
			state.WaterIor);


		SetSurfaceParameter(
			"water_extinction",
			state.Extinction);


		SetSurfaceParameter(
			"water_scatter_color",
			new Color(
				state.DeepScatterColor.X,
				state.DeepScatterColor.Y,
				state.DeepScatterColor.Z));


		_appliedOpticsRevision =
			revision;
	}


	private int _appliedLightingRevision = -1;
	private int _appliedCausticsRevision = -1;


	private void ApplyLightingParameters(
		bool force = false)
	{
		if (_material == null ||
			_runtime == null)
		{
			return;
		}


		_runtime.GetLightingState(
			out OceanLightingState state,
			out int revision);


		if (!force &&
			_appliedLightingRevision ==
				revision)
		{
			return;
		}


		_primarySunRayDirectionWorld =
			state.PrimarySunRayDirectionWorld;

		_primarySunRadiance =
			state.PrimarySunLinearRadiance;


		SetSurfaceParameter(
			"primary_sun_ray_direction_world",
			_primarySunRayDirectionWorld);


		SetSurfaceParameter(
			"primary_sun_radiance",
			_primarySunRadiance);


		SetSurfaceParameter(
			"ocean_ambient_light",
			state.OceanAmbientLightProxy);


		SetSurfaceParameter(
			"water_sun_scatter_strength",
			_waterSunScatterStrength);


		_appliedLightingRevision =
			revision;
	}


	private void ApplyShallowWaterParameters()
	{
		if (_material == null)
		{
			return;
		}


		SetSurfaceParameter(
			"water_shallow_color_strength",
			_waterShallowColorStrength);


		SetSurfaceParameter(
			"has_sea_floor_depth",
			_hasSeaFloorDepth);
	}


	/// <summary>
	/// Pushes renderer-only projected caustics settings to water surface
	/// materials. The optional source is an ordinary Godot texture; the
	/// neutral fallback keeps the sampler valid while the effect is disabled.
	/// </summary>
	private void ApplyCausticsParameters(
		bool force = false)
	{
		if (_material == null ||
			_runtime == null)
		{
			return;
		}


		_runtime.GetCausticsState(
			out OceanCausticsState state,
			out int revision);


		if (!force &&
			_appliedCausticsRevision ==
				revision)
		{
			return;
		}


		bool enabled =
			state.Texture != null &&
			state.Strength > 0.0f;


		bool distortionEnabled =
			state.DistortionTexture != null &&
			state.DistortionStrength > 0.0f;


		Texture2D textureToBind =
			state.Texture ??
			_causticsFallback;


		Texture2D distortionTextureToBind =
			state.DistortionTexture ??
			_causticsFallback;


		SetSurfaceParameter(
			"water_caustics_enabled",
			enabled);


		if (textureToBind != null)
		{
			SetSurfaceParameter(
				"water_caustics_texture",
				textureToBind);
		}


		SetSurfaceParameter(
			"water_caustics_distortion_enabled",
			distortionEnabled);


		if (distortionTextureToBind != null)
		{
			SetSurfaceParameter(
				"water_caustics_distortion_texture",
				distortionTextureToBind);
		}


		SetSurfaceParameter(
			"water_caustics_strength",
			state.Strength);


		SetSurfaceParameter(
			"water_caustics_distortion_strength",
			state.DistortionStrength);


		SetSurfaceParameter(
			"water_caustics_scale",
			state.Scale);


		SetSurfaceParameter(
			"water_caustics_texture_average",
			state.TextureAverage);


		SetSurfaceParameter(
			"water_caustics_focal_depth",
			state.FocalDepth);


		SetSurfaceParameter(
			"water_caustics_depth_of_field",
			state.DepthOfField);


		SetSurfaceParameter(
			"water_caustics_distortion_scale",
			state.DistortionScale);


		_appliedCausticsRevision =
			revision;
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


	/// <summary>
	/// Pushes renderer-only foam breakup settings to production water
	/// materials. These values never enter OceanFoamSettings or the persistent
	/// scalar simulation field.
	/// </summary>
	private void ApplyFoamRenderingParameters(
		bool force = false)
	{
		if (_material == null)
		{
			return;
		}


		float scale =
			Mathf.Clamp(
				FoamScale,
				0.01f,
				50.0f);


		float feather =
			Mathf.Clamp(
				FoamFeather,
				0.001f,
				1.0f);


		bool textureChanged =
			!ReferenceEquals(
				_appliedFoamTexture,
				FoamTexture);


		if (force ||
			!_foamRenderingStateInitialized ||
			textureChanged)
		{
			SetSurfaceParameter(
				"has_foam_pattern_texture",
				FoamTexture != null);


			if (FoamTexture != null)
			{
				SetSurfaceParameter(
					"foam_pattern_texture",
					FoamTexture);
			}
		}


		if (force ||
			!_foamRenderingStateInitialized ||
			!Mathf.IsEqualApprox(
				_appliedFoamScale,
				scale))
		{
			SetSurfaceParameter(
				"foam_pattern_scale",
				scale);
		}


		if (force ||
			!_foamRenderingStateInitialized ||
			!Mathf.IsEqualApprox(
				_appliedFoamFeather,
				feather))
		{
			SetSurfaceParameter(
				"foam_pattern_feather",
				feather);
		}


		if (force ||
			!_foamRenderingStateInitialized ||
			_appliedFoamTint != FoamTint)
		{
			SetSurfaceParameter(
				"foam_tint",
				FoamTint);
		}


		_appliedFoamTexture =
			FoamTexture;

		_appliedFoamScale =
			scale;

		_appliedFoamFeather =
			feather;

		_appliedFoamTint =
			FoamTint;

		_foamRenderingStateInitialized =
			true;
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
