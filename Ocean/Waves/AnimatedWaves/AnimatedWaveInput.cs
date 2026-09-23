using Godot;

namespace OceanFrontier.Water.Waves.AnimatedWaves;

/// <summary>
/// Selects when an Animated Waves input participates in composition.
///
/// Crest 4 mapping at db0658ff0b2e93e4a9e28cc2867509658b0ecc00:
/// RegisterAnimWavesInput.Wavelength &gt; 0 / &lt; 0 / == 0.
/// The explicit enum avoids exposing those sentinel values.
/// </summary>
public enum AnimatedWaveInputPlacement
{
	WavelengthFilteredPreCombine = 0,
	AllLodsPreCombine = 1,
	AllLodsPostCombine = 2,
}


public enum AnimatedWaveInputBlendMode
{
	Additive = 0,
	Blend = 1,
}


public enum AnimatedWaveInputOperation
{
	Displacement = 0,
	ScaleByFactor = 1,
	DirectionalFft = 2,
}


/// <summary>
/// Common public input contract. Operation-specific scene components add only
/// meaningful properties; renderer and physics continue to consume only the
/// composed AnimatedWaveField.
/// </summary>
public interface IAnimatedWaveInput
{
	bool Enabled { get; }
	int Priority { get; }
	Vector2 SizeXZ { get; }
	float FeatherWidth { get; }
}


internal interface IAnimatedWaveInputSnapshotSource : IAnimatedWaveInput
{
	string DiagnosticName { get; }

	bool TryCapture(
		long registrationOrder,
		out AnimatedWaveInputSnapshot snapshot);
}


/// <summary>
/// Allocation-free main-thread snapshot consumed by the render thread.
/// </summary>
internal readonly struct AnimatedWaveInputSnapshot
{
	public readonly int Priority;
	public readonly long RegistrationOrder;
	public readonly AnimatedWaveInputOperation Operation;
	public readonly AnimatedWaveInputPlacement Placement;
	public readonly AnimatedWaveInputBlendMode BlendMode;
	public readonly float Weight;
	public readonly float SourceAmplitude;
	public readonly float WavelengthMeters;
	public readonly Vector2 CenterXZ;
	public readonly Vector2 AxisX;
	public readonly Vector2 AxisZ;
	public readonly Vector2 SizeXZ;
	public readonly float FeatherWidth;
	public readonly Vector3 Displacement;
	public readonly float Scale;
	public readonly bool Invert;
	public readonly float DirectionRadians;


	public AnimatedWaveInputSnapshot(
		int priority,
		long registrationOrder,
		AnimatedWaveInputOperation operation,
		AnimatedWaveInputPlacement placement,
		AnimatedWaveInputBlendMode blendMode,
		float weight,
		float sourceAmplitude,
		float wavelengthMeters,
		Vector2 centerXZ,
		Vector2 axisX,
		Vector2 axisZ,
		Vector2 sizeXZ,
		float featherWidth,
		Vector3 displacement,
		float scale,
		bool invert,
		float directionRadians)
	{
		Priority = priority;
		RegistrationOrder = registrationOrder;
		Operation = operation;
		Placement = placement;
		BlendMode = blendMode;
		Weight = weight;
		SourceAmplitude = sourceAmplitude;
		WavelengthMeters = wavelengthMeters;
		CenterXZ = centerXZ;
		AxisX = axisX;
		AxisZ = axisZ;
		SizeXZ = sizeXZ;
		FeatherWidth = featherWidth;
		Displacement = displacement;
		Scale = scale;
		Invert = invert;
		DirectionRadians = directionRadians;
	}
}
