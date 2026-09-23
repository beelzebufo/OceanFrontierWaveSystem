using System;
using Godot;

namespace OceanFrontier.Water.Waves.AnimatedWaves;

/// <summary>
/// Fixed-capacity main-thread registry with a coherent persistent snapshot.
/// Node properties are captured only on the main thread. The render thread
/// copies the published snapshot under a short lock into caller-owned storage.
/// </summary>
internal sealed class AnimatedWaveInputRegistry
{
	public const int Capacity = 64;


	private readonly object _snapshotLock = new();

	private readonly IAnimatedWaveInputSnapshotSource[] _registered =
		new IAnimatedWaveInputSnapshotSource[Capacity];

	private readonly long[] _registrationOrders =
		new long[Capacity];

	private readonly AnimatedWaveInputSnapshot[] _capture =
		new AnimatedWaveInputSnapshot[Capacity];

	private readonly AnimatedWaveInputSnapshot[] _published =
		new AnimatedWaveInputSnapshot[Capacity];


	private int _registeredCount;
	private int _publishedCount;
	private long _nextRegistrationOrder = 1;


	public void Register(
		IAnimatedWaveInputSnapshotSource input)
	{
		if (input == null)
		{
			throw new ArgumentNullException(
				nameof(input));
		}


		for (int index = 0;
			 index < _registeredCount;
			 index++)
		{
			if (ReferenceEquals(
					_registered[index],
					input))
			{
				return;
			}
		}


		if (_registeredCount >=
			Capacity)
		{
			GD.PushError(
				$"Animated Wave input capacity {Capacity} exceeded; " +
				$"'{input.DiagnosticName}' was not registered.");

			return;
		}


		_registered[_registeredCount] =
			input;

		_registrationOrders[_registeredCount] =
			_nextRegistrationOrder++;

		_registeredCount++;
	}


	public void Unregister(
		IAnimatedWaveInputSnapshotSource input)
	{
		for (int index = 0;
			 index < _registeredCount;
			 index++)
		{
			if (!ReferenceEquals(
					_registered[index],
					input))
			{
				continue;
			}


			int moveCount =
				_registeredCount -
				index -
				1;


			if (moveCount > 0)
			{
				Array.Copy(
					_registered,
					index + 1,
					_registered,
					index,
					moveCount);


				Array.Copy(
					_registrationOrders,
					index + 1,
					_registrationOrders,
					index,
					moveCount);
			}


			_registeredCount--;

			_registered[_registeredCount] =
				null;

			_registrationOrders[_registeredCount] =
				0;


			return;
		}
	}


	/// <summary>
	/// Main thread only.
	/// </summary>
	public void CaptureMainThread()
	{
		int count =
			0;


		for (int index = 0;
			 index < _registeredCount;
			 index++)
		{
			IAnimatedWaveInputSnapshotSource input =
				_registered[index];


			if (input != null &&
				input.TryCapture(
					_registrationOrders[index],
					out AnimatedWaveInputSnapshot snapshot))
			{
				_capture[count++] =
					snapshot;
			}
		}


		StableSort(
			_capture,
			count);


		lock (_snapshotLock)
		{
			Array.Copy(
				_capture,
				_published,
				count);


			_publishedCount =
				count;
		}
	}


	/// <summary>
	/// Render thread only. Destination is persistent caller storage.
	/// </summary>
	public int CopyLatest(
		AnimatedWaveInputSnapshot[] destination)
	{
		if (destination == null ||
			destination.Length < Capacity)
		{
			throw new ArgumentException(
				$"Destination must hold {Capacity} inputs.",
				nameof(destination));
		}


		lock (_snapshotLock)
		{
			Array.Copy(
				_published,
				destination,
				_publishedCount);


			return _publishedCount;
		}
	}


	private static void StableSort(
		AnimatedWaveInputSnapshot[] values,
		int count)
	{
		for (int index = 1;
			 index < count;
			 index++)
		{
			AnimatedWaveInputSnapshot value =
				values[index];


			int insert =
				index - 1;


			while (insert >= 0 &&
				ComesAfter(
					values[insert],
					value))
			{
				values[insert + 1] =
					values[insert];


				insert--;
			}


			values[insert + 1] =
				value;
		}
	}


	private static bool ComesAfter(
		AnimatedWaveInputSnapshot left,
		AnimatedWaveInputSnapshot right)
	{
		if (left.Priority !=
			right.Priority)
		{
			return left.Priority >
				right.Priority;
		}


		return left.RegistrationOrder >
			right.RegistrationOrder;
	}
}
