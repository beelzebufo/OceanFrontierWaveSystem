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


/// <summary>
/// Public Stage 5A-1 input contract. Implementations snapshot these values on
/// the main thread; renderer and physics continue to consume only the composed
/// AnimatedWaveField.
/// </summary>
public interface IAnimatedWaveInput
{
	bool Enabled { get; }
	int Priority { get; }
	AnimatedWaveInputPlacement Placement { get; }
	AnimatedWaveInputBlendMode BlendMode { get; }
	float Weight { get; }
	float SourceAmplitude { get; }
	float WavelengthMeters { get; }
	Vector2 SizeXZ { get; }
	float FeatherWidth { get; }
	Vector3 Displacement { get; }
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


	public AnimatedWaveInputSnapshot(
		int priority,
		long registrationOrder,
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
		Vector3 displacement)
	{
		Priority = priority;
		RegistrationOrder = registrationOrder;
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
	}
}
