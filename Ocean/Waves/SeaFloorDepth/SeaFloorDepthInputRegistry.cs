using System;
using Godot;

namespace OceanFrontier.Water.Waves.SeaFloorDepth;

internal sealed class SeaFloorDepthInputRegistry
{
	public const int Capacity = 64;

	private readonly object _snapshotLock = new();
	private readonly ISeaFloorDepthInputSnapshotSource[] _registered =
		new ISeaFloorDepthInputSnapshotSource[Capacity];
	private readonly SeaFloorDepthInputSnapshot[] _capture =
		new SeaFloorDepthInputSnapshot[Capacity];
	private readonly SeaFloorDepthInputSnapshot[] _published =
		new SeaFloorDepthInputSnapshot[Capacity];

	private int _registeredCount;
	private int _publishedCount;

	public void Register(ISeaFloorDepthInputSnapshotSource input)
	{
		ArgumentNullException.ThrowIfNull(input);
		for (int i = 0; i < _registeredCount; i++)
		{
			if (ReferenceEquals(_registered[i], input)) return;
		}

		if (_registeredCount >= Capacity)
		{
			GD.PushError($"Sea floor depth input capacity {Capacity} exceeded; '{input.DiagnosticName}' was not registered.");
			return;
		}

		_registered[_registeredCount++] = input;
	}

	public void Unregister(ISeaFloorDepthInputSnapshotSource input)
	{
		for (int i = 0; i < _registeredCount; i++)
		{
			if (!ReferenceEquals(_registered[i], input)) continue;
			int move = _registeredCount - i - 1;
			if (move > 0) Array.Copy(_registered, i + 1, _registered, i, move);
			_registered[--_registeredCount] = null;
			return;
		}
	}

	public void CaptureMainThread()
	{
		int count = 0;
		for (int i = 0; i < _registeredCount; i++)
		{
			ISeaFloorDepthInputSnapshotSource input = _registered[i];
			if (input != null && input.TryCapture(out SeaFloorDepthInputSnapshot snapshot))
				_capture[count++] = snapshot;
		}

		lock (_snapshotLock)
		{
			Array.Copy(_capture, _published, count);
			_publishedCount = count;
		}
	}

	public int CopyLatest(SeaFloorDepthInputSnapshot[] destination)
	{
		if (destination == null || destination.Length < Capacity)
			throw new ArgumentException($"Destination must hold {Capacity} inputs.", nameof(destination));

		lock (_snapshotLock)
		{
			Array.Copy(_published, destination, _publishedCount);
			return _publishedCount;
		}
	}
}
