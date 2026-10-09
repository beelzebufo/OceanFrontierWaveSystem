using System;
using System.Threading.Tasks;
using Godot;
using OceanFrontier.Water.Runtime;
using OceanFrontier.Water.Queries;
using OceanFrontier.Water.Waves.AnimatedWaves;
using OceanFrontier.Water.Waves.SeaFloorDepth;
using OceanFrontier.Water.Rendering;

namespace OceanFrontier.Water.Debug;

/// <summary>Opt-in Stage 5E regression scene. Sparse GPU queries only; the analytic CPU
/// oracle below is diagnostic-only and never participates in production composition.</summary>
public partial class GerstnerPacketValidation : Node
{
	private OceanRuntime _runtime;
	private OceanPointQueryService.OwnerHandle _owner;
	private readonly Vector2[] _points = new Vector2[241];
	private readonly Vector4[] _results = new Vector4[241];
	private readonly Vector2 _origin = new(3.25f, -2.75f);
	private bool _running;
	private int _checks;

	public override async void _Ready()
	{
		try
		{
			_runtime = new OceanRuntime
			{
				FftResolution = 64, FftCascadeCount = 11,
				AnimatedWaveResolution = 256, AnimatedWaveLodCount = 6,
				AnimatedWaveBaseWorldSize = 128, AnimatedWaveViewHeightScaleEnabled = false,
			};
			_runtime.Spectrum.Multiplier = 0;
			_runtime.Foam.Enabled = false;
			AddChild(_runtime);
			_runtime.SetProcess(false);
			_runtime.FocusOverrideEnabled = true;
			_runtime.FocusOverrideXZ = _origin;
			_owner = _runtime.PointQueries.RegisterOwner(_points.Length);
			for (int i = 0; i < _points.Length; i++) _points[i] = _origin + new Vector2(i * 0.25f, 0);
			_running = true;
			for (int i = 0; i < 600; i++)
			{
				if (_runtime.TryGetAnimatedWaveSurface(0, out _, out _, out _, out _)) break;
				await Frames(1);
				if (i == 599) throw new Exception("GPU initialization timed out (RenderingDevice required).");
			}
			await RunChecks();
			GD.Print($"[PacketValidation] PASS: {_checks} checks; sparse canonical AWF physics queries.");
			Finish(0);
		}
		catch (Exception exception)
		{
			GD.PushError("[PacketValidation] FAIL: " + exception);
			Finish(1);
		}
	}

	public override void _Process(double delta)
	{
		if (_running) _runtime._Process(0); // Deterministic ocean clock; keeps async query submissions alive.
	}

