using Godot;
using OceanFrontier.Water.Rendering;
using OceanFrontier.Water.Runtime;
using OceanFrontier.Water.Waves.AnimatedWaves;

namespace OceanFrontier.Water.Debug;

/// <summary>Five persistent world-space markers driven only by completed GPU point queries.</summary>
public partial class OceanPointQueryDiagnostic : Node3D
{
	private const int PointCount = 5;
	private const float SeaLevel = 0.0f;
	private readonly Vector2[] _targets = new Vector2[PointCount];
	private readonly Vector2[] _submittedTargets = new Vector2[PointCount];
	private readonly Vector4[] _completed = new Vector4[PointCount];
	private readonly MeshInstance3D[] _targetMarkers = new MeshInstance3D[PointCount];
	private readonly MeshInstance3D[] _surfaceMarkers = new MeshInstance3D[PointCount];
	private readonly MeshInstance3D[] _invalidMarkers = new MeshInstance3D[PointCount];
	private readonly MeshInstance3D[] _horizontalLines = new MeshInstance3D[PointCount];
	private OceanRuntime _runtime;
	private AnimatedWaveSurfaceRenderer _surface;
	private int _selectedLod;
	private long _pendingGeneration;
	private float _submittedWorldSize;

	public override void _Ready()
	{
		_runtime = GetParent() as OceanRuntime;
		_surface = GetParent().GetNodeOrNull<AnimatedWaveSurfaceRenderer>("AnimatedWaveSurfaceRenderer");
		if (_runtime == null || _surface == null) { SetProcess(false); return; }
		_selectedLod = _surface.SpatialLodIndex;

		var sphere = new SphereMesh { Radius = 0.5f, Height = 1.0f };
		var line = new CylinderMesh { TopRadius = 0.5f, BottomRadius = 0.5f, Height = 1.0f };
		StandardMaterial3D targetMaterial = Material(new Color(0.95f, 0.95f, 0.95f));
		StandardMaterial3D resultMaterial = Material(new Color(1.0f, 0.82f, 0.12f), false);
		StandardMaterial3D invalidMaterial = Material(new Color(1.0f, 0.12f, 0.15f));
		StandardMaterial3D offsetMaterial = Material(new Color(0.12f, 1.0f, 0.95f));
		for (int i = 0; i < PointCount; i++)
		{
			_targetMarkers[i] = Marker(sphere, targetMaterial);
			_surfaceMarkers[i] = Marker(sphere, resultMaterial);
			_invalidMarkers[i] = Marker(sphere, invalidMaterial);
			_horizontalLines[i] = Marker(line, offsetMaterial);
		}
	}

	internal void SetSpatialLod(int lod) => _selectedLod = lod;

	private static StandardMaterial3D Material(Color color, bool alwaysVisible = true) => new()
	{
		ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		AlbedoColor = color,
		NoDepthTest = alwaysVisible,
	};

	private MeshInstance3D Marker(Mesh mesh, Material material)
	{
		var instance = new MeshInstance3D { Mesh = mesh, MaterialOverride = material, Visible = false };
		AddChild(instance);
		return instance;
	}

	public override void _Process(double delta)
	{
		if (!Visible || _runtime == null || _surface == null) return;

		if (_pendingGeneration != 0 && _runtime.PointQueries.TryCopyLatest(_completed,
			out int count, out long generation, out _, out _) &&
			generation == _pendingGeneration && count == PointCount)
		{
			ShowCompleted();
			_pendingGeneration = 0;
		}
		if (_pendingGeneration != 0) return;

		if (!_runtime.TryGetAnimatedWaveSurfaceWithNext(_selectedLod, out _, out _, out _,
			out AnimatedWaveLodSlice slice, out _, out Vector2 focusXZ)) return;

		// The cardinal points sit inside the selected visible square. For nested
		// layout they also sit beyond the next finer square's half-width.
		float offset = slice.WorldSize * 0.28f;
		_targets[0] = focusXZ;
		_targets[1] = focusXZ + new Vector2(offset, 0.0f);
		_targets[2] = focusXZ + new Vector2(-offset, 0.0f);
		_targets[3] = focusXZ + new Vector2(0.0f, offset);
		_targets[4] = focusXZ + new Vector2(0.0f, -offset);
		float minTexelWidth = _surface.LayoutMode == AnimatedWaveSurfaceRenderer.SurfaceLayoutMode.SingleLod
			? slice.TexelWidth : 0.0f;
		_targets.CopyTo(_submittedTargets, 0);
		_submittedWorldSize = slice.WorldSize;
		_pendingGeneration = _runtime.PointQueries.SubmitBatch(_targets, minTexelWidth);
	}

	private void ShowCompleted()
	{
		float diameter = _submittedWorldSize * 0.035f;
		float lineWidth = _submittedWorldSize * 0.004f;
		for (int i = 0; i < PointCount; i++)
		{
			Vector2 target = _submittedTargets[i];
			var basePosition = new Vector3(target.X, SeaLevel, target.Y);
			_targetMarkers[i].Position = basePosition;
			_targetMarkers[i].Scale = Vector3.One * (diameter * 0.48f);
			_targetMarkers[i].Visible = true;

			Vector4 result = _completed[i];
			bool valid = result.W > 0.5f &&
				float.IsFinite(result.X) && float.IsFinite(result.Y) && float.IsFinite(result.Z);
			_surfaceMarkers[i].Visible = valid;
			_invalidMarkers[i].Visible = !valid;
			_horizontalLines[i].Visible = false;
			if (!valid)
			{
				_invalidMarkers[i].Position = basePosition;
				_invalidMarkers[i].Scale = Vector3.One * diameter;
				continue;
			}

			var surfacePosition = new Vector3(target.X, SeaLevel + result.Y, target.Y);
			_surfaceMarkers[i].Position = surfacePosition;
			_surfaceMarkers[i].Scale = Vector3.One * diameter;

			// The query shader solves an undisplaced source XZ. Its final
			// horizontal displacement carries that source to the target XZ.
			var sourcePosition = new Vector3(target.X - result.X, surfacePosition.Y, target.Y - result.Z);
			Vector3 offset = surfacePosition - sourcePosition;
			float length = offset.Length();
			if (length < lineWidth) continue;
			MeshInstance3D segment = _horizontalLines[i];
			segment.Position = (sourcePosition + surfacePosition) * 0.5f;
			segment.Basis = new Basis(new Quaternion(Vector3.Up, offset / length));
			segment.Scale = new Vector3(lineWidth, length, lineWidth);
			segment.Visible = true;
		}
	}
}
