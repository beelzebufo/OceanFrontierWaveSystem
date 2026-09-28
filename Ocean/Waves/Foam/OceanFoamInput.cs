using Godot;

namespace OceanFrontier.Water.Waves.Foam;

public enum OceanFoamInputMode
{
	AdditiveRate = 0,
	Override = 1,
}


public interface IOceanFoamInput
{
	bool Enabled { get; }
	int Priority { get; }
	OceanFoamInputMode Mode { get; }
	float Weight { get; }
	Vector2 SizeXZ { get; }
	float FeatherWidth { get; }
}


internal interface IOceanFoamInputSnapshotSource : IOceanFoamInput
{
	string DiagnosticName { get; }

	bool TryCapture(
		long registrationOrder,
		out OceanFoamInputSnapshot snapshot);
}


/// <summary>Allocation-free main-thread snapshot for the render thread.</summary>
internal readonly struct OceanFoamInputSnapshot
{
	internal readonly int Priority;
	internal readonly long RegistrationOrder;
	internal readonly OceanFoamInputMode Mode;
	internal readonly Vector2 CenterXZ;
	internal readonly Vector2 AxisX;
	internal readonly Vector2 AxisZ;
	internal readonly Vector2 SizeXZ;
	internal readonly float FeatherWidth;
	internal readonly float Weight;
	internal readonly float AdditiveRate;
	internal readonly float OverrideValue;

	internal OceanFoamInputSnapshot(
		int priority,
		long registrationOrder,
		OceanFoamInputMode mode,
		Vector2 centerXZ,
		Vector2 axisX,
		Vector2 axisZ,
		Vector2 sizeXZ,
		float featherWidth,
		float weight,
		float additiveRate,
		float overrideValue)
	{
		Priority = priority;
		RegistrationOrder = registrationOrder;
		Mode = mode;
		CenterXZ = centerXZ;
		AxisX = axisX;
		AxisZ = axisZ;
		SizeXZ = sizeXZ;
		FeatherWidth = featherWidth;
		Weight = weight;
		AdditiveRate = additiveRate;
		OverrideValue = overrideValue;
	}
}
