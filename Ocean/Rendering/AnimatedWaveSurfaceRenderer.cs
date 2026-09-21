using Godot;
using OceanFrontier.Water.Runtime;
using OceanFrontier.Water.Waves.AnimatedWaves;

namespace OceanFrontier.Water.Rendering;

/// <summary>
/// Single-LOD validation grid or static nested tiles sampling canonical spatial LODs.
/// The AnimatedWaveField owns the RenderingDevice texture RID.
/// Visual micro normals are renderer-only and never modify the canonical field.
/// </summary>
public partial class AnimatedWaveSurfaceRenderer : Node3D
{
	public enum SurfaceLayoutMode
	{
		SingleLod,
		NestedLod,
	}

	private const int TilesPerSide = 4;
	private const int NormalGridResolution = 17;
	private const float NormalVectorScale = 0.35f;

	private const string ShaderPath =
		"res://Ocean/Shaders/Rendering/animated_wave_surface.gdshader";

	private const string NormalShaderPath =
		"res://Ocean/Shaders/Rendering/animated_wave_normal_debug.gdshader";


	[Export(PropertyHint.Enum, "1,2,4,8")]
	public int GeometryDownSampleFactor { get; set; } = 2;


	[Export(PropertyHint.Range, "0,15,1")]
	public int SpatialLodIndex { get; set; } = 4;


	[Export(PropertyHint.Enum, "Blended XYZ Forward,Crest Per LOD Forward")]
	public int NormalMethod { get; set; } = 1;


	[Export]
	public SurfaceLayoutMode LayoutMode { get; set; } =
		SurfaceLayoutMode.SingleLod;


	//
	// Renderer-only visual micro normal layer.
	//
	// This texture is NOT part of AnimatedWaveField and is NOT visible to
	// physics, foam simulation, queries, FFT or AnimatedWaveComposer.
	//

	[Export]
	public bool VisualMicroNormalsEnabled { get; set; } = true;


	[Export]
	public Texture2D VisualNormalMap { get; set; }


	//
	// Matches Crest's normal-map scale convention.
	//
	// Crest default:
	//     _NormalsScale = 40
	//

	[Export(PropertyHint.Range, "0.01,200,0.01")]
	public float VisualNormalScale { get; set; } = 40.0f;


	//
	// Crest default:
	//     _NormalsStrength = 0.36
	//

	[Export(PropertyHint.Range, "0,2,0.01")]
	public float VisualNormalStrength { get; set; } = 0.36f;


	private OceanRuntime _runtime;

	private Shader _surfaceShader;

	private Node3D _singleRoot;
	private Node3D _nestedRoot;

	private MeshInstance3D _meshInstance;
	private MeshInstance3D _normalMeshInstance;

	private PlaneMesh _plane;

	private ShaderMaterial _material;
	private ShaderMaterial _normalMaterial;

	private ShaderMaterial[] _nestedMaterials;
	private Node3D[] _nestedLodRoots;

	//
	// Regular, +Z edge, and adjacent +Z/+X edges.
	// Tiles rotate the latter two variants.
	//

	private Mesh[] _nestedPatchMeshes;

	private Vector2[] _nestedCenters;
	private Vector2[] _nestedNextCenters;
	private float[] _nestedWorldSizes;

	private Vector2 _nestedFocus =
		new(float.NaN, float.NaN);

	private int _nestedResolution;
	private int _nestedGeometryResolution;
	private int _nestedLodCount;
	private int _nestedTileCount;


	private Texture2DArrayRD _textureArray;
	private Rid _boundRid;


	private Vector2 _lastCenter =
		new(float.NaN, float.NaN);

	private Vector2 _lastNextCenter =
		new(float.NaN, float.NaN);

	private Vector2 _lastFocus =
		new(float.NaN, float.NaN);


	private int _selectedLodIndex;

	private int _waveContentMode;
	private int _displayMode;

	private float _horizontalDisplayScale = 1.0f;
	private float _verticalDisplayScale = 1.0f;

	private bool _showGrid = true;
	private bool _showMarkers = true;
	private bool _lightingEnabled = true;
	private bool _showNormalVectors;

	private float _diagnosticRoughness = 0.65f;


	private int _meshResolution;
	private float _meshWorldSize;


	private int _materialLod = -1;
	private int _materialLodCount = -1;
	private int _materialNormalMethod = -1;


	private float _lodScaleAlpha =
		float.NaN;


	//
	// Cached renderer-only micro-normal state.
	//
	// ShaderMaterial updates are only issued when a setting actually changes,
	// except when new nested materials have just been constructed.
	//

	private bool _visualNormalStateInitialized;

	private bool _appliedVisualMicroNormalsEnabled;

	private Texture2D _appliedVisualNormalMap;

	private float _appliedVisualNormalScale =
		float.NaN;

