using Godot;

namespace OceanFrontier.Water.Waves.AnimatedWaves;

/// <summary>
/// Crest-style rectangular scalar multiplier for Animated Waves displacement.
/// </summary>
[GlobalClass]
public partial class AnimatedWaveRectScaleInput :
	AnimatedWaveRectPlacedInputBase
{
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float ScaleFactor { get; set; } = 1.0f;

	[Export]
	public bool Invert { get; set; }


	internal override AnimatedWaveInputSnapshot CreatePlacedSnapshot(
		long registrationOrder,
		AnimatedWaveInputPlacement placement,
		float wavelengthMeters,
		Vector2 centerXZ,
		Vector2 axisX,
		Vector2 axisZ,
		Vector2 sizeXZ,
		float featherWidth)
	{
		float scale =
			float.IsFinite(ScaleFactor)
				? Mathf.Clamp(ScaleFactor, 0.0f, 1.0f)
				: 1.0f;


		return new AnimatedWaveInputSnapshot(
			Priority,
			registrationOrder,
			AnimatedWaveInputOperation.ScaleByFactor,
			placement,
			AnimatedWaveInputBlendMode.Additive,
			1.0f,
			1.0f,
			wavelengthMeters,
			centerXZ,
			axisX,
			axisZ,
			sizeXZ,
			featherWidth,
			Vector3.Zero,
			scale,
			Invert,
			0.0f);
	}
}
