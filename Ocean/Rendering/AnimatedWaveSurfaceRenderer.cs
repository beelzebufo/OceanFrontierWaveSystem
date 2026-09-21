using System;
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


	// Crest Water 4 OceanRenderer._extentsSizeMultiplier default.
	// Applied only to the outermost FatXOuter / FatXZOuter patches.
	[Export(PropertyHint.Range, "1,500,1,or_greater")]
	public float ExtentsSizeMultiplier { get; set; } = 100.0f;


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
	// Conservative validation default; Crest's source default is 0.36.

	[Export(PropertyHint.Range, "0,2,0.01")]
	public float VisualNormalStrength { get; set; } = 0.08f;


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
	// Crest Water 4 patch mesh set. Geometry.cs owns the exact
	// OceanBuilder PatchType ordering and tile layout.
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
	private Texture2D _visualNormalFallback;
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
	// One coherent committed AnimatedWaveField spatial state is copied once
	// per renderer frame. This is the CPU-side equivalent of consuming one
	// Crest LodTransform.RenderData.Current / CascadeParams generation.
	//
	// No per-frame allocation.
	//

	private AnimatedWaveLodSlice[] _renderStateSlices;


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


		//
		// Persistent storage for one complete committed AWF LOD stack.
		// The runtime copies into this buffer; the renderer never allocates
		// per frame and never recalculates spatial LOD metadata.
		//

		_renderStateSlices =
			new AnimatedWaveLodSlice[
				Math.Max(
					1,
					_runtime.AnimatedWaveLodCount)];


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


		Image fallbackImage =
			Image.CreateEmpty(
				1,
				1,
				false,
				Image.Format.Rgba8);


		fallbackImage.SetPixel(
			0,
			0,
			new Color(
				0.5f,
				0.5f,
				1.0f,
				1.0f));


		_visualNormalFallback =
			ImageTexture.CreateFromImage(
				fallbackImage);


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
