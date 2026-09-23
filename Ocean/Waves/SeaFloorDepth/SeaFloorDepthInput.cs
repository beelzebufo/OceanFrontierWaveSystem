using Godot;

namespace OceanFrontier.Water.Waves.SeaFloorDepth;

public interface ISeaFloorDepthInput
{
	bool Enabled { get; }
	float BottomHeightY { get; }
	Vector2 SizeXZ { get; }
}

internal interface ISeaFloorDepthInputSnapshotSource : ISeaFloorDepthInput
{
	string DiagnosticName { get; }
	bool TryCapture(out SeaFloorDepthInputSnapshot snapshot);
}

internal readonly struct SeaFloorDepthInputSnapshot
{
	public readonly Vector2 CenterXZ;
	public readonly Vector2 AxisX;
	public readonly Vector2 AxisZ;
	public readonly Vector2 SizeXZ;
	public readonly float BottomHeightY;

	public SeaFloorDepthInputSnapshot(
		Vector2 centerXZ,
		Vector2 axisX,
		Vector2 axisZ,
		Vector2 sizeXZ,
		float bottomHeightY)
	{
		CenterXZ = centerXZ;
		AxisX = axisX;
		AxisZ = axisZ;
		SizeXZ = sizeXZ;
		BottomHeightY = bottomHeightY;
	}
}
