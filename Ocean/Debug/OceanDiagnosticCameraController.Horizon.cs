using Godot;

namespace OceanFrontier.Water.Debug;

public partial class OceanDiagnosticCameraController
{
	/// <summary>
	/// Raises the current far plane when a renderer needs more distant
	/// coverage than the selected diagnostic LOD framing would normally use.
	///
	/// The existing camera controller remains authoritative for ordinary
	/// framing. This method only supplies a lower bound.
	/// </summary>
	internal void SetMinimumFarDistance(
		float distance)
	{
		if (!float.IsFinite(distance) ||
			distance <= 0.0f)
		{
			return;
		}


		if (Far <
			distance)
		{
			Far =
				distance;
		}
	}
}
