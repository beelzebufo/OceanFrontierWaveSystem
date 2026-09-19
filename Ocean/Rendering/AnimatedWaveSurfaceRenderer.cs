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
	private int _fixedLodIndex;

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

		_fixedLodIndex = SpatialLodIndex;
		_material = new ShaderMaterial { Shader = shader };
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
			!_runtime.TryGetAnimatedWaveSurface(
				_fixedLodIndex,
				out Rid textureRid,
				out int resolution,
				out int lodCount,
				out AnimatedWaveLodSlice slice))
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
			_material.SetShaderParameter("lod_world_size", slice.WorldSize);
			_material.SetShaderParameter("selected_lod", (float)_fixedLodIndex);
			GD.Print($"[Ocean] Canonical surface grid ready: LOD {_fixedLodIndex}/{lodCount - 1}, {resolution}x{resolution} quads.");
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

		_meshInstance.Visible = true;
	}

	public override void _ExitTree()
	{
		if (_textureArray != null)
			_textureArray.TextureRdRid = default;
		_boundRid = default;
	}
}
