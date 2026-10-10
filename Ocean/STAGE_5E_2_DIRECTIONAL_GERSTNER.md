# Stage 5E-2 — directional Gerstner wave sectors

## Starting state and read-only audit

Starting HEAD: `8e35599f5aeec83b25399dbb6e6dde43403b575b`.
Starting branch: `codex/stage-5e-1-radial-gerstner`.
The local implementation exactly matched the requested Stage 5E-1 baseline.
Read `OCEAN_ARCHITECTURE_SPEC.md.txt` and the Stage 5E-1 report before editing.

Pre-existing changes were confined to the physics diagnostic panel, terrain data,
terrain resources, ocean/game scenes, and untracked boat/vegetation/terrain assets.
A SHA-256 inventory of all 67 pre-existing modified/untracked files was recorded
outside the repository. Their hashes were unchanged after implementation and GPU
validation; none are included in this commit.

Audited files: `GerstnerWavePacketInput`, `AnimatedWaveInput`,
`AnimatedWaveInputRegistry`, `AnimatedWaveInputPass`, `AnimatedWaveComposer`,
`AnimatedWaveLodLayout`, `AnimatedWaveCombinePass`, `OceanRuntime.GerstnerPackets`,
`animated_wave_inputs.glsl`, `GerstnerPacketValidation`, and
`OceanDiagnosticDiagnosticsPanel`.

Findings:

- Immutable packet descriptions are validated by the existing create/update APIs.
  Eight packet handles share the existing ordered 64-input registry. Expiration,
  enabled state, IDs and immutable snapshot publication already support this extension.
- Packet operation tag is 3. The 80-byte std430 descriptor uses bytes 0–47 for
  radial parameters, 64–79 for metadata, and leaves bytes 48–63 spare.
- Packet-only dispatches use conservative radial AABB unions on eligible LODs.
  Mixed pre-combine inputs reuse the existing full-domain dispatch. Neither path
  needs a new resource or a sector-specific dispatch implementation.
- LOD selection uses wavelength × 0.25, with complementary final-pair weights.
  These rules, dispatch bounds and the 64-byte push constants remain unchanged.
- Terrain height feeds the established shallow attenuation formula. Direct inputs
  precede coarse-to-fine ShapeCombine, then post inputs, derivatives and committed
  spatial publication. Renderer and physics continue consuming the same canonical
  `AnimatedWaveField`; physics dispatch follows composition with the same ocean time.

## Crest 4 comparison