	private float _appliedVisualNormalStrength =
		float.NaN;


	internal int SelectedLodIndex =>
		_selectedLodIndex;


	internal int NestedTileCount =>
		_nestedTileCount;


	internal void SetSpatialLod(
		int lod)
	{
		_selectedLodIndex =
			lod;
	}


	internal void SetSurfaceLayout(
		SurfaceLayoutMode mode)
	{
		LayoutMode =
			mode;


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


		if (_normalMeshInstance != null)
		{
			_normalMeshInstance.Visible =
				mode ==
				SurfaceLayoutMode.SingleLod &&
				_showNormalVectors &&
				_meshInstance.Visible;
		}
	}


	internal void SetWaveContent(
		int mode)
	{
		_waveContentMode =
			mode;


		ApplyDisplayParameters();
	}


	internal void SetLightingEnabled(
		bool enabled)
	{
		_lightingEnabled =
			enabled;


		SetSurfaceParameter(
			"lighting_enabled",
			enabled);
	}


	internal void SetDiagnosticRoughness(
		float roughness)
	{
		_diagnosticRoughness =
			Mathf.Clamp(
				roughness,
				0.0f,
				1.0f);


		SetSurfaceParameter(
			"diagnostic_roughness",
			_diagnosticRoughness);
	}


	internal void SetNormalVectorsVisible(
		bool visible)
	{
		_showNormalVectors =
			visible;


		if (_normalMeshInstance != null)
		{
			_normalMeshInstance.Visible =
				visible &&
				LayoutMode ==
				SurfaceLayoutMode.SingleLod &&
				_meshInstance.Visible;
		}
	}


	internal void SetDisplay(
		int mode,
		float horizontalScale,
		float verticalScale)
	{
		_displayMode =
			mode;

		_horizontalDisplayScale =
			horizontalScale;

		_verticalDisplayScale =
			verticalScale;


		ApplyDisplayParameters();
	}


	internal void SetSurfaceMarkers(
		bool grid,
		bool markers)
	{
		_showGrid =
			grid;

		_showMarkers =
			markers;


		ApplyDisplayParameters();
	}


	internal void SetNormalMethod(
		int method)
	{
		NormalMethod =
			method <= 0
				? 0
				: 1;


		SetAllSamplingParameter(
			"normal_method",
			NormalMethod);


		_materialNormalMethod =
			NormalMethod;
	}


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


		//
		// When the texture becomes null we can leave the old GPU binding
		// untouched because visual_micro_normals_enabled is false.
		//
		// When another texture is assigned, bind the new one immediately.
		//

		if (VisualNormalMap != null &&
			(force ||
			 !_visualNormalStateInitialized ||
			 textureChanged))
		{
			SetSurfaceParameter(
				"visual_normal_map",
				VisualNormalMap);
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


	public override void _Ready()
	{
		_runtime =
			GetParent() as OceanRuntime;


		if (_runtime == null)
		{
			GD.PushError(
				"AnimatedWaveSurfaceRenderer must be a child of OceanRuntime.");


			SetProcess(
				false);


			return;
		}


		_surfaceShader =
			GD.Load<Shader>(
				ShaderPath);


		if (_surfaceShader == null)
		{
			GD.PushError(
				$"Could not load surface shader: {ShaderPath}");


			SetProcess(
				false);


			return;
		}


		_selectedLodIndex =
			SpatialLodIndex;


		_material =
			new ShaderMaterial
			{
				Shader =
					_surfaceShader,
			};


		ApplyDisplayParameters();


		_textureArray =
			new Texture2DArrayRD();


		_material.SetShaderParameter(
			"animated_wave_field",
			_textureArray);


		//
		// Safe before a normal map exists:
		// visual_micro_normals_enabled will be false.
		//

		ApplyVisualMicroNormalParameters(
			force: true);


		_singleRoot =
			new Node3D
			{
				Name =
					"SingleSurfaceRoot",

				Visible =
					false,
			};


		_nestedRoot =
			new Node3D
			{
				Name =
					"NestedSurfaceRoot",

				Visible =
					false,
			};


		AddChild(
			_singleRoot);


		AddChild(
			_nestedRoot);


		_meshInstance =
			new MeshInstance3D
			{
				MaterialOverride =
					_material,

				ExtraCullMargin =
					128.0f,

				Visible =
					false,
			};


		_singleRoot.AddChild(
			_meshInstance);


		Shader normalShader =
			GD.Load<Shader>(
				NormalShaderPath);


		if (normalShader == null)
		{
			GD.PushError(
				$"Could not load normal-vector shader: {NormalShaderPath}");


			return;
		}


		_normalMaterial =
			new ShaderMaterial
			{
				Shader =
					normalShader,
			};


		_normalMaterial.SetShaderParameter(
			"animated_wave_field",
			_textureArray);


		ApplyDisplayParameters();


		_normalMeshInstance =
			new MeshInstance3D
			{
				Mesh =
					CreateNormalLineMesh(),

				MaterialOverride =
					_normalMaterial,

				ExtraCullMargin =
					128.0f,

				CastShadow =
					GeometryInstance3D
						.ShadowCastingSetting
						.Off,

				Visible =
					false,
			};


		_singleRoot.AddChild(
			_normalMeshInstance);
	}


	public override void _Process(
		double delta)
	{
		if (_runtime == null ||
			!_runtime.TryGetAnimatedWaveSurfaceWithNext(
				0,
				out Rid textureRid,
				out int resolution,
				out int lodCount,
				out _,
				out _,
				out Vector2 focusXZ))
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


			return;
		}


		BindAwfTexture(
			textureRid);


		//
		// Visual micro-normal parameters can be edited in the inspector
		// at runtime. This method only touches materials when values change.
		//

		ApplyVisualMicroNormalParameters();


		float lodScaleAlpha =
			_runtime.RuntimeLodScaleAlpha;


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
				focusXZ);


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


