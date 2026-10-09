using System;
using Godot;
using OceanFrontier.Water.Waves.AnimatedWaves;

namespace OceanFrontier.Water.Runtime;

public partial class OceanRuntime
{
	private readonly PacketSource[] _packets = new PacketSource[GerstnerWavePacketInput.Capacity];
	private long _nextPacketId = 1;

	/// <summary>Main-thread ocean simulation seconds, including pause and time scale.</summary>
	public float OceanTime => SimulationTime;
	public int GerstnerPacketCount { get; private set; }

	/// <summary>Trigger a default packet now at any world XZ. Main thread only.</summary>
	public long TriggerGerstnerPacket(Vector2 worldPositionXZ) =>
		CreateGerstnerPacket(new GerstnerWavePacketInput { WorldPositionXZ = worldPositionXZ, StartTime = OceanTime });

	/// <summary>Returns a handle, or zero when either bounded source registry is full.</summary>
	public long CreateGerstnerPacket(GerstnerWavePacketInput input)
	{
		ArgumentNullException.ThrowIfNull(input);
		input.Validate();
		ExpireGerstnerPackets();
		for (int i = 0; i < _packets.Length; i++)
		{
			if (_packets[i] != null) continue;
			var source = new PacketSource(_nextPacketId++, input);
			if (!_animatedWaveInputs.Register(source)) return 0;
			_packets[i] = source;
			GerstnerPacketCount++;
			return source.Id;
		}
		return 0;
	}

	/// <summary>Replace parameters atomically for the next snapshot. Expired handles cannot be revived.</summary>
	public bool UpdateGerstnerPacket(long id, GerstnerWavePacketInput input)
	{
		ArgumentNullException.ThrowIfNull(input);
		input.Validate();
		ExpireGerstnerPackets();
		foreach (PacketSource source in _packets)
			if (source != null && source.Id == id) { source.Input = input; return true; }
		return false;
	}

	public bool RemoveGerstnerPacket(long id)
	{
		for (int i = 0; i < _packets.Length; i++)
			if (_packets[i] != null && _packets[i].Id == id)
			{
				_animatedWaveInputs.Unregister(_packets[i]);
				_packets[i] = null;
				GerstnerPacketCount--;
				return true;
			}
		return false;
	}

	public void ClearGerstnerPackets()
	{
		for (int i = 0; i < _packets.Length; i++)
			if (_packets[i] != null) RemoveGerstnerPacket(_packets[i].Id);
	}

	private void ExpireGerstnerPackets()
	{
		foreach (PacketSource source in _packets)
		{
			if (source == null) continue;
			float elapsed = OceanTime - source.Input.StartTime;
			if (elapsed >= source.Input.Lifetime) RemoveGerstnerPacket(source.Id);
			else source.Active = elapsed >= 0;
		}
	}

	private sealed class PacketSource : IAnimatedWaveInputSnapshotSource
	{
		internal readonly long Id;
		internal GerstnerWavePacketInput Input;
		internal bool Active;
		internal PacketSource(long id, GerstnerWavePacketInput input) { Id = id; Input = input; }
		public bool Enabled => Input.Enabled;
		// Add after ordinary pre-combine modifiers; existing post-combine modifiers still apply.
		public int Priority => int.MaxValue;
		// Rectangle-only metadata is unused by the radial shader operation.
		public Vector2 SizeXZ => Vector2.Zero;
		public float FeatherWidth => 0;
		public string DiagnosticName => "Gerstner wave packet";
		public bool TryCapture(long order, out AnimatedWaveInputSnapshot snapshot)
		{
			snapshot = new AnimatedWaveInputSnapshot(Priority, order,
				AnimatedWaveInputOperation.RadialGerstnerPacket,
				AnimatedWaveInputPlacement.WavelengthFilteredPreCombine,
				AnimatedWaveInputBlendMode.Additive, 1, Input.Amplitude, Input.Wavelength,
				Input.WorldPositionXZ, Vector2.Zero, Vector2.Zero, Vector2.Zero, 0,
				Vector3.Zero, 1, false, 0, Input);
			return Active && Enabled && Input.Amplitude > 0;
		}
	}
}