	private async Task RunChecks()
	{
		var packet = new GerstnerWavePacketInput { WorldPositionXZ = _origin, Chop = 0, StartTime = _runtime.OceanTime };
		foreach (GerstnerWavePacketInput invalid in new[]
		{
			packet with { Wavelength = 0 }, packet with { Lifetime = -1 }, packet with { CrestCount = 2 },
			packet with { WorldPositionXZ = new Vector2(float.NaN, 0) }, packet with { Amplitude = float.PositiveInfinity },
			packet with { Chop = 2 }, packet with { FadeOut = 0 },
		})
		{
			bool rejected = false;
			try { _runtime.CreateGerstnerPacket(invalid); } catch (ArgumentException) { rejected = true; }
			Check(rejected, "invalid parameter rejected");
		}
		for (int i = 0; i < GerstnerWavePacketInput.Capacity; i++) Check(_runtime.CreateGerstnerPacket(packet) != 0, "bounded create");
		Check(_runtime.CreateGerstnerPacket(packet) == 0, "capacity enforced");
		_runtime.ClearGerstnerPackets();
		Check(_runtime.GerstnerPacketCount == 0, "clear handles");
		long id = _runtime.CreateGerstnerPacket(packet with { StartTime = _runtime.OceanTime + 10 });
		Check(Peak(await Sample()) == 0, "zero before start");
		Check(_runtime.UpdateGerstnerPacket(id, packet), "update active parameters");
		_runtime._Process(30);
		Vector3[] single = await Sample();
		AssertOracle(single, packet, 0.045f, "isolated packet XYZ");
		Check(Peak(single) > 0.15f && Peak(single) < packet.Amplitude, "nonzero bounded amplitude");
		int crests = 0;
		for (int i = 1; i < single.Length - 1; i++)
			if (single[i].Y > 0.03f && single[i].Y > single[i - 1].Y && single[i].Y >= single[i + 1].Y) crests++;
		Check(crests >= 3 && crests <= 5, $"finite train has {crests} positive crests");
		await CaptureVisual("single");

		float oldTime = _runtime.OceanTime;
		float dt = 0.25f;
		_runtime._Process(dt);
		Vector3[] moved = await Sample();
		AssertOracle(moved, packet, 0.045f, "outward moving carrier and group");
		float k = Mathf.Tau / packet.Wavelength, cp = MathF.Sqrt(9.81f / k);
		float r = 35, theta0 = k * r - MathF.Sqrt(9.81f * k) * oldTime;
		float theta1 = k * (r + cp * dt) - MathF.Sqrt(9.81f * k) * _runtime.OceanTime;
		Check(MathF.Abs(theta1 - theta0) < 0.0001f, "constant phase travels outward at cp, envelope at cp/2");
		Check(Difference(single, moved) > 0.03f, "carrier actually changes over time");

		_runtime.CreateGerstnerPacket(packet);
		Vector3[] doubled = await Sample();
		Check(Difference(doubled, moved, 2) < 0.003f, "identical packets add exactly without LOD multiplication");
		_runtime.ClearGerstnerPackets();
		id = _runtime.CreateGerstnerPacket(packet);
		var other = packet with { WorldPositionXZ = _origin + new Vector2(-8, 4), InitialPhase = 0.5f };
		_runtime.CreateGerstnerPacket(other);
		Vector3[] overlap = await Sample();
		AssertOracle(overlap, packet, 0.065f, "offset overlapping packets", other);
		await CaptureVisual("overlap");
		_runtime.ClearGerstnerPackets();
		id = _runtime.CreateGerstnerPacket(packet);
		_runtime.FocusOverrideXZ += new Vector2(1.3f, 0.8f);
		Vector3[] shifted = await Sample();
		Check(Difference(shifted, moved) < 0.04f, "camera snapping preserves world-space packet");
		_runtime.LodScaleOverrideEnabled = true;
		_runtime.LodScaleOverride = 2;
		Vector3[] scaled = await Sample();
		Check(Difference(scaled, moved) < 0.045f, "whole-stack scale preserves amplitude");
		_runtime.LodScaleOverride = 1;
		_runtime.FocusOverrideXZ = _origin;

		// Exercise the complementary last-two-LOD weights with a long carrier.
		var longPacket = packet with { Wavelength = 128, Lifetime = 200 };
		_runtime.UpdateGerstnerPacket(id, longPacket);
		Vector3[] longBase = await Sample();
		var camera = new Camera3D { Position = new Vector3(_origin.X, 32 * MathF.Sqrt(2) + 4, _origin.Y), Current = true };
		AddChild(camera);
		_runtime.LodScaleOverrideEnabled = false;
		_runtime.AnimatedWaveViewHeightScaleEnabled = true;
		Vector3[] transition = await Sample();
		Check(MathF.Abs(_runtime.RuntimeLodScaleAlpha - 0.5f) < 0.001f, "last-two-LOD transition alpha 0.5 exercised");
		AssertOracle(transition, longPacket, 0.065f, "last-two-LOD complementary sum");
		Check(Difference(transition, longBase) < 0.05f, "transition has no amplitude multiplication");
		_runtime.AnimatedWaveViewHeightScaleEnabled = false;
		camera.QueueFree();
		_runtime.UpdateGerstnerPacket(id, packet);

		var local = new AnimatedWaveRectInput
		{
			SizeXZ = new Vector2(1000, 1000), Displacement = new Vector3(0, 0.1f, 0),
			Placement = AnimatedWaveInputPlacement.WavelengthFilteredPreCombine, WavelengthMeters = 2,
		};
		_runtime.AddChild(local);
		Vector3[] localResult = await Sample();
		float localError = 0;
		for (int i = 0; i < localResult.Length; i++) localError = MathF.Max(localError, (localResult[i] - moved[i] - local.Displacement).Length());
		Check(localError < 0.004f, "mixed full-grid dispatch preserves local Additive input");
		local.Placement = AnimatedWaveInputPlacement.AllLodsPostCombine;
		local.BlendMode = AnimatedWaveInputBlendMode.Blend;
		local.Displacement = Vector3.Zero; local.Weight = 0.5f;
		Check(Difference(await Sample(), moved, 0.5f) < 0.004f, "post-combine Blend scales packet once");
		local.Enabled = false;

		packet = packet with { Chop = 0.5f };
		_runtime.UpdateGerstnerPacket(id, packet);
		Vector3[] chopped = await Sample();
		AssertOracle(chopped, packet, 0.06f, "XYZ displacement through inverse physics queries");
		float horizontalPeak = 0;
		foreach (Vector3 value in chopped) horizontalPeak = MathF.Max(horizontalPeak, MathF.Abs(value.X));
		Check(horizontalPeak > 0.05f, $"horizontal displacement present: {horizontalPeak:0.000} m");
		for (int i = 0; i < _points.Length; i++)
		{
			float angle = Mathf.Tau * i / _points.Length;
			_points[i] = _origin + 38 * new Vector2(MathF.Cos(angle), MathF.Sin(angle));
		}
		Vector3[] circle = await Sample();
		AssertOracle(circle, packet, 0.06f, "radial directions including displacement Z");
		float zPeak = 0;
		foreach (Vector3 value in circle) zPeak = MathF.Max(zPeak, MathF.Abs(value.Z));
		Check(zPeak > 0.05f, "horizontal Z displacement present");
		for (int i = 0; i < _points.Length; i++) _points[i] = _origin + new Vector2(i * 0.25f, 0);

		// Early-time origin guard, arbitrary phase, and near-zero radial positions.
		var young = packet with { StartTime = _runtime.OceanTime - 1, InitialPhase = 1.1f };
		_runtime.UpdateGerstnerPacket(id, young);
		AssertOracle(await Sample(), young, 0.06f, "finite smooth origin");
		packet = packet with { Chop = 0 };
		_runtime.UpdateGerstnerPacket(id, packet);
		var depth = new SeaFloorDepthRectInput { SizeXZ = new Vector2(1000, 1000), BottomHeightY = 0 };
		_runtime.AddChild(depth);
		Vector3[] shallow = await Sample();
		Check(Difference(shallow, moved, 0.05f) < 0.004f, "existing depth convention attenuates packet XYZ");
		depth.Enabled = false;

		// Chop zero makes point-query sampling linear, so FFT+packet can be compared directly.
		_runtime.ClearGerstnerPackets();
		RuntimeWaveSettings settings = _runtime.GetWaveSettingsSnapshot();
		settings.Multiplier = 1; settings.Chop = 0;
		_runtime.RequestWaveSettings(settings);
		await Frames(12);
		Vector3[] fft = await Sample();
		Check(Peak(fft) > 0.0001f, "Global FFT restored");
		id = _runtime.CreateGerstnerPacket(packet);
		Vector3[] combined = await Sample();
		Check(SumError(combined, fft, moved) < 0.008f, "FFT and packet coexist additively");
		_runtime.UpdateGerstnerPacket(id, packet with { Enabled = false });
		Check(Difference(await Sample(), fft) < 0.001f, "disable restores prior FFT field");
		_runtime.RemoveGerstnerPacket(id);
		Check(!_runtime.RemoveGerstnerPacket(id), "removed handle stays invalid");
		var directional = new AnimatedWaveRectDirectionInput
		{
			SizeXZ = new Vector2(2000, 2000), DirectionOffsetDegrees = 35,
			BlendMode = AnimatedWaveInputBlendMode.Blend, Weight = 0.7f,
		};
		_runtime.AddChild(directional);
		Vector3[] directionBase = await Sample();
		Check(Difference(directionBase, fft) > 0.001f, "Directional FFT active");
		_runtime.CreateGerstnerPacket(packet);
		Check(SumError(await Sample(), directionBase, moved) < 0.008f, "Directional FFT Blend and packet coexist");
		_runtime.ClearGerstnerPackets();
		directional.Enabled = false;

		settings.Multiplier = 0;
		_runtime.RequestWaveSettings(settings);
		await Frames(12);
		id = _runtime.CreateGerstnerPacket(packet);
		_runtime._Process(9.5); // 39.75 s, inside smooth fade-out.
		Check(Peak(await Sample()) < 0.003f, "smooth disappearance before lifetime");
		_runtime._Process(0.25);
		Check(Peak(await Sample()) == 0 && _runtime.GerstnerPacketCount == 0, "automatic expiration, exact zero");
		long dispatches = _runtime.RuntimeAnimatedWaveInputDispatchCount;
		await Frames(5);
		Check(_runtime.RuntimeAnimatedWaveInputDispatchCount == dispatches, "no input dispatches without active packets");
	}

