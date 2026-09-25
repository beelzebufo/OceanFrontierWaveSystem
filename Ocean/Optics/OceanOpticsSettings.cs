using System;
using Godot;

namespace OceanFrontier.Water.Optics;

/// <summary>
/// Serialized authority for the physical optical state of one water medium.
/// Consumers receive value snapshots through OceanRuntime and never read this
/// mutable Resource from the render thread.
/// </summary>
[GlobalClass]
public partial class OceanOpticsSettings : Resource
{
	public const float DefaultWaterIor = 1.333f;

	public static readonly Vector3 DefaultExtinction =
		new(0.18f, 0.07f, 0.025f);

	public static readonly Vector3 DefaultDeepScatterColor =
		new(0.015f, 0.12f, 0.18f);


	[Export(PropertyHint.Range, "1,3,0.001,or_greater")]
	public float WaterIor { get; set; } =
		DefaultWaterIor;


	[Export]
	public Vector3 Extinction { get; set; } =
		DefaultExtinction;


	[Export]
	public Vector3 DeepScatterColor { get; set; } =
		DefaultDeepScatterColor;


	internal OceanOpticsState Snapshot() =>
		new(
			SanitizeIor(
				WaterIor),
			SanitizeNonNegative(
				Extinction,
				DefaultExtinction),
			SanitizeNonNegative(
				DeepScatterColor,
				DefaultDeepScatterColor));


	internal static OceanOpticsState DefaultState =>
		new(
			DefaultWaterIor,
			DefaultExtinction,
			DefaultDeepScatterColor);


	private static float SanitizeIor(
		float value) =>
		float.IsFinite(
			value)
			? MathF.Max(
				1.0f,
				value)
			: DefaultWaterIor;


	private static Vector3 SanitizeNonNegative(
		Vector3 value,
		Vector3 fallback) =>
		new(
			SanitizeComponent(
				value.X,
				fallback.X),
			SanitizeComponent(
				value.Y,
				fallback.Y),
			SanitizeComponent(
				value.Z,
				fallback.Z));


	private static float SanitizeComponent(
		float value,
		float fallback) =>
		float.IsFinite(
			value)
			? MathF.Max(
				0.0f,
				value)
			: fallback;
}


/// <summary>Immutable, allocation-free main-thread optical snapshot.</summary>
internal readonly struct OceanOpticsState : IEquatable<OceanOpticsState>
{
	internal readonly float WaterIor;
	internal readonly Vector3 Extinction;
	internal readonly Vector3 DeepScatterColor;


	internal OceanOpticsState(
		float waterIor,
		Vector3 extinction,
		Vector3 deepScatterColor)
	{
		WaterIor =
			waterIor;

		Extinction =
			extinction;

		DeepScatterColor =
			deepScatterColor;
	}


	public bool Equals(
		OceanOpticsState other) =>
		WaterIor ==
			other.WaterIor &&
		Extinction ==
			other.Extinction &&
		DeepScatterColor ==
			other.DeepScatterColor;


	public override bool Equals(
		object obj) =>
		obj is OceanOpticsState other &&
		Equals(
			other);


	public override int GetHashCode() =>
		HashCode.Combine(
			WaterIor,
			Extinction,
			DeepScatterColor);
}
