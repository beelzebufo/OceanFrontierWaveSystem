using Godot;
using OceanFrontier.Water.Runtime;
using OceanFrontier.Water.Waves.AnimatedWaves;

namespace OceanFrontier.Water.Debug;

/// <summary>Undisplaced Y=0 footprint, axes and 8x8 metric grid.</summary>
public partial class OceanDiagnosticReferenceFrame : Node3D
{
	private OceanRuntime _runtime;
	private MeshInstance3D _instance;
	private ImmediateMesh _mesh;
	private StandardMaterial3D _material;
	private int _lod;
	private float _worldSize;
	private Vector2 _center = new(float.NaN, float.NaN);

	internal void SetSpatialLod(int lod) => _lod = lod;

	public override void _Ready()
	{
		_runtime = GetParent() as OceanRuntime;
		_lod = GetParent().GetNodeOrNull<OceanFrontier.Water.Rendering.AnimatedWaveSurfaceRenderer>(
			"AnimatedWaveSurfaceRenderer")?.SpatialLodIndex ?? 0;
		_material = new StandardMaterial3D
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			VertexColorUseAsAlbedo = true,
			NoDepthTest = true,
		};
		_mesh = new ImmediateMesh();
		_instance = new MeshInstance3D { Mesh = _mesh, ExtraCullMargin = 128.0f };
		AddChild(_instance);
	}

	public override void _Process(double delta)
	{
		if (_runtime == null || !Visible ||
			!_runtime.TryGetAnimatedWaveSurface(_lod, out _, out _, out _, out AnimatedWaveLodSlice slice))
			return;

		if (_worldSize != slice.WorldSize)
		{
			_worldSize = slice.WorldSize;
			Rebuild();
		}
		if (_center != slice.CenterXZ)
		{
			_center = slice.CenterXZ;
			Position = new Vector3(_center.X, 0.0f, _center.Y);
		}
	}

	private void Rebuild()
	{
		_mesh.ClearSurfaces();
		_mesh.SurfaceBegin(Mesh.PrimitiveType.Lines, _material);
		float half = _worldSize * 0.5f;
		Color major = new(0.25f, 0.34f, 0.38f);
		Color boundary = new(1.0f, 0.72f, 0.05f);
		Color xAxis = new(1.0f, 0.15f, 0.15f);
		Color zAxis = new(0.15f, 0.55f, 1.0f);

		for (int i = 1; i < 8; i++)
		{
			float p = -half + _worldSize * i / 8.0f;
			Line(new Vector3(p, 0, -half), new Vector3(p, 0, half), major);
			Line(new Vector3(-half, 0, p), new Vector3(half, 0, p), major);
		}
		Line(new Vector3(-half, 0, -half), new Vector3(half, 0, -half), boundary);
		Line(new Vector3(half, 0, -half), new Vector3(half, 0, half), boundary);
		Line(new Vector3(half, 0, half), new Vector3(-half, 0, half), boundary);
		Line(new Vector3(-half, 0, half), new Vector3(-half, 0, -half), boundary);
		Line(new Vector3(-half, 0, 0), new Vector3(half, 0, 0), xAxis);
		Line(new Vector3(0, 0, -half), new Vector3(0, 0, half), zAxis);
		float cross = _worldSize / 64.0f;
		Line(new Vector3(-cross, 0, 0), new Vector3(cross, 0, 0), Colors.White);
		Line(new Vector3(0, 0, -cross), new Vector3(0, 0, cross), Colors.White);
		_mesh.SurfaceEnd();
	}

	private void Line(Vector3 a, Vector3 b, Color color)
	{
		_mesh.SurfaceSetColor(color);
		_mesh.SurfaceAddVertex(a);
		_mesh.SurfaceSetColor(color);
		_mesh.SurfaceAddVertex(b);
	}
}
