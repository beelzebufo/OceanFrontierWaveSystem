using Godot;

namespace OceanFrontier.Water.Waves.AnimatedWaves;

/// <summary>
/// Rectangle input whose one contribution has an explicit Animated Waves
/// placement and wavelength. Directional FFT sources derive their wavelengths
/// from the reused FFT cascades and therefore do not use this contract.
/// </summary>
public abstract partial class AnimatedWaveRectPlacedInputBase :
	AnimatedWaveRectInputBase
{
	[Export]
	public AnimatedWaveInputPlacement Placement { get; set; } =
		AnimatedWaveInputPlacement.AllLodsPostCombine;

	[Export(PropertyHint.Range, "0.001,8192,0.001,or_greater")]
	public float WavelengthMeters { get; set; } = 1.0f;


	internal sealed override AnimatedWaveInputSnapshot CreateSnapshot(
		long registrationOrder,
		Vector2 centerXZ,
		Vector2 axisX,
		Vector2 axisZ,
		Vector2 sizeXZ,
		float featherWidth)
	{
		AnimatedWaveInputPlacement placement =
			Placement is
				AnimatedWaveInputPlacement.WavelengthFilteredPreCombine or
				AnimatedWaveInputPlacement.AllLodsPreCombine or
				AnimatedWaveInputPlacement.AllLodsPostCombine
					? Placement
					: AnimatedWaveInputPlacement.AllLodsPostCombine;


		float wavelength =
			float.IsFinite(WavelengthMeters)
				? Mathf.Max(WavelengthMeters, 0.001f)
				: 0.001f;


		return CreatePlacedSnapshot(
			registrationOrder,
			placement,
			wavelength,
			centerXZ,
			axisX,
			axisZ,
			sizeXZ,
			featherWidth);
	}


	internal abstract AnimatedWaveInputSnapshot CreatePlacedSnapshot(
		long registrationOrder,
		AnimatedWaveInputPlacement placement,
		float wavelengthMeters,
		Vector2 centerXZ,
		Vector2 axisX,
		Vector2 axisZ,
		Vector2 sizeXZ,
		float featherWidth);
}
