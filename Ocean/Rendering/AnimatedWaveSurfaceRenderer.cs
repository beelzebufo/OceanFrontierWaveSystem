using Godot;
using OceanFrontier.Water.Runtime;
using OceanFrontier.Water.Waves.AnimatedWaves;

namespace OceanFrontier.Water.Rendering;

/// <summary>
/// Stage 3A-1: one diagnostic grid sampling one canonical spatial LOD.
/// The AnimatedWaveField owns the RenderingDevice texture RID.
/// </summary>
public partial class AnimatedWaveSurfaceRenderer : Node3D
{
	private const string ShaderPath =
		"res://Ocean/Shaders/Rendering/animated_wave_surface.gdshader";

	[Export(PropertyHint.Range, "0,15,1")]
	public int SpatialLodIndex { get; set; } = 4;

	private OceanRuntime _runtime;
	private MeshInstance3D _meshInstance;
	private PlaneMesh _plane;
	private ShaderMaterial _material;
	private Texture2DArrayRD _textureArray;
	private Rid _boundRid;
	private Vector2 _lastCenter = new(float.NaN, float.NaN);
	private Vector2 _lastNextCenter = new(float.NaN, float.NaN);
	private int _selectedLodIndex;
	private int _waveContentMode;
	private int _displayMode;
	private float _horizontalDisplayScale = 1.0f;
	private float _verticalDisplayScale = 1.0f;
	private bool _showGrid = true;
	private bool _showMarkers = true;
	private int _meshResolution;
	private float _meshWorldSize;
	private int _materialLod = -1;

	internal int SelectedLodIndex => _selectedLodIndex;
	internal void SetSpatialLod(int lod) => _selectedLodIndex = lod;
	internal void SetWaveContent(int mode)
	{
		_waveContentMode = mode;
		_material?.SetShaderParameter("wave_content_mode", _waveContentMode);
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
		_material.SetShaderParameter("display_mode", _displayMode);
		_material.SetShaderParameter("horizontal_display_scale", _horizontalDisplayScale);
		_material.SetShaderParameter("vertical_display_scale", _verticalDisplayScale);
		_material.SetShaderParameter("show_surface_grid", _showGrid);
		_material.SetShaderParameter("show_surface_markers", _showMarkers);
		_material.SetShaderParameter("wave_content_mode", _waveContentMode);
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
				out AnimatedWaveLodSlice nextSlice))
		{
			if (_meshInstance != null)
				_meshInstance.Visible = false;
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
		if (_materialLod != _selectedLodIndex)
		{
			_material.SetShaderParameter("lod_world_size", slice.WorldSize);
			_material.SetShaderParameter("selected_lod", (float)_selectedLodIndex);
			bool hasNext = _selectedLodIndex + 1 < lodCount;
			_material.SetShaderParameter("has_next_lod", hasNext);
			_material.SetShaderParameter("next_lod", (float)(hasNext ? _selectedLodIndex + 1 : _selectedLodIndex));
			_material.SetShaderParameter("next_lod_world_size", hasNext ? nextSlice.WorldSize : slice.WorldSize);
			_materialLod = _selectedLodIndex;
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
			_material.SetShaderParameter("lod_center_xz", slice.CenterXZ);
		}
		if (_selectedLodIndex + 1 < lodCount && _lastNextCenter != nextSlice.CenterXZ)
		{
			_lastNextCenter = nextSlice.CenterXZ;
			_material.SetShaderParameter("next_lod_center_xz", nextSlice.CenterXZ);
		}

		_meshInstance.Visible = true;
	}

	public override void _ExitTree()
	{
		if (_textureArray != null)
			_textureArray.TextureRdRid = default;
		_boundRid = default;
	}
}