		if (!_runtime.TryGetAnimatedWaveSurfaceWithNext(
				_selectedLodIndex,
				out _,
				out _,
				out _,
				out AnimatedWaveLodSlice slice,
				out AnimatedWaveLodSlice nextSlice,
				out _))
		{
			_singleRoot.Visible =
				false;


			return;
		}


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
		}
	}


	private static ArrayMesh CreateStitchedPatchMesh(
		int quads,
		bool stitchPositiveZ,
		bool stitchPositiveX)
	{
		//
		// Match Godot PlaneMesh FACE_Y:
		// i/j run from local +X/+Z toward -X/-Z.
		//

		int width =
			quads +
			1;


		var vertices =
			new Vector3[
				width *
				width];


		var normals =
			new Vector3[
				vertices.Length];


		var uvs =
			new Vector2[
				vertices.Length];


		var indices =
			new int[
				quads *
				quads *
				6];


		for (int z = 0;
			 z <= quads;
			 z++)
		{
			for (int x = 0;
				 x <= quads;
				 x++)
			{
				int stitchedX =
					stitchPositiveZ &&
					z == 0 &&
					(x & 1) != 0
						? x - 1
						: x;


				int stitchedZ =
					stitchPositiveX &&
					x == 0 &&
					(z & 1) != 0
						? z - 1
						: z;


				int vertex =
					z *
					width +
					x;


				vertices[vertex] =
					new Vector3(
						0.5f -
						(float)stitchedX /
						quads,

						0.0f,

						0.5f -
						(float)stitchedZ /
						quads);


				normals[vertex] =
					Vector3.Up;


				uvs[vertex] =
					new Vector2(
						1.0f -
						(float)x /
						quads,

						1.0f -
						(float)z /
						quads);
			}
		}


		int index =
			0;


		for (int z = 1;
			 z <= quads;
			 z++)
		{
			for (int x = 1;
				 x <= quads;
				 x++)
			{
				int previous =
					(z - 1) *
					width +
					x -
					1;


				int current =
					z *
					width +
					x -
					1;


				indices[index++] =
					previous;

				indices[index++] =
					previous +
					1;

				indices[index++] =
					current;

				indices[index++] =
					previous +
					1;

				indices[index++] =
					current +
					1;

				indices[index++] =
					current;
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
			(int)Mesh.ArrayType.Normal] =
			normals;


		arrays[
			(int)Mesh.ArrayType.TexUV] =
			uvs;


		arrays[
			(int)Mesh.ArrayType.Index] =
			indices;


		var mesh =
			new ArrayMesh();


		mesh.AddSurfaceFromArrays(
			Mesh.PrimitiveType.Triangles,
			arrays);


		return mesh;
	}


	private static void SelectPatch(
		int x,
		int z,
		bool stitchOuter,
		out int variant,
		out float rotationY)
	{
		variant =
			0;

		rotationY =
			0.0f;


		if (!stitchOuter)
		{
			return;
		}


		bool north =
			z ==
			TilesPerSide -
			1;


		bool south =
			z ==
			0;


		bool east =
			x ==
			TilesPerSide -
			1;


		bool west =
			x ==
			0;


		if (!north &&
			!south &&
			!east &&
			!west)
		{
			return;
		}


		variant =
			(north || south) &&
			(east || west)
				? 2
				: 1;


		if (north)
		{
			rotationY =
				west
					? -Mathf.Pi *
					  0.5f
					: 0.0f;
		}
		else if (east)
		{
			rotationY =
				Mathf.Pi *
				0.5f;
		}
		else if (south)
		{
			rotationY =
				Mathf.Pi;
		}
		else
		{
			rotationY =
				-Mathf.Pi *
				0.5f;
		}
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


		int patchResolution =
			geometryResolution /
			TilesPerSide;


		//
		// GetGeometryResolution() aligns the full grid to
		// TilesPerSide * 2, therefore every patch has an even
		// number of quads.
		//

		_nestedPatchMeshes =
			new Mesh[]
			{
				new PlaneMesh
				{
					Size =
						Vector2.One,

					SubdivideWidth =
						patchResolution -
						1,

					SubdivideDepth =
						patchResolution -
						1,
				},

				CreateStitchedPatchMesh(
					patchResolution,
					stitchPositiveZ: true,
					stitchPositiveX: false),

				CreateStitchedPatchMesh(
					patchResolution,
					stitchPositiveZ: true,
					stitchPositiveX: true),
			};


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
				_textureArray);


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


			for (int z = 0;
				 z < TilesPerSide;
				 z++)
			{
				for (int x = 0;
					 x < TilesPerSide;
					 x++)
				{
					if (lod > 0 &&
						(x is 1 or 2) &&
						(z is 1 or 2))
					{
						continue;
					}


					SelectPatch(
						x,
						z,
						lod <
						lodCount -
						1,
						out int patchVariant,
						out float rotationY);


					var tile =
						new MeshInstance3D
						{
							Name =
								$"Tile{x}_{z}",

							Mesh =
								_nestedPatchMeshes[
									patchVariant],

							MaterialOverride =
								material,

							Position =
								new Vector3(
									x -
									1.5f,
									0.0f,
									z -
									1.5f),

							Rotation =
								new Vector3(
									0.0f,
									rotationY,
									0.0f),

							ExtraCullMargin =
								128.0f,
						};


					lodRoot.AddChild(
						tile);


					_nestedTileCount++;
				}
			}
		}


		_nestedResolution =
			resolution;


		_nestedGeometryResolution =
			geometryResolution;


		_nestedLodCount =
			lodCount;


		ApplyDisplayParameters();


		SetAllSamplingParameter(
			"normal_method",
			NormalMethod);


		//
		// _Process() can set altitude alpha before nested materials exist.
		//

		SetAllSamplingParameter(
			"lod_scale_alpha",
			float.IsFinite(
				_lodScaleAlpha)
				? _lodScaleAlpha
				: 0.0f);


		//
		// Newly created nested materials must receive the complete current
		// renderer-only micro-normal state.
		//

		ApplyVisualMicroNormalParameters(
			force: true);


		GD.Print(
			$"[Ocean] Nested surface ready: " +
			$"{lodCount} LODs, {_nestedTileCount} tiles, " +
			$"AWF {resolution}², geometry {geometryResolution}², " +
			$"patch {patchResolution} quads, " +
			$"downsample x{GeometryDownSampleFactor}, " +
			$"{_nestedPatchMeshes.Length} shared patch variants.");
	}


	private void UpdateNestedMaterials(
		int lodCount,
		Vector2 focusXZ)
	{
		int geometryResolution =
			_nestedGeometryResolution >
			0
				? _nestedGeometryResolution
				: GetGeometryResolution(
					_nestedResolution);


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
			if (!_runtime.TryGetAnimatedWaveSurfaceWithNext(
					lod,
					out _,
					out _,
					out _,
					out AnimatedWaveLodSlice slice,
					out AnimatedWaveLodSlice nextSlice,
					out _))
			{
				continue;
			}


			ShaderMaterial material =
				_nestedMaterials[
					lod];


			bool hasNext =
				lod +
				1 <
				lodCount;


			if (_nestedWorldSizes[lod] !=
				slice.WorldSize)
			{
				float tileWorldSize =
					slice.WorldSize /
					TilesPerSide;


				_nestedLodRoots[lod].Scale =
					new Vector3(
						tileWorldSize,
						1.0f,
						tileWorldSize);


				//
				// Physical spacing between adjacent geometry vertices.
				//
				// Production baseline LOD0:
				//     32m / 192 = 0.1666667m.
				//

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


			_singleRoot.Position =
				new Vector3(
					slice.CenterXZ.X,
					0.0f,
					slice.CenterXZ.Y);


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


			SetSamplingParameter(
				"lod_focus_xz",
				focusXZ);
		}
	}


	private int GetGeometryResolution(
		int animatedWaveResolution)
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


		int alignment =
			TilesPerSide *
			2;


		int geometryResolution =
			Mathf.Max(
				alignment,
				animatedWaveResolution /
				factor);


		//
		// Nested stitching collapses every second boundary vertex.
		// Align the complete grid so each patch has an even quad count.
		//

		geometryResolution -=
			geometryResolution %
			alignment;


		return Mathf.Max(
			alignment,
			geometryResolution);
	}


	public override void _ExitTree()
	{
		if (_textureArray != null)
		{
			_textureArray.TextureRdRid =
				default;
		}


		_boundRid =
			default;
	}
}
