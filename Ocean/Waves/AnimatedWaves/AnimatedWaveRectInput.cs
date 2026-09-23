using Godot;

namespace OceanFrontier.Water.Waves.AnimatedWaves;

/// <summary>
/// Scene-usable rectangular constant-displacement Animated Waves input.
/// </summary>
[GlobalClass]
public partial class AnimatedWaveRectInput :
	AnimatedWaveRectInputBase
{
	[Export]
	public AnimatedWaveInputBlendMode BlendMode { get; set; } =
		AnimatedWaveInputBlendMode.Additive;

	[Export(PropertyHint.Range, "0,1,0.01")]
	public float Weight { get; set; } = 1.0f;

	[Export(PropertyHint.Range, "0,10,0.01,or_greater")]
	public float SourceAmplitude { get; set; } = 1.0f;

	[Export]
	public Vector3 Displacement { get; set; } = new(0.0f, 0.5f, 0.0f);


	internal override AnimatedWaveInputSnapshot CreateSnapshot(
		long registrationOrder,
		AnimatedWaveInputPlacement placement,
		float wavelengthMeters,
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


		float sourceAmplitude =
			float.IsFinite(SourceAmplitude)
				? Mathf.Max(SourceAmplitude, 0.0f)
				: 0.0f;


		return new AnimatedWaveInputSnapshot(
			Priority,
			registrationOrder,
			AnimatedWaveInputOperation.Displacement,
			placement,
			blendMode,
			weight,
			sourceAmplitude,
			wavelengthMeters,
			centerXZ,
			axisX,
			axisZ,
			sizeXZ,
			featherWidth,
			Displacement.IsFinite()
				? Displacement
				: Vector3.Zero,
			1.0f,
			false);
	}
}