	private async Task<Vector3[]> Sample()
	{
		await Frames(4);
		long generation = _runtime.PointQueries.SubmitBatch(_owner, _points);
		for (int frame = 0; frame < 600; frame++)
		{
			await Frames(1);
			if (!_runtime.PointQueries.TryCopyLatest(_owner, _results, out int count, out long actual, out _, out _, out double time) || actual != generation) continue;
			Check(count == _points.Length && Math.Abs(time - _runtime.OceanTime) < 0.001, "query timestamp matches canonical field");
			var values = new Vector3[count];
			for (int i = 0; i < count; i++)
			{
				if (!_results[i].IsFinite() || _results[i].W < 0.5f) throw new Exception($"Invalid/nonfinite GPU query {i}: {_results[i]}");
				values[i] = new Vector3(_results[i].X, _results[i].Y, _results[i].Z);
			}
			return values;
		}
		throw new Exception("Sparse GPU query timeout.");
	}

	private void AssertOracle(Vector3[] actual, GerstnerWavePacketInput packet, float tolerance, string name, GerstnerWavePacketInput other = null)
	{
		float error = 0;
		for (int i = 0; i < actual.Length; i++)
		{
			Vector2 source = _points[i];
			Vector3 Evaluate(Vector2 p) => EvaluatePacket(p, packet, _runtime.OceanTime) +
				(other == null ? Vector3.Zero : EvaluatePacket(p, other, _runtime.OceanTime));
			for (int j = 0; j < 4; j++) { Vector3 d = Evaluate(source); source = _points[i] - new Vector2(d.X, d.Z); }
			error = MathF.Max(error, (actual[i] - Evaluate(source)).Length());
		}
		Check(error < tolerance, $"{name}: max error {error:0.00000} m < {tolerance}");
	}

