using System.Threading;
using Godot;

namespace OceanFrontier.Water.Waves.Foam;

/// <summary>
/// Coherently publishes the persistent foam texture together with the exact
/// AnimatedWaveRenderState generation whose spatial layout it represents.
/// Owns no GPU resources.
/// </summary>
internal sealed class OceanFoamRenderState
{
	private const int MaxReadAttempts = 4;

	private int _sequence;
	private Rid _texture;
	private long _animatedWaveGeneration;
	private bool _enabled;

	public void Publish(
		Rid texture,
		long animatedWaveGeneration,
		bool enabled)
	{
		Interlocked.Increment(
			ref _sequence);


		_texture =
			texture;

		_animatedWaveGeneration =
			animatedWaveGeneration;

		_enabled =
			enabled;


		Thread.MemoryBarrier();


		Interlocked.Increment(
			ref _sequence);
	}

	public void Invalidate() =>
		Publish(
			default,
			0,
			false);

	public bool TryGetTexture(
		long expectedAnimatedWaveGeneration,
		out Rid texture)
	{
		texture =
			default;


		for (int attempt = 0;
			 attempt < MaxReadAttempts;
			 attempt++)
		{
			int sequenceBefore =
				Volatile.Read(
					ref _sequence);


			if ((sequenceBefore & 1) != 0)
			{
				continue;
			}


			Rid localTexture =
				_texture;

			long localGeneration =
				_animatedWaveGeneration;

			bool localEnabled =
				_enabled;


			Thread.MemoryBarrier();


			int sequenceAfter =
				Volatile.Read(
					ref _sequence);


			if (sequenceBefore != sequenceAfter ||
				(sequenceAfter & 1) != 0)
			{
				continue;
			}


			if (!localEnabled ||
				!localTexture.IsValid ||
				localGeneration != expectedAnimatedWaveGeneration)
			{
				return false;
			}


			texture =
				localTexture;


			return true;
		}


		return false;
	}
}
