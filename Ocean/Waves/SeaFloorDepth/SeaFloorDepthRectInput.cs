using Godot;
using OceanFrontier.Water.Runtime;

namespace OceanFrontier.Water.Waves.SeaFloorDepth;

[GlobalClass]
public partial class SeaFloorDepthRectInput : Node3D, ISeaFloorDepthInputSnapshotSource
{
	[Export] public bool Enabled { get; set; } = true;

	[Export(PropertyHint.Range, "-65504,65504,0.1")]
	public float BottomHeightY { get; set; } = -10.0f;

	[Export] public Vector2 SizeXZ { get; set; } = new(20.0f, 20.0f);

	private OceanRuntime _runtime;
	string ISeaFloorDepthInputSnapshotSource.DiagnosticName => Name;

	public override void _EnterTree()
	{
		_runtime = FindRuntime();
		_runtime?.RegisterSeaFloorDepthInput(this);
	}

	public override void _ExitTree()
	{
		_runtime?.UnregisterSeaFloorDepthInput(this);
		_runtime = null;
	}

	private OceanRuntime FindRuntime()
	{
		for (Node current = GetParent(); current != null; current = current.GetParent())
		{
			if (current is OceanRuntime runtime) return runtime;
		}
		return null;
	}

	bool ISeaFloorDepthInputSnapshotSource.TryCapture(out SeaFloorDepthInputSnapshot snapshot)
	{
		snapshot = default;
		if (!Enabled || !IsInsideTree()) return false;

		Transform3D transform = GlobalTransform;
		Vector3 position = transform.Origin;
		if (!position.IsFinite() || !float.IsFinite(BottomHeightY)) return false;

		Vector2 axisX = new(transform.Basis.X.X, transform.Basis.X.Z);
		axisX = axisX.IsFinite() && axisX.LengthSquared() >= 0.000001f
			? axisX.Normalized()
			: Vector2.Right;

		Vector2 size = new(
			float.IsFinite(SizeXZ.X) ? Mathf.Max(Mathf.Abs(SizeXZ.X), 0.001f) : 0.001f,
			float.IsFinite(SizeXZ.Y) ? Mathf.Max(Mathf.Abs(SizeXZ.Y), 0.001f) : 0.001f);
		Vector2 axisZ = new(-axisX.Y, axisX.X);

		snapshot = new SeaFloorDepthInputSnapshot(
			new Vector2(position.X, position.Z), axisX, axisZ, size,
			Mathf.Clamp(BottomHeightY, -65504.0f, 65504.0f));
		return true;
	}
}
