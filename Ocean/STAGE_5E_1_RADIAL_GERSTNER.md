# Stage 5E-1 — radial Gerstner packet

Starting HEAD: `e6f6af18a3a9b8220f4da7ad5a8e4930ad8e30ae`.

Read-only audit preceded implementation. Architecture instructions are stored as
`OCEAN_ARCHITECTURE_SPEC.md.txt` in this checkout. The starting worktree already
contained terrain, scene, boat and physics-panel edits; these are excluded from
this stage's commit. No material architecture discrepancy was found.

## Crest 4 source audit

Source pinned to `db0658ff0b2e93e4a9e28cc2867509658b0ecc00`, public MIT Crest 4.
Paths below are under `crest/Assets/Crest/Crest/`:

| Source / function | Finding |
|---|---|
| [Scripts/Shapes/ShapeGerstnerBatched.cs](https://github.com/wave-harmonic/crest/blob/db0658ff0b2e93e4a9e28cc2867509658b0ecc00/crest/Assets/Crest/Crest/Scripts/Shapes/ShapeGerstnerBatched.cs), `UpdateBatch`, `GerstnerBatch` | Batches up to 32 components in groups of four; CPU packs k, direction, amplitude, negative chop amplitude and time-dependent phase into material arrays. Wavelength buckets register as LOD inputs; final two slices receive transition weights. The old batched phase advances with a positive time sign. |
| [Scripts/Shapes/ShapeGerstner.cs](https://github.com/wave-harmonic/crest/blob/db0658ff0b2e93e4a9e28cc2867509658b0ecc00/crest/Assets/Crest/Crest/Scripts/Shapes/ShapeGerstner.cs), `SliceUpWaves`, `UpdateGenerateWaves`, cleanup | Structured buffers pack four components, including two counter-propagating waves, cascade start indices and cumulative variance. Wavenumbers are adjusted for periodic tiling. CPU rebuilds component data when needed; compute evaluates persistent wave texture-array cascades each frame. Buffers/textures have explicit cleanup. |
| [Shaders/OceanInputs/GerstnerShared.hlsl](https://github.com/wave-harmonic/crest/blob/db0658ff0b2e93e4a9e28cc2867509658b0ecc00/crest/Assets/Crest/Crest/Shaders/OceanInputs/GerstnerShared.hlsl), `ComputeGerstner` | Vertical cosine, horizontal sine with packed negative chop; depth factor approximately clamp(2 depth / wavelength). Direct Towards Point weights preferred directional components; it is not a compact radial packet. |
| [Shaders/OceanInputs/AnimWavesGerstnerBatchGeometry.shader](https://github.com/wave-harmonic/crest/blob/db0658ff0b2e93e4a9e28cc2867509658b0ecc00/crest/Assets/Crest/Crest/Shaders/OceanInputs/AnimWavesGerstnerBatchGeometry.shader), `Frag` | Additive geometry rasterization; UV-edge and optional vertex-color feather. Unity renderer/material/mesh plumbing is not needed here. |
| [Shaders/Resources/Gerstner.compute](https://github.com/wave-harmonic/crest/blob/db0658ff0b2e93e4a9e28cc2867509658b0ecc00/crest/Assets/Crest/Crest/Shaders/Resources/Gerstner.compute), `ComputeGerstner`, `Gerstner` | Four-wide sine/cosine evaluation with phases k·x + phase ± omega·time; outputs XYZ plus variance to persistent wave cascades. Portable dispersion/math, not the periodic-source architecture, informs this packet. |
| [Scripts/LodData/LodDataMgrAnimWaves.cs](https://github.com/wave-harmonic/crest/blob/db0658ff0b2e93e4a9e28cc2867509658b0ecc00/crest/Assets/Crest/Crest/Scripts/LodData/LodDataMgrAnimWaves.cs), `FilterWavelength`, `BuildCommandBuffer`, `CombinePassCompute` | Filtered/pre inputs → coarse-to-fine combine → wavelength-zero post inputs. Half-open wavelength bands; longest waves split between last two slices with 1-alpha/alpha weights. Persistent direct/final arrays; Unity command buffers, render targets, property wrappers are platform infrastructure. |
| [Shaders/Resources/ShapeCombine.compute](https://github.com/wave-harmonic/crest/blob/db0658ff0b2e93e4a9e28cc2867509658b0ecc00/crest/Assets/Crest/Crest/Shaders/Resources/ShapeCombine.compute), `ShapeCombineBase` | Direct displacement plus manually bilinear-sampled next-coarser final XYZ; direct variance is retained. Optional flow/dynamic-wave branches are outside this stage. |

New radial-envelope code is original; no new Crest code was directly copied.
Existing direct ports retain their existing notices.

## Audited project flow and ownership

`OceanRuntime` owns the fixed-capacity ordered `AnimatedWaveInputRegistry`.
Scene rectangles register/unregister with the runtime; main-thread capture publishes
immutable snapshots under a short lock. Render-thread scratch storage and GPU upload
arrays are reused. Additive adds weighted XYZ; Blend erases existing contribution
independently of transition alpha before adding its weighted source.

`AnimatedWaveComposer` owns persistent direct/final RGBA16F texture arrays,
R16F terrain-height data, 32-byte-per-LOD metadata and derivative data. It runs:

1. Update snapped `AnimatedWaveLodLayout`, upload the shared LOD buffer.
2. Compose `SeaFloorDepthField` from registered terrain-height inputs.
3. FFT → `AnimatedWaveDirectField`, with shallow attenuation.
4. Ordered direct inputs, now including radial packets.
5. `Final[L] = Direct[L] + bilinear(Final[L+1])`, coarse to fine with barriers.
6. Existing post-combine inputs → derivative pass → publish committed spatial state.

Renderer bindings and `OceanPointQueryService` continue to borrow the same final
`AnimatedWaveField` and corresponding LOD metadata. Queries retain their four-step
horizontal inversion and small async result-buffer readbacks. No renderer/query
Gerstner evaluation, FFT kernel change, full-grid wave readback, or alternate final
field was introduced. Existing diagnostic UI, field view and point-query service
were reused; the repository had diagnostic scenes rather than a separate unit-test project.

## Source management and API

Main-thread APIs on `OceanRuntime`:

```csharp
long id = runtime.TriggerGerstnerPacket(new Vector2(20, -15));
var input = new GerstnerWavePacketInput {
    WorldPositionXZ = new Vector2(20, -15), StartTime = runtime.OceanTime,
    Amplitude = 0.5f, Wavelength = 8, CrestCount = 4, Chop = 0.5f,
    InitialPhase = 0, Lifetime = 40, FadeIn = 1, FadeOut = 5,
    AttenuationStrength = 0.95f, Enabled = true,
};
runtime.UpdateGerstnerPacket(id, input);
runtime.UpdateGerstnerPacket(id, input with { Enabled = false });
runtime.RemoveGerstnerPacket(id);
runtime.ClearGerstnerPackets();
// CreateGerstnerPacket(input) also supports an explicit future StartTime.
```

At most eight descriptions, sharing the existing 64-input registry. Capacity
exhaustion returns handle zero; invalid descriptions throw argument exceptions.
Expired handles are removed automatically, including disabled packets. Records
are immutable; updates replace a description for the next snapshot. Trigger/update
may allocate descriptions; steady-state capture/upload/expiry uses fixed arrays.
Packets run at maximum pre-combine priority, so ordinary local pre modifiers operate
before their additive contribution; existing post modifiers still affect them.

Authoring bounds: amplitude 0–4 m, wavelength 0.1–10000 m, integer crest count 3–5,
chop 0–1, phase ±2π, lifetime 0.01–3600 s, positive fade durations no longer than
lifetime, attenuation 0–1; positions/time/amplitude and other scalars must be finite.
`StartTime` is nonnegative ocean time. These conservative limits bound packet XYZ
by 32 m across all eight sources. Surface culling margins add this allowance to the
previous 128 m allowance, including nested tiles and the normal visualization.

## Mathematics

With r = |worldXZ - origin|, tau = oceanTime - start, k = 2π/λ,
omega = sqrt(g·k), cp = omega/k and cg = cp/2:

- Front radius = cg·tau; width = CrestCount·λ; b = front - r.
- Compact support: 0 < tau < lifetime and 0 < b < width.
- Edge width = λ/2. Spatial envelope is smoothstep(0, edge, b) times
  smoothstep(0, edge, width-b) times smoothstep(0, edge, r).
- Temporal envelope is smoothstep(0, FadeIn, tau) times
  smoothstep(0, FadeOut, lifetime-tau) times exp(-tau/lifetime).
- theta = k·r - omega·tau + initialPhase.
- Dy = A·envelope·cos(theta).
- Dxz = -Q·A·envelope·sin(theta)·(worldXZ-origin)/r.

The exact origin returns zero; the radial fade is O(r²), making displacement and
first derivatives continuous there for arbitrary phase. Both compact edges have
zero first derivative. Constant phase gives dr/dtau = +omega/k, proving outward
carrier motion; the envelope moves at half that speed. No Crest positive time sign
was copied implicitly. All output uses project X/Y/Z axes, Y up.

Depth attenuation reuses sea level zero minus terrain height and
mix(1, clamp(2 depth/λ), AttenuationStrength), with the existing maximum-depth blend.
It affects all XYZ equally. No depth field means no attenuation. This stage has no
refraction, altered dispersion, radial energy-conservation solver, or wave history.

## GPU packing, LODs and cost

The existing 80-byte descriptor has a new operation tag. For packets its first
three vec4s hold origin/start/lifetime, wavelength/amplitude/crests/chop, and
phase/fade-in/fade-out/attenuation. No persistent GPU resource is added. The existing
5 KiB descriptor buffer and uniform sets keep their explicit create/reuse/dispose
lifecycle. Push constants expand from 48 to 64 bytes for ocean time, gravity and
bounded dispatch offsets.

Packet wavelength filtering uses λ/4 with the existing filter: normally 8–16 texels
per carrier, allowing better envelope sampling than a 2–4-texel carrier. A packet
enters one direct LOD, or the final pair with complementary weights; ShapeCombine
inherits it once. It is never inserted at full strength into every direct slice.
World-coordinate phase stays fixed when snapped camera-relative windows move.
Sub-resolution packets are filtered out according to the existing LOD contract;
packets outside eligible spatial windows do not contribute. This is intentional.

Expected cost (not isolated GPU timing):

- No active packets: no extra descriptor upload/dispatch from packets.
- Only packet pre inputs: one dispatch per intersecting eligible LOD, over the
  union of their front-radius bounding boxes. Usually one for equal wavelengths;
  two during the final-pair transition, up to the existing LOD count for varied sources.
- With ordinary pre inputs: reuse their existing full-domain dispatch; no extra pass.
- Each touched texel reads/writes one RGBA16F value (16 bytes logical traffic),
  plus 2 bytes when depth is present; descriptors are cacheable. Support/LOD rejection
  precedes trigonometry. Up to eight packet evaluations per touched texel.
- Example at production 384² × 8 LODs, base width 32 m, default λ=8 at tau=30:
  assigned LOD 3, about 160² dispatched texels versus 1,179,648 full-domain texels.
  Logical target traffic is about 0.39 MiB versus 18 MiB. These are estimates,
  excluding cache effects and unchanged FFT/combine/derivative work.
- CPU work is bounded description sorting, packing and O(packets × LODs) bounds
  metadata, with no wave-grid evaluation or new steady-state managed allocations
  in the packet code. Godot/driver allocation costs were not profiled.

## Validation and reproduction

Environment verified: Godot 4.7.2 mono, Vulkan 1.4.305 / RADV RENOIR,
AMD Ryzen 7 5825U with integrated Radeon Graphics. Final run used Forward Plus.
`dotnet build --no-restore`: zero warnings/errors. Isolated GPU regression: **71
checks passed**, clean resource release, no runtime warnings/errors. Shader import
succeeded; the separate headless editor import emitted shutdown resource-leak
messages, while the isolated Vulkan validation did not.

Run from the repository (use your Godot mono executable):

```sh
dotnet build --no-restore
godot --headless --editor --path . --import
OCEAN_PACKET_CAPTURE_DIR=/tmp/stage5e-validation godot --path . \
  --rendering-method forward_plus --rendering-driver vulkan \
  --resolution 700x730 res://Ocean/Debug/GerstnerPacketValidation.tscn
```

The opt-in regression scene submits 241 sparse world-space physics points per
batch; it does not read back a displacement texture. A diagnostic-only CPU oracle
checks the canonical GPU result within tolerances for half-float storage,
interpolation, LOD resampling and query inversion. Measured maximum errors: isolated
packet 0.02448 m; shifted overlapping packets 0.03654 m; last-pair transition
0.02818 m; chopped inverse queries 0.01964 m; near-origin test 0.00240 m.
The suite checks all parameter rejection categories, capacity, update/remove/clear,
pre-start zero, exactly four crests, outward motion, overlap, world-origin offsets,
XZ direction, snapped camera movement, stack scaling, alpha=0.5 complementary LOD
weights, local Additive/post Blend, depth, Global/Directional FFT coexistence,
disabling, fade-out, exact expiry and zero input dispatches afterward.

Captured `packet-single.png` and `packet-overlap.png` from the existing AWF debug
view were visually inspected: finite concentric train and overlapping interference,
with smooth grayscale edges. They are rendered diagnostic images, not full-grid
wave readbacks. No separate GPU pass timing, CPU allocation profile, or long-running
camera traversal benchmark was measured. The production 3D scene remains available
for subjective water-material inspection; the automated scene inspects the canonical
field and physics, not the complete terrain/boat scene.

Interactive diagnostic: Diagnostics → Radial Gerstner packet. Set origin X/Z,
isolate FFT, trigger one/two, clear, then restore FFT. Existing pause/time scale,
2D AWF Height view and point-query markers remain available. Full default train
emerges at about 18 s and vanishes at 40 s.

## Changed files and limitations

- `Waves/AnimatedWaves/GerstnerWavePacketInput.cs`: immutable parameters and limits.
- `Runtime/OceanRuntime.GerstnerPackets.cs`, `Runtime/OceanRuntime.cs`: bounded
  source management, ocean time/gravity routing and lifecycle.
- `Waves/AnimatedWaves/AnimatedWaveInput.cs`, `AnimatedWaveInputRegistry.cs`,
  `AnimatedWaveInputPass.cs`, `AnimatedWaveComposer.cs`: operation, snapshot,
  registration result, descriptor packing and bounded dispatch.
- `Shaders/Waves/animated_wave_inputs.glsl`: sole production packet evaluator.
- `Rendering/AnimatedWaveSurfaceRenderer.cs`, `.Geometry.cs`: culling allowance only.
- `Debug/OceanDiagnosticDiagnosticsPanel.cs`: compact controls.
- `Debug/GerstnerPacketValidation.cs`, `.tscn`: opt-in GPU regression, plus script UIDs.
- This report. FFT math, query shaders, ShapeCombine, depth composition and optics unchanged.

Known limits: a monochromatic artist-controlled packet, with temporal decay rather
than physically conserved radial energy. Very steep permitted parameter combinations
can challenge the existing horizontal-inversion contract; defaults are conservative.
Sampling loses short waves at coarse scales and outside assigned LOD windows.
Long-lived simulation time inherits existing float-clock precision. Abrupt explicit
parameter updates/removal are intentional; automatic lifetime endpoints are smooth.
No Dynamic Waves, foam changes, wakes, or new render system.

Final commit is the stage commit containing this report (reported in the delivery;
its self-referential hash is intentionally not embedded here).
