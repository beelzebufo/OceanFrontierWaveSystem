using Godot;

namespace OceanFrontier.Water.Debug;

public partial class OceanDiagnosticCameraController
{
	private float _minimumFarDistance;


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
		float minimum =
			float.IsFinite(distance) &&
			distance > 0.0f
				? distance
				: 0.0f;


		if (Mathf.IsEqualApprox(
				_minimumFarDistance,
				minimum))
		{
			return;
		}


		_minimumFarDistance =
			minimum;


		UpdateFar();
	}
}
