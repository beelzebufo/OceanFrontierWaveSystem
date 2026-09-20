using Godot;
using OceanFrontier.Water.Runtime;
using OceanFrontier.Water.Waves.AnimatedWaves;

namespace OceanFrontier.Water.Rendering;

/// <summary>
/// One validation grid sampling adjacent canonical spatial LODs.
/// The AnimatedWaveField owns the RenderingDevice texture RID.
/// </summary>
public partial class AnimatedWaveSurfaceRenderer : Node3D
{
	private const int NormalGridResolution = 17;
	private const float NormalVectorScale = 0.35f;
	private const string ShaderPath =
		"res://Ocean/Shaders/Rendering/animated_wave_surface.gdshader";
	private const string NormalShaderPath =
		"res://Ocean/Shaders/Rendering/animated_wave_normal_debug.gdshader";

	[Export(PropertyHint.Range, "0,15,1")]
	public int SpatialLodIndex { get; set; } = 4;

	[Export(PropertyHint.Enum, "Blended XYZ Forward,Crest Per LOD Forward")]
	public int NormalMethod { get; set; } = 0;

	private OceanRuntime _runtime;
	private MeshInstance3D _meshInstance;
	private MeshInstance3D _normalMeshInstance;
	private PlaneMesh _plane;
	private ShaderMaterial _material;
	private ShaderMaterial _normalMaterial;
	private Texture2DArrayRD _textureArray;
	private Rid _boundRid;
	private Vector2 _lastCenter = new(float.NaN, float.NaN);
	private Vector2 _lastNextCenter = new(float.NaN, float.NaN);
	private Vector2 _lastFocus = new(float.NaN, float.NaN);
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
	private int _materialNormalMethod = -1;

	internal int SelectedLodIndex => _selectedLodIndex;
	internal void SetSpatialLod(int lod) => _selectedLodIndex = lod;
	internal void SetWaveContent(int mode)
	{
		_waveContentMode = mode;
		ApplyDisplayParameters();
	}
	internal void SetLightingEnabled(bool enabled)
	{
		_lightingEnabled = enabled;
		_material?.SetShaderParameter("lighting_enabled", enabled);
	}
	internal void SetDiagnosticRoughness(float roughness)
	{
		_diagnosticRoughness = Mathf.Clamp(roughness, 0.0f, 1.0f);
		_material?.SetShaderParameter("diagnostic_roughness", _diagnosticRoughness);
	}
	internal void SetNormalVectorsVisible(bool visible)
	{
		_showNormalVectors = visible;
		if (_normalMeshInstance != null)
			_normalMeshInstance.Visible = visible && _meshInstance.Visible;
	}
	internal void SetDisplay(int mode, float horizontalScale, float verticalScale)
	{
		_displayMode = mode;
		_horizontalDisplayScale = horizontalScale;
		_verticalDisplayScale = verticalScale;
		ApplyDisplayParameters();
	}
	internal void SetSurfaceMarkers(bool grid, bool markers)
	{
		_showGrid = grid;
		_showMarkers = markers;
		ApplyDisplayParameters();
	}
	private void ApplyDisplayParameters()
	{
		if (_material == null) return;
		SetSamplingParameter("display_mode", _displayMode);
		SetSamplingParameter("horizontal_display_scale", _horizontalDisplayScale);
		SetSamplingParameter("vertical_display_scale", _verticalDisplayScale);
		_material.SetShaderParameter("show_surface_grid", _showGrid);
		_material.SetShaderParameter("show_surface_markers", _showMarkers);
		SetSamplingParameter("wave_content_mode", _waveContentMode);
		_material.SetShaderParameter("lighting_enabled", _lightingEnabled);
		_material.SetShaderParameter("diagnostic_roughness", _diagnosticRoughness);
	}

	private void SetSamplingParameter(string name, Variant value)
	{
		_material.SetShaderParameter(name, value);
		_normalMaterial?.SetShaderParameter(name, value);
	}

	private static ArrayMesh CreateNormalLineMesh()
	{
		// COLOR.r marks each static anchor's base (0) and tip (1).
		var vertices = new Vector3[NormalGridResolution * NormalGridResolution * 2];
		var endpoints = new Color[vertices.Length];
		int index = 0;
		for (int z = 0; z < NormalGridResolution; z++)
		for (int x = 0; x < NormalGridResolution; x++)
		{
			var anchor = new Vector3(
				(float)x / (NormalGridResolution - 1) - 0.5f,
				0.0f,
				(float)z / (NormalGridResolution - 1) - 0.5f);
			vertices[index] = anchor;
			endpoints[index++] = new Color(0, 0, 0);
			vertices[index] = anchor;
			endpoints[index++] = new Color(1, 0, 0);
		}
		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = vertices;
		arrays[(int)Mesh.ArrayType.Color] = endpoints;
		var mesh = new ArrayMesh();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
		return mesh;
	}