	private static Vector3 EvaluatePacket(Vector2 position, GerstnerWavePacketInput p, float time)
	{
		float tau = time - p.StartTime;
		Vector2 delta = position - p.WorldPositionXZ;
		float r = delta.Length();
		if (tau <= 0 || tau >= p.Lifetime || r == 0 || !p.Enabled) return Vector3.Zero;
		float k = Mathf.Tau / p.Wavelength, omega = MathF.Sqrt(9.81f * k);
		float behind = 0.5f * omega / k * tau - r, width = p.CrestCount * p.Wavelength;
		if (behind <= 0 || behind >= width) return Vector3.Zero;
		float e = Smooth(behind / (0.5f * p.Wavelength)) * Smooth((width - behind) / (0.5f * p.Wavelength)) *
			Smooth(r / (0.5f * p.Wavelength)) * Smooth(tau / p.FadeIn) * Smooth((p.Lifetime - tau) / p.FadeOut) * MathF.Exp(-tau / p.Lifetime);
		float theta = k * r - omega * tau + p.InitialPhase;
		Vector2 h = -p.Chop * p.Amplitude * e * MathF.Sin(theta) * delta / r;
		return new Vector3(h.X, p.Amplitude * e * MathF.Cos(theta), h.Y);
	}

	private static float Smooth(float x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }
	private static float Peak(Vector3[] data) { float max = 0; foreach (Vector3 p in data) max = MathF.Max(max, p.Length()); return max; }
	private static float Difference(Vector3[] a, Vector3[] b, float scale = 1) { float max = 0; for (int i = 0; i < a.Length; i++) max = MathF.Max(max, (a[i] - scale * b[i]).Length()); return max; }
	private static float SumError(Vector3[] a, Vector3[] b, Vector3[] c) { float max = 0; for (int i = 0; i < a.Length; i++) max = MathF.Max(max, (a[i] - b[i] - c[i]).Length()); return max; }
	private void Check(bool passed, string message) { if (!passed) throw new Exception(message); _checks++; GD.Print("[PacketValidation] " + message); }
	private async Task Frames(int count) { for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }

	private async Task CaptureVisual(string name)
	{
		// Existing diagnostic view consumes the same canonical texture as surface and queries.
		var view = new FftDisplacementDebugView { Gain = 2, PanelSize = new Vector2(650, 650), Mode = FftDisplacementDebugView.DebugMode.Grayscale };
		_runtime.AddChild(view);
		await Frames(4);
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
		string directory = OS.GetEnvironment("OCEAN_PACKET_CAPTURE_DIR");
		if (!string.IsNullOrEmpty(directory))
		{
			DirAccess.MakeDirRecursiveAbsolute(directory);
			GetViewport().GetTexture().GetImage().SavePng(directory + "/packet-" + name + ".png");
		}
		view.QueueFree();
	}

	private void Finish(int code)
	{
		_running = false;
		if (_owner != null) _runtime.PointQueries.UnregisterOwner(_owner);
		_runtime?.QueueFree();
		GetTree().Quit(code);
	}
}
