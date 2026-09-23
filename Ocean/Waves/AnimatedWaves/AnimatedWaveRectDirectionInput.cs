using Godot;

namespace OceanFrontier.Water.Waves.AnimatedWaves;

/// <summary>
/// Rectangular local source which resamples every eligible cascade of the
/// existing global FFT at a relative direction. It does not own an FFT.
/// </summary>
[GlobalClass]
public partial class AnimatedWaveRectDirectionInput :
	AnimatedWaveRectInputBase
{
	[Export]
	public AnimatedWaveInputBlendMode BlendMode { get; set; } =
		AnimatedWaveInputBlendMode.Additive;

	[Export(PropertyHint.Range, "0,1,0.01")]
	public float Weight { get; set; } = 1.0f;

	[Export(PropertyHint.Range, "-180,180,0.1")]
	public float DirectionOffsetDegrees { get; set; }


	internal override AnimatedWaveInputSnapshot CreateSnapshot(
		long registrationOrder,
		Vector2 centerXZ,
		Vector2 axisX,
		Vector2 axisZ,
		Vector2 sizeXZ,
		float featherWidth)
	{
		AnimatedWaveInputBlendMode blendMode =
			BlendMode is
				AnimatedWaveInputBlendMode.Additive or
				AnimatedWaveInputBlendMode.Blend
					? BlendMode
					: AnimatedWaveInputBlendMode.Additive;


		float weight =
			float.IsFinite(Weight)
				? Mathf.Clamp(Weight, 0.0f, 1.0f)
				: 0.0f;


		float directionDegrees =
			float.IsFinite(DirectionOffsetDegrees)
				? Mathf.PosMod(DirectionOffsetDegrees + 180.0f, 360.0f) - 180.0f
				: 0.0f;


		return new AnimatedWaveInputSnapshot(
			Priority,
			registrationOrder,
			AnimatedWaveInputOperation.DirectionalFft,
			AnimatedWaveInputPlacement.WavelengthFilteredPreCombine,
			blendMode,
			weight,
			1.0f,
			0.0f,
			centerXZ,
			axisX,
			axisZ,
			sizeXZ,
			featherWidth,
			Vector3.Zero,
			1.0f,
			false,
			Mathf.DegToRad(directionDegrees));
	}
}