	public override void _Ready()
	{
		_runtime = GetParent() as OceanRuntime;
		if (_runtime == null)
		{
			GD.PushError("AnimatedWaveSurfaceRenderer must be a child of OceanRuntime.");
			SetProcess(false);
			return;
		}

		Shader shader = GD.Load<Shader>(ShaderPath);
		if (shader == null)
		{
			GD.PushError($"Could not load surface shader: {ShaderPath}");
			SetProcess(false);
			return;
		}

		_selectedLodIndex = SpatialLodIndex;
		_material = new ShaderMaterial { Shader = shader };
		ApplyDisplayParameters();
		_textureArray = new Texture2DArrayRD();
		_material.SetShaderParameter("animated_wave_field", _textureArray);

		_meshInstance = new MeshInstance3D
		{
			MaterialOverride = _material,
			ExtraCullMargin = 128.0f,
			Visible = false,
		};
		AddChild(_meshInstance);
		Shader normalShader = GD.Load<Shader>(NormalShaderPath);
		if (normalShader == null)
		{
			GD.PushError($"Could not load normal-vector shader: {NormalShaderPath}");
			return;
		}
		_normalMaterial = new ShaderMaterial { Shader = normalShader };
		_normalMaterial.SetShaderParameter("animated_wave_field", _textureArray);
		ApplyDisplayParameters();
		_normalMeshInstance = new MeshInstance3D
		{
			Mesh = CreateNormalLineMesh(),
			MaterialOverride = _normalMaterial,
			ExtraCullMargin = 128.0f,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			Visible = false,
		};
		AddChild(_normalMeshInstance);
	}

	public override void _Process(double delta)
	{
		if (_runtime == null ||
			!_runtime.TryGetAnimatedWaveSurfaceWithNext(
				_selectedLodIndex,
				out Rid textureRid,
				out int resolution,
				out int lodCount,
				out AnimatedWaveLodSlice slice,
				out AnimatedWaveLodSlice nextSlice,
				out Vector2 focusXZ))
		{
			if (_meshInstance != null)
				_meshInstance.Visible = false;
			if (_normalMeshInstance != null)
				_normalMeshInstance.Visible = false;
			return;
		}

		if (_plane == null)
		{
			_plane = new PlaneMesh
			{
				Size = new Vector2(slice.WorldSize, slice.WorldSize),
				SubdivideWidth = resolution - 1,
				SubdivideDepth = resolution - 1,
			};
			_meshInstance.Mesh = _plane;
			_meshResolution = resolution;
			_meshWorldSize = slice.WorldSize;
			GD.Print($"[Ocean] Canonical surface grid ready: LOD {_selectedLodIndex}/{lodCount - 1}, {resolution}x{resolution} quads.");
		}
		if (_meshWorldSize != slice.WorldSize || _meshResolution != resolution)
		{
			_plane.Size = new Vector2(slice.WorldSize, slice.WorldSize);
			_plane.SubdivideWidth = resolution - 1;
			_plane.SubdivideDepth = resolution - 1;
			_meshWorldSize = slice.WorldSize;
			_meshResolution = resolution;
		}
		ApplySurfaceSamplingParameters(textureRid, lodCount, slice, nextSlice, focusXZ);
		_meshInstance.Visible = true;
		if (_normalMeshInstance != null)
			_normalMeshInstance.Visible = _showNormalVectors;
	}

	// Both materials receive the same sampling metadata; only their vertex paths differ.
	private void ApplySurfaceSamplingParameters(Rid textureRid, int lodCount,
		AnimatedWaveLodSlice slice, AnimatedWaveLodSlice nextSlice, Vector2 focusXZ)
	{
		if (_materialLod != _selectedLodIndex)
		{
			SetSamplingParameter("lod_world_size", slice.WorldSize);
			SetSamplingParameter("current_lod_texel_width", slice.TexelWidth);
			SetSamplingParameter("selected_lod", (float)_selectedLodIndex);
			bool hasNext = _selectedLodIndex + 1 < lodCount;
			SetSamplingParameter("has_next_lod", hasNext);
			SetSamplingParameter("next_lod", (float)(hasNext ? _selectedLodIndex + 1 : _selectedLodIndex));
			SetSamplingParameter("next_lod_world_size", hasNext ? nextSlice.WorldSize : slice.WorldSize);
			SetSamplingParameter("next_lod_texel_width", hasNext ? nextSlice.TexelWidth : slice.TexelWidth);
			_normalMaterial?.SetShaderParameter("diagnostic_normal_length",
				slice.WorldSize / (NormalGridResolution - 1) * NormalVectorScale);
			if (_normalMeshInstance != null)
				_normalMeshInstance.Scale = new Vector3(slice.WorldSize, 1.0f, slice.WorldSize);
			_materialLod = _selectedLodIndex;
		}
		if (_materialNormalMethod != NormalMethod)
		{
			SetSamplingParameter("normal_method", NormalMethod);
			_materialNormalMethod = NormalMethod;
		}

		if (!_boundRid.IsValid || _boundRid.Id != textureRid.Id)
		{
			_textureArray.TextureRdRid = textureRid;
			_boundRid = textureRid;
		}

		if (_lastCenter != slice.CenterXZ)
		{
			_lastCenter = slice.CenterXZ;
			Position = new Vector3(slice.CenterXZ.X, 0.0f, slice.CenterXZ.Y);
			SetSamplingParameter("lod_center_xz", slice.CenterXZ);
		}
		Vector2 nextCenter = _selectedLodIndex + 1 < lodCount ? nextSlice.CenterXZ : slice.CenterXZ;
		if (_lastNextCenter != nextCenter)
		{
			_lastNextCenter = nextCenter;
			SetSamplingParameter("next_lod_center_xz", nextCenter);
		}
		if (_lastFocus != focusXZ)
		{
			_lastFocus = focusXZ;
			SetSamplingParameter("lod_focus_xz", focusXZ);
		}
	}

	public override void _ExitTree()
	{
		if (_textureArray != null)
			_textureArray.TextureRdRid = default;
		_boundRid = default;
	}
}
