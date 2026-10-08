# Reflection-2B: fixed mean plane

Audit baseline: `a60c49ea5ac0346f8a1498cb7a16da00af90188b`.

## Authority and spatial audit

`OceanRuntime.MeanSeaLevelY` is a **fixed constant, world Y=0**, not an exported
setting or support for moving the ocean vertically. It names the existing spatial
contract. Changing this constant alone is not a supported ocean relocation.

The surface renderer is a Node3D, but its transform is not a mean-level authority:
`animated_wave_surface.gdshader::vertex()` builds the final world vertex from
`vec3(world_xz.x, 0.0, world_xz.y) + displayed_displacement`. It discards the incoming
vertex Y. Sea-floor depth also explicitly assumes a mean level of zero.
`AnimatedWaveSurfaceRenderer.Visual.cs` supplies visual parameters, not an ocean
height offset. Both ocean scenes use this same renderer/runtime architecture.
Planet curvature is a visual deformation; planar reflection remains disabled for it.

Relevant search occurrences are classified as follows:

| Occurrences | Meaning / action |
| --- | --- |
| Runtime's world-Y-zero comment and camera height for LOD | Mean/static plane. LOD now uses `abs(cameraY - MeanSeaLevelY)`. |
| Surface shader vertex base and sea-floor depth calculation | Mean/static Y=0 spatial contract; unchanged. |
| Reflection diagnostic's former `SeaLevel` constant: eligibility, mirror, material | Reflection rendering state derived from the runtime constant; independent constant removed. |
| `reflection_clip_y`, `reflection_clip_bias` in the diagnostic shader/include | Reflection-only material state. Bias remains separate and zero. |
| Serialized clip zeros in `node_3d.tscn` | Harmless defaults: `_Ready()` supplies runtime plane and bias before creating the planar viewport. |
| Underwater camera `GlobalPosition.Y`, query `result.Y`, compositor camera depth | Instantaneous AWF surface comparison and optical depth, not mean level. Unchanged. |
| Point query service and point-query compute shader | Canonical AWF displacement results; Y is instantaneous height under the fixed zero-plane contract. No added offset. |
| `OceanBuoyancy.SeaLevel`, `WeirdBuoyancy.SeaLevel`, water patch `currentSeaLevel` | Per-body flat-water/reference offsets, also added to sampled displacement. They are not authoritative for the rendered ocean or underwater. Unchanged. |
| Physics panel SeaLevel controls and reset placement | Diagnostic controls for those per-body reference offsets, not a global ocean-height feature. |
| `OceanPointQueryDiagnostic.SeaLevel`, base/surface marker positions | Diagnostic zero-plane literals; no runtime plane ownership. |
| Physics panel body `GlobalPosition.Y` and other object/camera transforms | World position/display, not ocean level. |

No `MeanSeaLevel` or `WaterLevel` authority existed before this stage. Automatic
underwater activation requires a valid canonical surface query; no static-Y-zero
classification fallback was found. Forced underwater is an explicit diagnostic
mode. Compositor zero/clamp values for depth are optical safeguards, not a sea plane.

## Data flow and material contract

`OceanRuntime.MeanSeaLevelY` supplies planar eligibility, camera reflection
(`reflectedY = 2 * planeY - cameraY`) and the crossing hull's `reflection_clip_y`.
The immutable authority means the value cannot change between initialization,
process and draw. Clip bias only affects `clipY + bias`, never the mirrored camera.

The accepted draw sequence remains:
`FramePreDraw → GetCameraTransform → mirrored snapshot → GlobalTransform →
ForceUpdateTransform → view from the same snapshot → VP → surface renderer`.

Layer 16 (`32768`) is the camera marker; layer 19 (`262144`) contains planar
candidates; ocean layer 20 (`524288`) is excluded. Main-camera marker clearing and
exact original-mask restoration are unchanged. World position is reconstructed
with `INV_VIEW_MATRIX * vec4(VERTEX, 1)`, so hull pitch/roll does not rotate the plane.

Always-above geometry needs no special clip material. Always-below geometry should
prefer exclusion from candidate layers. Crossing geometry needs this include or
equivalent world-space clipping in every planar-rendered material path. The lower
hull is the proof implementation; no general material conversion is introduced.

## Crest 4 source audit

Inspected public Crest Water 4 commit `db0658ff0b2e93e4a9e28cc2867509658b0ecc00`:

- [OceanRenderer.cs](https://github.com/wave-harmonic/crest/blob/db0658ff0b2e93e4a9e28cc2867509658b0ecc00/crest/Assets/Crest/Crest/Scripts/OceanRenderer.cs): `SeaLevel` follows `Root.position.y`.
- [OceanPlanarReflection.cs](https://github.com/wave-harmonic/crest/blob/db0658ff0b2e93e4a9e28cc2867509658b0ecc00/crest/Assets/Crest/Crest/Scripts/Reflection/OceanPlanarReflection.cs): horizontal plane at the ocean root and a separately stored `_clipPlaneOffset`, initially 0.07 m. In this implementation the offset enters both reflection-plane construction and clipping.

Portable concept: use the ocean's mean/static spatial authority, not instantaneous
waves or a second diagnostic level. Unity oblique projection, culling matrices and
camera setup are engine-specific plumbing and are not ported. This Godot stage
keeps material-only rendering bias at zero, independent of camera mirroring.

## Validation

- `dotnet build OceanFrontierWaveSystem.sln`: passed, 0 warnings, 0 errors.
- Vulkan Forward+ diagnostic scene: GPU ready; no shader/reflection errors logged.
- Temporary external smoke driver selected ALWAYS and Distortion OFF through the
  existing UI signals, then ran 180 frames of rapid camera translation/yaw/pitch
  and hull pitch/roll. Completed before shutdown; no production test hooks added.
- Pixel-level temporal stability, clip boundary and main-camera appearance were
  **not visually verified**. Motion smoke is not a visual acceptance result.
- Shutdown reproduced the known SIGSEGV / code 139 after GPU resources released.
- No nonzero-plane test: arbitrary vertical ocean relocation is unsupported.
- Underwater, physics, AWF, production surface shader and projective UV behavior
  remain unchanged. Reflection-2C is outside this stage.
