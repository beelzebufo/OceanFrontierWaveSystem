using System;
using Godot;

namespace OceanFrontier.Water.Waves.Foam;

/// <summary>
/// Fixed-capacity main-thread registry with stable priority ordering and a
/// coherent allocation-free snapshot for the render thread.
/// </summary>
internal sealed class OceanFoamInputRegistry
{
	public const int Capacity = 16;

	private readonly object _snapshotLock = new();
	private readonly IOceanFoamInputSnapshotSource[] _registered =
		new IOceanFoamInputSnapshotSource[Capacity];
	private readonly long[] _registrationOrders =
		new long[Capacity];
	private readonly OceanFoamInputSnapshot[] _capture =
		new OceanFoamInputSnapshot[Capacity];
	private readonly OceanFoamInputSnapshot[] _published =
		new OceanFoamInputSnapshot[Capacity];

	private int _registeredCount;
	private int _publishedCount;
	private long _nextRegistrationOrder = 1;

	internal void Register(
		IOceanFoamInputSnapshotSource input)
	{
		ArgumentNullException.ThrowIfNull(input);


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


		if (_registeredCount >= Capacity)
		{
			GD.PushError(
				$"Ocean Foam input capacity {Capacity} exceeded; " +
				$"'{input.DiagnosticName}' was not registered.");
			return;
		}


		_registered[_registeredCount] =
			input;
		_registrationOrders[_registeredCount] =
			_nextRegistrationOrder++;
		_registeredCount++;
	}

	internal void Unregister(
		IOceanFoamInputSnapshotSource input)
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
			_registered[_registeredCount] = null;
			_registrationOrders[_registeredCount] = 0;
			return;
		}
	}

	/// <summary>Main thread only.</summary>
	internal void CaptureMainThread()
	{
		int count = 0;


		for (int index = 0;
			 index < _registeredCount;
			 index++)
		{
			IOceanFoamInputSnapshotSource input =
				_registered[index];


			if (input != null &&
				input.TryCapture(
					_registrationOrders[index],
					out OceanFoamInputSnapshot snapshot))
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

	/// <summary>Render thread only.</summary>
	internal int CopyLatest(
		OceanFoamInputSnapshot[] destination)
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
		OceanFoamInputSnapshot[] values,
		int count)
	{
		for (int index = 1;
			 index < count;
			 index++)
		{
			OceanFoamInputSnapshot value =
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
		OceanFoamInputSnapshot left,
		OceanFoamInputSnapshot right)
	{
		if (left.Priority != right.Priority)
		{
			return left.Priority > right.Priority;
		}


		return left.RegistrationOrder >
			right.RegistrationOrder;
	}
}
