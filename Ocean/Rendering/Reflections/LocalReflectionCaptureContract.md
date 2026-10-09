# Local reflection capture (2B)

The scene has one local `SubViewport` and one camera. The active registry order
owns at most four disjoint capture tiles: 1×1 for one reflector, 2×1 for two,
and 2×2 for three or four. Each tile keeps the configured per-reflector
resolution. Mirrored proxies are projected into only their owner's tile.

The tile index is the captured object ID. Color and alpha remain the accepted
single-boat capture format: RGB is unshaded source color and alpha is coverage.
Encoding IDs into alpha would make overlapping meshes blend their IDs and
could falsely assign a sample to another reflector. The disjoint tiles make
ownership exact without a second viewport or a second texture.

At render synchronization, the controller refreshes the registry from the
effective main camera, updates proxy transforms, and sends each active center
and radius to the water material. The water shader tests world-XZ influence
in registry order and samples only that reflector's tile. A missing local
pixel contributes zero local weight, leaving the existing global planar and
environment fallback path in control. Overlapping influence regions resolve
by the registry's priority, distance, and registration order.

Proxy meshes are discovered when an active set is created. Selection changes
can rebuild those sets; steady frames reuse all proxy nodes, materials, the
camera, and the viewport. This stage mirrors rigid mesh transforms and source
color, as the accepted diagnostic boat path did; it does not mirror skeletal
deformation or animated material parameters.

## Reflection-3A distortion

Global and local lookups use the same screen-space distortion direction from
the final view-space water normal. Adaptive strength uses the normalized
world-space AWF geometric normal before the visual micro-normal layer. Both
lookups apply the resulting offset in capture-local coordinates. For local
reflection, raw UV validity uses the participant tile's pixel dimensions;
only then is the UV clamped inside that tile and mapped into the shared atlas.
Neither distortion nor breakup changes the world-XZ influence test or the
registry's slot ownership. A lower local weight reveals the existing global
planar/environment fallback.
