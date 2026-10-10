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
			GD.Print($"[PacketValidation] Stage 5E-1 retained: {_checks} checks passed.");
			await RunSectorChecks();
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

	private async Task RunSectorChecks()
	{
		GD.Print("[PacketValidation] Stage 5E-2 sector checks");
		SetCirclePoints();
		float phase = Mathf.PosMod(MathF.Sqrt(9.81f * Mathf.Tau / 8) * 30 - Mathf.Tau / 8 * 36 + Mathf.Pi, Mathf.Tau) - Mathf.Pi;
		var radial = new GerstnerWavePacketInput
		{
			WorldPositionXZ = _origin, StartTime = _runtime.OceanTime - 30, Chop = 0, InitialPhase = phase,
		};
		foreach (GerstnerWavePacketInput invalid in new[]
		{
			radial with { DirectionXZ = Vector2.Zero }, radial with { DirectionXZ = new Vector2(float.NaN, 1) },
			radial with { DirectionXZ = new Vector2(1, float.PositiveInfinity) },
			radial with { SectorHalfAngleDegrees = 0 }, radial with { SectorHalfAngleDegrees = 181 },
			radial with { SectorHalfAngleDegrees = float.NaN }, radial with { SectorHalfAngleDegrees = float.PositiveInfinity },
			radial with { AngularFeatherDegrees = -1 }, radial with { AngularFeatherDegrees = 181 },
			radial with { AngularFeatherDegrees = float.NaN }, radial with { AngularFeatherDegrees = float.PositiveInfinity },
			radial with { SectorHalfAngleDegrees = 20, AngularFeatherDegrees = 21 },
		})
		{
			bool rejected = false;
			try { _runtime.CreateGerstnerPacket(invalid); } catch (ArgumentException) { rejected = true; }
			Check(rejected, "invalid sector parameter rejected");
		}
		Check(AnimatedWaveInputPass.DescriptorStrideBytes == 80 && GerstnerWavePacketInput.Capacity == 8, "descriptor stride and capacity unchanged");
		long id = _runtime.CreateGerstnerPacket(radial);
		Vector3[] full = await Sample();
		AssertOracle(full, radial, 0.04f, "default full circle matches unchanged Stage 5E-1 oracle");
		_runtime.UpdateGerstnerPacket(id, radial with { DirectionXZ = Vector2.Left, AngularFeatherDegrees = 180 });
		Check(Difference(await Sample(), full) == 0, "H=180 bypasses angle and feather exactly, including opposite axis");
		await CaptureVisual("full-circle");
		var sector = radial with { SectorHalfAngleDegrees = 90, AngularFeatherDegrees = 15 };
		_runtime.UpdateGerstnerPacket(id, sector);
		Vector3[] half = await Sample();
		AssertSectorOracle(half, sector, 0.04f, "half-circle canonical physics");
		Check(half[0].Y > 0.15f && half[120].Length() == 0, "half-circle has positive interior and zero opposite hemisphere");
		await CaptureVisual("half-circle");
		sector = sector with { SectorHalfAngleDegrees = 45, AngularFeatherDegrees = 10 };
		_runtime.UpdateGerstnerPacket(id, sector);
		await Sample();
		await CaptureVisual("90-degree-sector");
		sector = sector with { SectorHalfAngleDegrees = 20, AngularFeatherDegrees = 10 };
		_runtime.UpdateGerstnerPacket(id, sector);
		Vector3[] narrow = await Sample();
		AssertSectorOracle(narrow, sector, 0.04f, "narrow sector canonical physics");
		Check(narrow[0].Y > 0.15f && narrow[60].Length() == 0 && narrow[120].Length() == 0, "narrow sector orientation and no opposite leakage");
		await CaptureVisual("narrow-sector");
		for (int axis = 0; axis < 4; axis++)
		{
			Vector2 direction = axis switch { 0 => Vector2.Right, 1 => Vector2.Down, 2 => Vector2.Left, _ => Vector2.Up };
			Check(_runtime.UpdateGerstnerPacket(id, sector with { DirectionXZ = direction * 7 }), "direction update keeps existing handle");
			Vector3[] rotated = await Sample();
			AssertSectorOracle(rotated, sector with { DirectionXZ = direction }, 0.04f, $"world XZ direction {axis * 90} degrees");
			Check(rotated[axis * 60].Y > 0.15f && rotated[(axis * 60 + 120) % 240].Length() == 0, "updated direction rotates the sector");
		}
		foreach (float magnitude in new[] { float.Epsilon, float.MaxValue })
		{
			_runtime.UpdateGerstnerPacket(id, sector with { DirectionXZ = new Vector2(magnitude, 0) });
			Check(Difference(await Sample(), narrow) == 0, "finite extreme direction normalizes without overflow or mutation");
		}
		Check(sector.DirectionXZ == Vector2.Right && _runtime.GerstnerPacketCount == 1, "immutable description and source count preserved");

		// Dense angular samples through both boundaries; fixed crest radius keeps the radial oracle positive.
		for (int i = 0; i < _points.Length; i++)
		{
			float angle = Mathf.DegToRad(-60 + i * 0.5f);
			_points[i] = _origin + 36 * new Vector2(MathF.Cos(angle), MathF.Sin(angle));
		}
		var feathered = sector with { SectorHalfAngleDegrees = 45, AngularFeatherDegrees = 20 };
		_runtime.UpdateGerstnerPacket(id, feathered);
		Vector3[] feather = await Sample();
		AssertSectorOracle(feather, feathered, 0.04f, "smooth angular feather");
		Check(feather[170].Y > feather[190].Y && feather[190].Y > feather[210].Y && feather[220].Length() == 0,
			"feather falls from inner boundary through midpoint to zero outside");
		float step = 0;
		for (int i = 1; i < feather.Length; i++) step = MathF.Max(step, (feather[i] - feather[i - 1]).Length());
		Check(step < 0.02f, $"positive feather continuous at 0.5 degree sampling: max step {step:0.00000} m");
		// Hard/sub-texel edges are checked away from boundaries; bilinear AWF sampling necessarily blurs them.
		for (int i = 0; i < _points.Length; i++)
		{
			float angle = Mathf.DegToRad(new[] { 0f, 10f, 30f, 180f }[i % 4]);
			_points[i] = _origin + 36 * new Vector2(MathF.Cos(angle), MathF.Sin(angle));
		}
		var hard = sector with { AngularFeatherDegrees = 0 };
		_runtime.UpdateGerstnerPacket(id, hard);
		Vector3[] hardResult = await Sample();
		AssertSectorOracle(hardResult, hard, 0.04f, "zero feather explicit hard edge");
		_runtime.UpdateGerstnerPacket(id, hard with { AngularFeatherDegrees = float.Epsilon });
		Check(Difference(await Sample(), hardResult) == 0, "coincident float cosine thresholds are finite and deterministic");
		_runtime.UpdateGerstnerPacket(id, hard with { SectorHalfAngleDegrees = 1 });
		Vector3[] minimum = await Sample();
		Check(minimum[0].Y > 0.04f && minimum[2].Length() == 0 && minimum[3].Length() == 0, "one-degree minimum sector is finite with no opposite leakage");
		SetCirclePoints();
		var chopped = feathered with { Chop = 0.5f, InitialPhase = phase + 0.7f };
		_runtime.UpdateGerstnerPacket(id, chopped);
		AssertSectorOracle(await Sample(), chopped, 0.055f, "sector XYZ and physics horizontal inversion");
		for (int i = 0; i < _points.Length; i++) _points[i] = _origin + new Vector2((i - 120) * 0.01f, 0);
		var young = chopped with { StartTime = _runtime.OceanTime - 1 };
		_runtime.UpdateGerstnerPacket(id, young);
		AssertSectorOracle(await Sample(), young, 0.025f, "directional origin and near-origin guard");

		SetCirclePoints();
		_runtime.UpdateGerstnerPacket(id, feathered);
		Vector3[] first = await Sample();
		var other = feathered with { WorldPositionXZ = _origin + new Vector2(-8, 4), DirectionXZ = new Vector2(1, 1) };
		_runtime.UpdateGerstnerPacket(id, other);
		Vector3[] second = await Sample();
		_runtime.UpdateGerstnerPacket(id, feathered);
		long secondId = _runtime.CreateGerstnerPacket(other);
		Vector3[] overlap = await Sample();
		Check(SumError(overlap, first, second) < 0.002f, "two directional packets add through canonical field");
		AssertSectorOracle(overlap, feathered, 0.055f, "directional overlap oracle", other);
		await CaptureVisual("directional-overlap");
		_runtime.RemoveGerstnerPacket(secondId);
		_runtime.FocusOverrideXZ += new Vector2(1.3f, 0.8f);
		Check(Difference(await Sample(), first) < 0.04f, "camera snapping preserves sector world orientation");
		_runtime.LodScaleOverrideEnabled = true; _runtime.LodScaleOverride = 2;
		Check(Difference(await Sample(), first) < 0.04f, "sector whole-stack scale has no amplitude multiplication");
		_runtime.LodScaleOverride = 1; _runtime.FocusOverrideXZ = _origin;
		var longSector = feathered with { Wavelength = 128, Lifetime = 200, SectorHalfAngleDegrees = 90, AngularFeatherDegrees = 60 };
		_runtime.UpdateGerstnerPacket(id, longSector);
		var camera = new Camera3D { Position = new Vector3(_origin.X, 32 * MathF.Sqrt(2) + 4, _origin.Y), Current = true };
		AddChild(camera);
		_runtime.LodScaleOverrideEnabled = false; _runtime.AnimatedWaveViewHeightScaleEnabled = true;
		Vector3[] transition = await Sample();
		Check(MathF.Abs(_runtime.RuntimeLodScaleAlpha - 0.5f) < 0.001f, "sector last-two-LOD alpha 0.5 exercised");
		AssertSectorOracle(transition, longSector, 0.065f, "sector complementary LOD weights");
		_runtime.AnimatedWaveViewHeightScaleEnabled = false; camera.QueueFree();
		_runtime.UpdateGerstnerPacket(id, feathered);
		await Frames(4);
		await CheckSectorDispatches(1, "one eligible LOD uses one dispatch per frame");

		var depth = new SeaFloorDepthRectInput { SizeXZ = new Vector2(1000, 1000), BottomHeightY = 0 };
		_runtime.AddChild(depth);
		Check(Difference(await Sample(), first, 0.05f) < 0.003f, "sector retains shallow-water attenuation");
		depth.Enabled = false;
		var local = new AnimatedWaveRectInput { SizeXZ = new Vector2(1000, 1000), Displacement = new Vector3(0, 0.1f, 0),
			Placement = AnimatedWaveInputPlacement.WavelengthFilteredPreCombine, WavelengthMeters = 2 };
		_runtime.AddChild(local);
		Vector3[] mixed = await Sample();
		float error = 0;
		for (int i = 0; i < mixed.Length; i++) error = MathF.Max(error, (mixed[i] - first[i] - local.Displacement).Length());
		Check(error < 0.003f, "sector coexists with local pre-combine input");
		await CheckSectorDispatches(1, "sector reuses ordinary-input dispatch");
		local.Placement = AnimatedWaveInputPlacement.AllLodsPostCombine;
		local.BlendMode = AnimatedWaveInputBlendMode.Blend; local.Weight = 0.5f; local.Displacement = Vector3.Zero;
		Check(Difference(await Sample(), first, 0.5f) < 0.003f, "post modifier applies once to sector");
		local.Enabled = false;
		_runtime.RemoveGerstnerPacket(id);
		RuntimeWaveSettings settings = _runtime.GetWaveSettingsSnapshot(); settings.Multiplier = 1; settings.Chop = 0;
		_runtime.RequestWaveSettings(settings); await Frames(12);
		Vector3[] fft = await Sample();
		id = _runtime.CreateGerstnerPacket(feathered);
		Check(SumError(await Sample(), fft, first) < 0.008f, "Global FFT plus directional sector");
		_runtime.RemoveGerstnerPacket(id);
		var directional = new AnimatedWaveRectDirectionInput { SizeXZ = new Vector2(2000, 2000), DirectionOffsetDegrees = 35,
			BlendMode = AnimatedWaveInputBlendMode.Blend, Weight = 0.7f };
		_runtime.AddChild(directional);
		Vector3[] directionalBase = await Sample();
		id = _runtime.CreateGerstnerPacket(feathered);
		Check(SumError(await Sample(), directionalBase, first) < 0.008f, "Directional FFT plus sector");
		directional.Enabled = false;
		settings.Multiplier = 0; _runtime.RequestWaveSettings(settings); await Frames(12);
		_runtime.UpdateGerstnerPacket(id, feathered with { Enabled = false });
		Check(Peak(await Sample()) == 0, "disabled sector clears field");
		_runtime.UpdateGerstnerPacket(id, feathered);
		_runtime._Process(10);
		Check(Peak(await Sample()) == 0 && _runtime.GerstnerPacketCount == 0, "direction updates preserve original start and expiration");
		await CheckSectorDispatches(0, "expired sectors add zero dispatches");
		id = _runtime.TriggerGerstnerPacket(_origin); _runtime.RemoveGerstnerPacket(id);
		await Frames(4);
		await CheckSectorDispatches(0, "removed sectors add zero dispatches");
	}

	private void SetCirclePoints()
	{
		for (int i = 0; i < _points.Length - 1; i++)
		{
			float angle = Mathf.Tau * i / (_points.Length - 1);
			_points[i] = _origin + 36 * new Vector2(MathF.Cos(angle), MathF.Sin(angle));
		}
		_points[^1] = _origin;
	}

	private async Task CheckSectorDispatches(int perFrame, string name)
	{
		// Fence render-thread counters without reading any wave texture or synchronizing production code.
		long before = -1, after = -1;
		_running = false;
		await Frames(2);
		RenderingServer.CallOnRenderThread(Callable.From(() => before = _runtime.RuntimeAnimatedWaveInputDispatchCount));
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
		_running = false;
		for (int i = 0; i < 3; i++) { _runtime._Process(0); await Frames(1); }
		RenderingServer.CallOnRenderThread(Callable.From(() => after = _runtime.RuntimeAnimatedWaveInputDispatchCount));
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
		_running = true;
		Check(before >= 0 && after - before == 3 * perFrame, $"{name}: {after - before} / 3 frames");
	}

	private void AssertSectorOracle(Vector3[] actual, GerstnerWavePacketInput packet, float tolerance, string name, GerstnerWavePacketInput other = null)
	{
		float error = 0;
		for (int i = 0; i < actual.Length; i++)
		{
			Vector3 Evaluate(Vector2 p) => EvaluateSector(p, packet) + (other == null ? Vector3.Zero : EvaluateSector(p, other));
			Vector2 source = _points[i];
			for (int j = 0; j < 4; j++) { Vector3 d = Evaluate(source); source = _points[i] - new Vector2(d.X, d.Z); }
			error = MathF.Max(error, (actual[i] - Evaluate(source)).Length());
		}
		Check(error < tolerance, $"{name}: max error {error:0.00000} m < {tolerance}");
	}

	private Vector3 EvaluateSector(Vector2 position, GerstnerWavePacketInput p)
	{
		Vector3 radial = EvaluatePacket(position, p, _runtime.OceanTime); // Original Stage 5E-1 oracle is unchanged.
		if (p.SectorHalfAngleDegrees == 180 || position == p.WorldPositionXZ) return radial;
		// Independent double-precision angular reference, diagnostic only.
		double dx = p.DirectionXZ.X, dz = p.DirectionXZ.Y, length = Math.Sqrt(dx * dx + dz * dz);
		Vector2 delta = position - p.WorldPositionXZ;
		double r = Math.Sqrt((double)delta.X * delta.X + (double)delta.Y * delta.Y);
		double alignment = Math.Clamp((delta.X * dx + delta.Y * dz) / (r * length), -1, 1);
		double outer = Math.Cos(p.SectorHalfAngleDegrees * Math.PI / 180);
		double inner = Math.Cos((p.SectorHalfAngleDegrees - p.AngularFeatherDegrees) * Math.PI / 180);
		float weight = inner > outer ? Smooth((float)((alignment - outer) / (inner - outer))) : alignment >= outer ? 1 : 0;
		return radial * weight;
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
