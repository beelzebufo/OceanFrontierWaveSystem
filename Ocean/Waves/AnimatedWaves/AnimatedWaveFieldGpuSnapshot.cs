using Godot;

namespace OceanFrontier.Water.Waves.AnimatedWaves;

/// <summary>
/// Immutable render-thread view of one persistent canonical AWF resource
/// generation. The composer owns both RIDs; consumers only borrow them.
/// </summary>
internal readonly struct AnimatedWaveFieldGpuSnapshot
{
	internal readonly Rid AnimatedWaveField;
	internal readonly Rid LodMetadataBuffer;
	internal readonly int Resolution;
	internal readonly int LodCount;
	internal readonly long Generation;


	internal AnimatedWaveFieldGpuSnapshot(
		Rid animatedWaveField,
		Rid lodMetadataBuffer,
		int resolution,
		int lodCount,
		long generation)
	{
		AnimatedWaveField = animatedWaveField;
		LodMetadataBuffer = lodMetadataBuffer;
		Resolution = resolution;
		LodCount = lodCount;
		Generation = generation;
	}


	internal bool IsValid =>
		AnimatedWaveField.IsValid &&
		LodMetadataBuffer.IsValid &&
		Resolution > 0 &&
		LodCount > 0 &&
		Generation > 0;
}


/// <summary>
/// Render-thread-only consumer contract. Clearing must happen before the
/// composer frees a resource generation.
/// </summary>
internal interface IAnimatedWaveFieldGpuConsumer
{
	void SetAnimatedWaveFieldGpuSnapshot(
		AnimatedWaveFieldGpuSnapshot snapshot);

	void ClearAnimatedWaveFieldGpuSnapshot();
}
