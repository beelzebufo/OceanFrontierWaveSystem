using System;
using Godot;

namespace OceanFrontier.Water.Waves.Foam;

/// <summary>
/// Persistent foam simulation settings.
/// The diagnostic spot is deliberately a one-shot validation aid, not a
/// production foam-input API.
/// </summary>
[GlobalClass]
public partial class OceanFoamSettings : Resource
{
	public const float DefaultSimulationFrequency = 30.0f;
	public const float DefaultFadeRate = 0.8f;
	public const float DefaultWaveFoamStrength = 1.0f;
	public const float DefaultWaveFoamCoverage = 0.55f;
	public const float DefaultShorelineFoamStrength = 2.0f;
	public const float DefaultShorelineFoamMaxDepth = 0.65f;

	[Export]
	public bool Enabled { get; set; } = true;

	[Export(PropertyHint.Range, "1,200,1,or_greater")]
	public float SimulationFrequency { get; set; } =
		DefaultSimulationFrequency;

	[Export(PropertyHint.Range, "0,20,0.01,or_greater")]
	public float FadeRate { get; set; } =
		DefaultFadeRate;

	[ExportGroup("Whitecaps")]
	[Export(PropertyHint.Range, "0,5,0.01")]
	public float WaveFoamStrength { get; set; } =
		DefaultWaveFoamStrength;

	[Export(PropertyHint.Range, "0,1,0.01")]
	public float WaveFoamCoverage { get; set; } =
		DefaultWaveFoamCoverage;

	[ExportGroup("Shoreline")]
	[Export(PropertyHint.Range, "0,10,0.01,or_greater")]
	public float ShorelineFoamStrength { get; set; } =
		DefaultShorelineFoamStrength;

	[Export(PropertyHint.Range, "0.01,20,0.01,or_greater")]
	public float ShorelineFoamMaxDepth { get; set; } =
		DefaultShorelineFoamMaxDepth;

	[ExportGroup("Foam-1A Diagnostic")]
	[Export]
	public bool InjectWorldSpaceSpot { get; set; }

	[Export]
	public Vector2 DiagnosticSpotWorldXZ { get; set; } =
		Vector2.Zero;

	[Export(PropertyHint.Range, "0.01,100,0.01,or_greater")]
	public float DiagnosticSpotRadius { get; set; } = 2.0f;

	[Export(PropertyHint.Range, "0,1,0.01")]
	public float DiagnosticSpotAmount { get; set; } = 1.0f;

	internal OceanFoamState Snapshot() =>
		new(
			Enabled,
			SanitizePositive(
				SimulationFrequency,
				DefaultSimulationFrequency),
			SanitizeNonNegative(
				FadeRate,
				DefaultFadeRate),
			SanitizeRange(
				WaveFoamStrength,
				0.0f,
				5.0f,
				DefaultWaveFoamStrength),
			SanitizeRange(
				WaveFoamCoverage,
				0.0f,
				1.0f,
				DefaultWaveFoamCoverage),
			SanitizeNonNegative(
				ShorelineFoamStrength,
				DefaultShorelineFoamStrength),
			SanitizePositive(
				ShorelineFoamMaxDepth,
				DefaultShorelineFoamMaxDepth),
			InjectWorldSpaceSpot,
			DiagnosticSpotWorldXZ,
			SanitizePositive(
				DiagnosticSpotRadius,
				2.0f),
			Mathf.Clamp(
				float.IsFinite(DiagnosticSpotAmount)
					? DiagnosticSpotAmount
					: 1.0f,
				0.0f,
				1.0f));

	internal static OceanFoamState DefaultState =>
		new(
			true,
			DefaultSimulationFrequency,
			DefaultFadeRate,
			DefaultWaveFoamStrength,
			DefaultWaveFoamCoverage,
			DefaultShorelineFoamStrength,
			DefaultShorelineFoamMaxDepth,
			false,
			Vector2.Zero,
			2.0f,
			1.0f);

	private static float SanitizePositive(
		float value,
		float fallback) =>
		float.IsFinite(value) && value > 0.0f
			? value
			: fallback;

	private static float SanitizeNonNegative(
		float value,
		float fallback) =>
		float.IsFinite(value)
			? MathF.Max(0.0f, value)
			: fallback;

	private static float SanitizeRange(
		float value,
		float minimum,
		float maximum,
		float fallback) =>
		Mathf.Clamp(
			float.IsFinite(value)
				? value
				: fallback,
			minimum,
			maximum);
}


/// <summary>Immutable, allocation-free main-thread foam snapshot.</summary>
internal readonly struct OceanFoamState
{
	internal readonly bool Enabled;
	internal readonly float SimulationFrequency;
	internal readonly float FadeRate;
	internal readonly float WaveFoamStrength;
	internal readonly float WaveFoamCoverage;
	internal readonly float ShorelineFoamStrength;
	internal readonly float ShorelineFoamMaxDepth;
	internal readonly bool InjectWorldSpaceSpot;
	internal readonly Vector2 DiagnosticSpotWorldXZ;
	internal readonly float DiagnosticSpotRadius;
	internal readonly float DiagnosticSpotAmount;

	internal OceanFoamState(
		bool enabled,
		float simulationFrequency,
		float fadeRate,
		float waveFoamStrength,
		float waveFoamCoverage,
		float shorelineFoamStrength,
		float shorelineFoamMaxDepth,
		bool injectWorldSpaceSpot,
		Vector2 diagnosticSpotWorldXZ,
		float diagnosticSpotRadius,
		float diagnosticSpotAmount)
	{
		Enabled = enabled;
		SimulationFrequency = simulationFrequency;
		FadeRate = fadeRate;
		WaveFoamStrength = waveFoamStrength;
		WaveFoamCoverage = waveFoamCoverage;
		ShorelineFoamStrength = shorelineFoamStrength;
		ShorelineFoamMaxDepth = shorelineFoamMaxDepth;
		InjectWorldSpaceSpot = injectWorldSpaceSpot;
		DiagnosticSpotWorldXZ = diagnosticSpotWorldXZ;
		DiagnosticSpotRadius = diagnosticSpotRadius;
		DiagnosticSpotAmount = diagnosticSpotAmount;
	}
}
