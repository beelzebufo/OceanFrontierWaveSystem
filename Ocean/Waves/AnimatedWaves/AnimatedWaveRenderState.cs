using System;
using System.Threading;
using Godot;

namespace OceanFrontier.Water.Waves.AnimatedWaves;

/// <summary>
/// Committed CPU-side spatial state for the canonical AnimatedWaveField.
///
/// Crest equivalent:
///
///     LodTransform.RenderData.Current
///         -> WriteCascadeParams()
///         -> all consumers use the same current LOD state
///
/// This object is NOT another source of LOD mathematics.
///
/// AnimatedWaveLodLayout remains the place where the spatial hierarchy
/// is calculated.
///
/// AnimatedWaveRenderState only publishes an immutable-in-practice snapshot
/// of the exact layout which was used to build the current
/// AnimatedWaveField.
///
/// Writer:
///
///     render thread
///
/// Reader:
///
///     main thread renderer / diagnostics
///
/// Contract:
///
///     AnimatedWaveField generation N
///     and
///     AnimatedWaveRenderState generation N
///
/// always describe the same spatial hierarchy.
///
/// No per-frame allocations.
/// No locks.
/// No GPU readback.
/// No recalculation of LOD slices.
/// </summary>
internal sealed class AnimatedWaveRenderState
{
	private const int MaxReadAttempts = 4;


	private readonly AnimatedWaveLodSlice[] _slices;


	//
	// Sequence lock.
	//
	// Even:
	//     stable published state.
	//
	// Odd:
	//     render thread is currently publishing.
	//
	// Reader accepts a snapshot only when the sequence value is unchanged
	// before and after copying.
	//

	private int _sequence;


	private long _generation;


	private int _resolution;
	private int _lodCount;

	private Vector2 _focusXZ;

	private float _worldScale;
	private float _lodScaleAlpha;
	private bool _hasSeaFloorDepth;


	public int Capacity =>
		_slices.Length;


	public AnimatedWaveRenderState(
		int lodCount)
	{
		if (lodCount <= 0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(lodCount));
		}


		_slices =
			new AnimatedWaveLodSlice[
				lodCount];
	}


	/// <summary>
	/// Publishes the exact spatial LOD state used by a completed
	/// AnimatedWaveField composition.
	///
	/// Must be called by the render thread after the direct-input and
	/// coarse-to-fine combine commands for this generation have been
	/// recorded.
	///
	/// The layout is copied. It is never exposed directly to readers.
	/// </summary>
	public void Publish(
		AnimatedWaveLodLayout layout,
		float lodScaleAlpha,
		bool hasSeaFloorDepth)
	{
		if (layout == null)
		{
			throw new ArgumentNullException(
				nameof(layout));
		}


		if (layout.LodCount !=
			_slices.Length)
		{
			throw new InvalidOperationException(
				"Animated Wave render-state LOD count does not match layout.");
		}


		if (!float.IsFinite(
				lodScaleAlpha))
		{
			throw new ArgumentOutOfRangeException(
				nameof(lodScaleAlpha));
		}


		lodScaleAlpha =
			Mathf.Clamp(
				lodScaleAlpha,
				0.0f,
				1.0f);


		//
		// Begin publish.
		//
		// Interlocked operation provides the required memory barrier and
		// makes the sequence odd.
		//

		Interlocked.Increment(
			ref _sequence);


		_resolution =
			layout.Resolution;

		_lodCount =
			layout.LodCount;

		_focusXZ =
			layout.FocusXZ;

		_worldScale =
			layout.WorldScale;

		_lodScaleAlpha =
			lodScaleAlpha;

		_hasSeaFloorDepth =
			hasSeaFloorDepth;


		for (int lod = 0;
			 lod < layout.LodCount;
			 lod++)
		{
			_slices[lod] =
				layout[lod];
		}


		//
		// Generation zero means:
		//
		//     no canonical field has been published yet.
		//

		_generation++;


		//
		// Ensure all state writes become visible before the sequence
		// returns to an even value.
		//

		Thread.MemoryBarrier();


		Interlocked.Increment(
			ref _sequence);
	}


	/// <summary>
	/// Copies one coherent committed AnimatedWaveField spatial snapshot.
	///
	/// Destination storage is owned by the caller and should normally be
	/// persistent for the lifetime of the consumer.
	///
	/// Returns false if:
	///
	/// - no field generation has been published yet;
	/// - destination is too small;
	/// - the render thread repeatedly publishes while this copy is running.
	///
	/// No allocation occurs.
	/// </summary>
	public bool TryCopy(
		Span<AnimatedWaveLodSlice> destination,
		out int resolution,
		out int lodCount,
		out Vector2 focusXZ,
		out float worldScale,
		out float lodScaleAlpha,
		out bool hasSeaFloorDepth,
		out long generation)
	{
		resolution =
			0;

		lodCount =
			0;

		focusXZ =
			default;

		worldScale =
			1.0f;

		lodScaleAlpha =
			0.0f;

		hasSeaFloorDepth =
			false;

		generation =
			0;


		if (destination.Length <
			_slices.Length)
		{
			return false;
		}


		for (int attempt = 0;
			 attempt < MaxReadAttempts;
			 attempt++)
		{
			int sequenceBefore =
				Volatile.Read(
					ref _sequence);


			//
			// Writer is currently publishing.
			//

			if ((sequenceBefore & 1) != 0)
			{
				continue;
			}


			long localGeneration =
				_generation;


			if (localGeneration <= 0)
			{
				return false;
			}


			int localResolution =
				_resolution;

			int localLodCount =
				_lodCount;

			Vector2 localFocusXZ =
				_focusXZ;

			float localWorldScale =
				_worldScale;

			float localLodScaleAlpha =
				_lodScaleAlpha;

			bool localHasSeaFloorDepth =
				_hasSeaFloorDepth;


			for (int lod = 0;
				 lod < localLodCount;
				 lod++)
			{
				destination[lod] =
					_slices[lod];
			}


			//
			// Prevent the compiler / CPU from moving snapshot reads across
			// the final sequence validation.
			//

			Thread.MemoryBarrier();


			int sequenceAfter =
				Volatile.Read(
					ref _sequence);


			if (sequenceBefore !=
					sequenceAfter ||
				(sequenceAfter & 1) != 0)
			{
				continue;
			}


			//
			// Coherent snapshot accepted.
			//

			resolution =
				localResolution;

			lodCount =
				localLodCount;

			focusXZ =
				localFocusXZ;

			worldScale =
				localWorldScale;

			lodScaleAlpha =
				localLodScaleAlpha;

			hasSeaFloorDepth =
				localHasSeaFloorDepth;

			generation =
				localGeneration;


			return true;
		}


		return false;
	}
}