Public MIT reference is pinned to
[`db0658ff0b2e93e4a9e28cc2867509658b0ecc00`](https://github.com/wave-harmonic/crest/tree/db0658ff0b2e93e4a9e28cc2867509658b0ecc00).
Inspected paths below are under `crest/Assets/Crest/Crest/`:

| Source | Relevant finding |
|---|---|
| [Scripts/Shapes/ShapeGerstnerBatched.cs](https://github.com/wave-harmonic/crest/blob/db0658ff0b2e93e4a9e28cc2867509658b0ecc00/crest/Assets/Crest/Crest/Scripts/Shapes/ShapeGerstnerBatched.cs) | `UpdateBatch` packs four-wide wave data and phases; `GerstnerBatch` supplies wavelength and transition weights. Target-point parameters do not describe a finite angular sector. |
| [Shaders/OceanInputs/GerstnerShared.hlsl](https://github.com/wave-harmonic/crest/blob/db0658ff0b2e93e4a9e28cc2867509658b0ecc00/crest/Assets/Crest/Crest/Shaders/OceanInputs/GerstnerShared.hlsl) | `ComputeGerstner` weights preferred directional components and applies depth attenuation. Direct Towards Point has a nonzero directional weight floor, unlike a compact sector. |
| [Scripts/Shapes/ShapeGerstnerSplineHandling.cs](https://github.com/wave-harmonic/crest/blob/db0658ff0b2e93e4a9e28cc2867509658b0ecc00/crest/Assets/Crest/Crest/Scripts/Shapes/ShapeGerstnerSplineHandling.cs) | `GenerateMeshFromSpline`/`UpdateMesh` build a ribbon with UV direction, shoreline distance and custom weight data. This Unity mesh/transform workflow is unnecessary here. |
| [Shaders/OceanInputs/AnimWavesGerstnerBatchGeometry.shader](https://github.com/wave-harmonic/crest/blob/db0658ff0b2e93e4a9e28cc2867509658b0ecc00/crest/Assets/Crest/Crest/Shaders/OceanInputs/AnimWavesGerstnerBatchGeometry.shader) | Geometry input multiplies wave displacement by UV-edge/vertex-color feather and rasterizes additively. |
| [Shaders/OceanInputs/Resources/AnimWavesGerstnerGeometry.shader](https://github.com/wave-harmonic/crest/blob/db0658ff0b2e93e4a9e28cc2867509658b0ecc00/crest/Assets/Crest/Crest/Shaders/OceanInputs/Resources/AnimWavesGerstnerGeometry.shader) | Geometry direction and front/back feather control sampled wave-buffer contributions; shallow attenuation multiplies the result. |
| [Shaders/OceanInputs/AnimWavesSpectrum.shader](https://github.com/wave-harmonic/crest/blob/db0658ff0b2e93e4a9e28cc2867509658b0ecc00/crest/Assets/Crest/Crest/Shaders/OceanInputs/AnimWavesSpectrum.shader) | Direction-map resampling, rotated displacement, feathering, additive source and multiplicative Blend passes. The sector must not rotate radial displacement this way. |
| [Scripts/LodData/LodDataMgrAnimWaves.cs](https://github.com/wave-harmonic/crest/blob/db0658ff0b2e93e4a9e28cc2867509658b0ecc00/crest/Assets/Crest/Crest/Scripts/LodData/LodDataMgrAnimWaves.cs) | `FilterWavelength` and composition ordering underpin the unchanged project's direct/final field integration. |

These sources inform weighting, feathering and composition. They do not implement
this exact finite sector-constrained radial packet. No new Crest code was copied.
This model is not a plane-front packet, Kelvin wake solver or Dynamic Waves simulation.

## Public parameters and compatibility

`GerstnerWavePacketInput` adds three init-only properties:

| Property | Default | Valid values |
|---|---|---|
| `DirectionXZ` | `Vector2.Right` | Finite, nonzero vector; normalized when packed |
| `SectorHalfAngleDegrees` | 180 | Finite, 1–180 inclusive |
| `AngularFeatherDegrees` | 10 | Finite, 0–half-angle inclusive |

All prior properties and validation limits remain. A full circle bypasses angular
attenuation explicitly, even on the opposite axis and with a 180° feather.
`TriggerGerstnerPacket()` and the original radial diagnostic buttons remain unchanged.
The same create/update/remove/clear APIs and eight-packet limit are used.

```csharp
var packet = new GerstnerWavePacketInput {
    WorldPositionXZ = new Vector2(20, -15),
    StartTime = runtime.OceanTime,
    DirectionXZ = new Vector2(0, 1), // +Z, 90 degrees
    SectorHalfAngleDegrees = 45,    // 90-degree-wide sector
    AngularFeatherDegrees = 10,
};
long id = runtime.CreateGerstnerPacket(packet);
runtime.UpdateGerstnerPacket(id, packet with { DirectionXZ = Vector2.Left });
```

World heading is 0° → +X, 90° → +Z, 180° → -X, 270° → -Z.
Normalization uses double intermediates before storing float components, so any
finite nonzero float vector, including subnormal and near-maximum components,
can be prepared without squared-length overflow/underflow. The record is not mutated.

## Exact GPU layout and mathematics

Existing descriptor stride stays **80 bytes**; all scalar elements are 32 bits:

| Byte offsets | Packet interpretation |
|---|---|
| 0, 4, 8, 12 | origin X, origin Z, start time, lifetime |
| 16, 20, 24, 28 | wavelength, amplitude, crest count, chop |
| 32, 36, 40, 44 | initial phase, fade-in, fade-out, attenuation |
| 48, 52, 56, 60 | normalized axis X, normalized axis Z, outer cosine, inner cosine |
| 64, 68, 72, 76 | uint placement=0, blend=0, operation=3, flags |

Flags bit 1 (`2u`) is packet-only `FullCircle`. Bit 0 remains the existing
non-packet invert flag. For half-angle exactly 180, CPU packing sets FullCircle;
it is not inferred from a cosine rounded near -1. Other input types retain their
existing byte meanings and flag handling. The same 5120-byte storage buffer,
uniform sets, pipeline and 64-byte push constants are reused.

For H=half-angle and F=feather, CPU packing computes:

`outerCos = cos(H π/180)`; `innerCos = cos((H-F) π/180)`.

The validated F≤H means the inner angle is nonnegative. After existing radial
support/origin guards, the GPU forms `d = (worldXZ-origin)/r` and
`alignment = clamp(dot(d, normalizedAxis), -1, 1)`.

- FullCircle: preserve the old radial envelope without angular multiplication.
- Distinct thresholds: `angularWeight = smoothstep(outerCos, innerCos, alignment)`.
- Zero feather or coincident float thresholds: explicit `step(outerCos, alignment)`.
- Zero angular weight: early out before radial envelope/depth/carrier evaluation.
- Otherwise multiply the existing envelope by angularWeight for **all XYZ**.

The sector axis selects allowed radial directions; displacement still points along
`d`. The original `k*r - omega*elapsed + phase`, `omega=sqrt(g*k)`, group speed
`omega/(2*k)`, radial envelope, lifetime fades, origin guard and depth formula are
unchanged. No per-texel angular cosine/arccosine, additional texture fetch or second
Gerstner evaluation is introduced.

## Validation

Godot 4.7.2 Mono, Forward Plus / Vulkan 1.4.305, AMD Radeon Graphics (RADV RENOIR),
Ryzen 7 5825U integrated-GPU environment.

| Check | Result |
|---|---|
| `dotnet build --no-restore` | Passed, 0 warnings, 0 errors |
| Godot shader import | Completed, exit 0; Vulkan pipeline creation and compute use also passed |
| Unmodified Stage 5E-1 baseline run | 71 checks passed |
| Extended Vulkan regression | **162 checks passed: all 71 old checks plus 91 new checks** |
| Original tests/oracle source | `RunChecks` and `EvaluatePacket` retained verbatim |
| Full-circle compatibility | Old single/overlap capture PNG SHA-256 hashes identical before/after; full-circle axis/feather changes also produced exactly identical queried values |
| Resource cleanup | Regression exited 0 with GPU resource release and no runtime warnings/errors; see smoke-test qualification below |
| Existing diagnostic scene smoke | `World/Ocean/node_3d.tscn`, 120 frames: GPU initialization, 384²×8 canonical field, 100 surface tiles and diagnostic field binding succeeded; both attempts exited 134 after logging GPU release; isolated Stage 5E-1 baseline also crashed (exit 139) |
| Pre-existing user files | All 67 recorded SHA-256 hashes unchanged |

The headless editor import separately emitted the same category of editor shutdown
CanvasItem/ObjectDB/dummy-texture leak messages seen in Stage 5E-1. Neither the
isolated Vulkan regression nor the production diagnostic scene smoke emitted those
warnings. However, both full-scene smoke attempts aborted at native shutdown. A
separate managed checkout of the starting commit, rebuilt with the same local
scene/assets, reproduced the crash after GPU release (exit 139). A debugger run of
the sector version stopped at a native Godot SIGSEGV during shutdown. This is a
baseline scene/engine teardown limitation, not a clean smoke-test pass; the exact
cause is not diagnosed and no unrelated lifecycle code was changed. The isolated
packet regression completed and released its resources with exit 0.

New GPU-backed checks cover full circle, H=90 semicircle, H=20 narrow sector,
all four cardinal axes, normalization, invalid direction/angles, zero feather,
coincident thresholds, H=1, feather inner/mid/outer boundaries, origin handling,
XYZ inverse queries, direction updates on the same handle, original expiration,
overlap, snapped camera movement, stack scaling, last-pair alpha=0.5, depth,
Global FFT, Directional FFT, local pre inputs, post Blend, disable and zero-input work.
Queries remain sparse batches of 241 points; the CPU angular oracle is diagnostic-only
and wraps the unchanged radial oracle. No full-grid displacement readback is used.

Measured maximum query/oracle differences (meters):

| Case | Max error | Acceptance tolerance |
|---|---:|---:|
| Full circle vs old radial oracle | 0.01802 | 0.040 |
| Half circle | 0.01874 | 0.040 |
| Narrow sector | 0.01788 | 0.040 |
| Feathered sector | 0.01808 | 0.040 |
| XYZ with inverse horizontal query | 0.01438 | 0.055 |
| Near origin | 0.00542 | 0.025 |
| Overlap | 0.02316 | 0.055 |
| Last-two-LOD transition | 0.02736 | 0.065 |

Tolerances allow existing RGBA16F quantization, 8–16 carrier samples, bilinear
resampling across spatial LODs, angular sampling and four-step query inversion.
Original tolerances were not changed. Hard-edge tests sample away from the boundary;
the H=1 test asserts finite interior signal and no opposite leakage rather than
claiming sub-texel geometric accuracy. A 0.5° sweep through positive feather measured
a maximum neighboring displacement change of 0.00992 m (limit 0.02 m).

Canonical grayscale captures were actually viewed: `packet-full-circle.png`,
`packet-90-degree-sector.png` (H=45), `packet-narrow-sector.png` (H=20), and
`packet-directional-overlap.png`. They show the expected finite radial trains,
angular restriction and smooth feather. An additional `packet-half-circle.png`
(H=90) was also captured and inspected. Files are written to `OCEAN_PACKET_CAPTURE_DIR`; this run used
`/tmp/stage5e2-validation`. The original captures remain there too.
These are screenshots of the existing GPU field view, not CPU-generated wave images.

Reproduce from the repository, using the installed Godot Mono executable:

```sh
dotnet build --no-restore
godot --headless --editor --path . --import
OCEAN_PACKET_CAPTURE_DIR=/tmp/stage5e2-validation godot --path . \
  --rendering-method forward_plus --rendering-driver vulkan \
  --resolution 700x730 res://Ocean/Debug/GerstnerPacketValidation.tscn
godot --path . --rendering-method forward_plus --rendering-driver vulkan \
  --quit-after 120 res://World/Ocean/node_3d.tscn
```

Diagnostics → Radial Gerstner packet now adds direction angle (default 0°),
half-angle (45°), angular feather (10°), and a directional trigger. The feather
control follows the half-angle maximum. Existing radial buttons, FFT isolation,
2D field inspection and physics markers remain available without restarting.

## Resources and performance

Additional persistent GPU textures: **0**. Buffers: **0**. Compute pipelines: **0**.
Descriptor stride, upload byte count, packet capacity, push constants, ownership
and destruction code are unchanged. Descriptors are still uploaded once per composed
frame when inputs are active, at 80 bytes per descriptor. Packing adds a bounded
CPU normalization and up to two cosines per active packet per upload; no heap
allocation or spatial grid evaluation is added to production code.

Measured input-pass dispatch counters, fenced in the diagnostic only:

- One eligible LOD with a sector: 3 dispatches / 3 frames.
- Sector plus ordinary pre input: 3 / 3, sharing the existing dispatch.
- Expired sectors: 0 / 3; removed sectors: 0 / 3.

The conservative full radial AABB and wavelength×0.25 selection are intentionally
unchanged. The final-pair path still expects up to two bounded LOD dispatches.
Additional GPU ALU is a dot/clamp, threshold branch, smoothstep (or hard comparison)
and envelope multiply for sector samples. Full circles skip angular evaluation;
sector exterior samples can skip later packet math. Logical texture bandwidth and
allocated resources are unchanged. **No isolated GPU timing or CPU allocation
profile was measured, and no performance improvement is claimed.**

## Changed files and limitations

Only six Stage 5E-2 files:

1. `Waves/AnimatedWaves/GerstnerWavePacketInput.cs`: properties and validation.
2. `Waves/AnimatedWaves/AnimatedWaveInputPass.cs`: reuse spare descriptor bytes.
3. `Shaders/Waves/animated_wave_inputs.glsl`: angular attenuation in `radial_packet`.
4. `Debug/OceanDiagnosticDiagnosticsPanel.cs`: compact sector controls.
5. `Debug/GerstnerPacketValidation.cs`: additional tests/captures; old tests intact.
6. This report.

Runtime manager, registry, composer, ShapeCombine, LOD math, depth formula, renderer,
FFT kernels, query shaders and unrelated user files are unchanged.

Zero feather deliberately requests a discontinuous analytic boundary; positive
resolvable feather is smooth. Extremely tiny positive feather can quantize to
coincident cosine thresholds and safely falls back to a hard boundary. Finite
texture sampling softens boundaries, and very narrow sectors can be under-resolved.
Existing finite LOD coverage, steep-wave inversion and float-clock limitations remain.
This is a sector-constrained radial wave model, not a Kelvin wake solver; no refraction,
diffraction, energy correction, new simulation history or Dynamic Waves are included.
