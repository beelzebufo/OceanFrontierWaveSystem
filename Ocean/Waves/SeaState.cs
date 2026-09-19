using Godot;

namespace OceanFrontier.Water.Waves;

[GlobalClass]
public partial class SeaState : Resource
{
	[Export(PropertyHint.Range, "0.0,80.0,0.1,or_greater")]
	public float WindSpeedMetersPerSecond { get; set; } = 12.0f;

	[Export(PropertyHint.Range, "-180.0,180.0,0.1")]
	public float WindDirectionDegrees { get; set; } = 0.0f;

	[Export(PropertyHint.Range, "0.0,1.0,0.001")]
	public float WindTurbulence { get; set; } = 0.145f;

	[Export(PropertyHint.Range, "0.01,30.0,0.01,or_greater")]
	public float Gravity { get; set; } = 9.81f;

	[Export(PropertyHint.Range, "0.0,4.0,0.01,or_greater")]
	public float Chop { get; set; } = 1.6f;

	/// <summary>
	/// 0 = no forced temporal looping.
	/// Used by spectral dispersion quantization.
	/// </summary>
	[Export(PropertyHint.Range, "0.0,1000.0,0.1,or_greater")]
	public float LoopPeriodSeconds { get; set; } = 0.0f;
}
