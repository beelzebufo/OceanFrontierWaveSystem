using System;
using Godot;

namespace OceanFrontier.Water.Waves.AnimatedWaves;

/// <summary>Immutable description of a finite radial source. Times use OceanRuntime.OceanTime.
/// Create/update through OceanRuntime; GPU composition evaluates all displacement.</summary>
public sealed record GerstnerWavePacketInput
{
	public const int Capacity = 8;
	public const float MaximumAmplitude = 4.0f;
	public const float MaximumCombinedDisplacement = Capacity * MaximumAmplitude;

	/// <summary>Outward sector axis in world XZ (+X = 0°, +Z = 90°). Normalized during packing.</summary>
	public Vector2 DirectionXZ { get; init; } = Vector2.Right;
	/// <summary>180 bypasses angular attenuation, preserving the original radial packet.</summary>
	public float SectorHalfAngleDegrees { get; init; } = 180.0f;
	/// <summary>Feather immediately inside each sector boundary. Zero requests a hard edge.</summary>
	public float AngularFeatherDegrees { get; init; } = 10.0f;

	public Vector2 WorldPositionXZ { get; init; }
	public float StartTime { get; init; }
	public float Amplitude { get; init; } = 0.5f;
	public float Wavelength { get; init; } = 8.0f;
	public int CrestCount { get; init; } = 4;
	public float Chop { get; init; } = 0.5f;
	public float InitialPhase { get; init; }
	public float Lifetime { get; init; } = 40.0f;
	public float FadeIn { get; init; } = 1.0f;
	public float FadeOut { get; init; } = 5.0f;
	public float AttenuationStrength { get; init; } = 0.95f;
	public bool Enabled { get; init; } = true;

	internal void Validate()
	{
		if (!DirectionXZ.IsFinite() || (DirectionXZ.X == 0 && DirectionXZ.Y == 0))
			throw new ArgumentException("Packet direction must be finite and nonzero.", nameof(DirectionXZ));
		FiniteRange(SectorHalfAngleDegrees, 1, 180, nameof(SectorHalfAngleDegrees));
		FiniteRange(AngularFeatherDegrees, 0, SectorHalfAngleDegrees, nameof(AngularFeatherDegrees));
		if (!WorldPositionXZ.IsFinite()) throw new ArgumentException("Packet position must be finite.");
		FiniteRange(StartTime, 0, float.MaxValue, nameof(StartTime));
		FiniteRange(Amplitude, 0, MaximumAmplitude, nameof(Amplitude));
		// Bounded authoring ranges keep intermediate float arithmetic and half-float output finite.
		FiniteRange(Wavelength, 0.1f, 10000, nameof(Wavelength));
		if (CrestCount < 3 || CrestCount > 5) throw new ArgumentOutOfRangeException(nameof(CrestCount));
		FiniteRange(Chop, 0, 1, nameof(Chop));
		FiniteRange(InitialPhase, -Mathf.Tau, Mathf.Tau, nameof(InitialPhase));
		FiniteRange(Lifetime, 0.01f, 3600, nameof(Lifetime));
		FiniteRange(FadeIn, 0.01f, Lifetime, nameof(FadeIn));
		FiniteRange(FadeOut, 0.01f, Lifetime, nameof(FadeOut));
		FiniteRange(AttenuationStrength, 0, 1, nameof(AttenuationStrength));
	}

	private static void FiniteRange(float value, float min, float max, string name)
	{
		if (!float.IsFinite(value) || value < min || value > max)
			throw new ArgumentOutOfRangeException(name, $"Expected a finite value in [{min}, {max}].");
	}
}
