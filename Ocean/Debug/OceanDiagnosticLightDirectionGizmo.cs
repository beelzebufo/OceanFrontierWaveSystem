using Godot;
using OceanFrontier.Water.Waves.AnimatedWaves;

namespace OceanFrontier.Water.Debug;

/// <summary>Unlit world-space arrow following the diagnostic sun's actual ray direction.</summary>
public partial class OceanDiagnosticLightDirectionGizmo : Node3D
{
	private Vector2 _lastCenter = new(float.NaN, float.NaN);
	private float _lastWorldSize = float.NaN;
	private Vector3 _lastRayDirection = new(float.NaN, float.NaN, float.NaN);

	public override void _Ready()
	{
		var material = new StandardMaterial3D
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			AlbedoColor = new Color(1.0f, 0.9f, 0.1f),
			NoDepthTest = true,
		};
		var shaft = new MeshInstance3D
		{
			Mesh = new CylinderMesh { TopRadius = 0.02f, BottomRadius = 0.02f,
				Height = 0.8f, RadialSegments = 8 },
			MaterialOverride = material,
			Position = new Vector3(0, 0, -0.4f),
			RotationDegrees = new Vector3(-90, 0, 0),
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		var head = new MeshInstance3D
		{
			Mesh = new CylinderMesh { TopRadius = 0.0f, BottomRadius = 0.08f,
				Height = 0.2f, RadialSegments = 8 },
			MaterialOverride = material,
			Position = new Vector3(0, 0, -0.9f),
			RotationDegrees = new Vector3(-90, 0, 0),
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		AddChild(shaft);
		AddChild(head);
		Visible = false;
	}

	internal void UpdateFrom(DirectionalLight3D sun, AnimatedWaveLodSlice slice)
	{
		// Godot directional light rays travel along its global local -Z axis.
		Vector3 rayDirection = -sun.GlobalTransform.Basis.Z.Normalized();
		if (_lastCenter == slice.CenterXZ && _lastWorldSize == slice.WorldSize &&
			_lastRayDirection == rayDirection)
			return;
		_lastCenter = slice.CenterXZ;
		_lastWorldSize = slice.WorldSize;
		_lastRayDirection = rayDirection;
		GlobalPosition = new Vector3(slice.CenterXZ.X,
			Mathf.Clamp(slice.WorldSize * 0.08f, 1.0f, 45.0f), slice.CenterXZ.Y);
		LookAt(GlobalPosition + rayDirection, Vector3.Up);
		float length = Mathf.Clamp(slice.WorldSize * 0.15f, 0.5f, 40.0f);
		Scale = Vector3.One * length;
	}
}
