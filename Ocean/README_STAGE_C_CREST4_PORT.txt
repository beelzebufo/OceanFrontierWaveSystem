OceanFrontier Stage C - direct Crest Water 4 topology port
==========================================================

Baseline checked before preparing this package:
  OceanFrontierWaterSystem git main
  commit 58e3614cd03061dc25951d2ec07dca9869eaac03 ("rebuild crst")

This package assumes the Stage B renderer split/committed AWF state has already been applied.
It replaces the Stage B geometry/sampling files and the two surface shaders that share the
sampling contract.

Crest reference:
  wave-harmonic/crest
  commit db0658ff0b2e93e4a9e28cc2867509658b0ecc00

WHAT IS DIRECTLY PORTED
-----------------------

OceanBuilder.cs:
  - all 10 Crest PatchType variants
  - BuildOceanPatch skirt math
  - checkerboard triangle topology
  - LOD0 4x4 / 16 patch layout
  - coarser 12 patch ring layout
  - fat/slim patch assignment
  - patch rotations
  - outer FatXOuter/FatXZOuter horizon skirts

OceanVertHelpers.hlsl:
  - ComputeLodAlpha taxicab-distance calculation
  - SnapAndTransitionVertLayout
  - snap to 2 * geometryGridWidth
  - transition phase on 4 * geometryGridWidth
  - minRadius = 0.375
  - LOD0 ViewerAltitudeLevelAlpha contribution

OceanRenderer.cs:
  - baseMeshDensity = resolution * 0.25 / GeometryDownSampleFactor
  - blackPoint = 0.4 / (baseMeshDensity / 8)
  - whiteRange = 1 - blackPoint - blackPoint

WHAT IS GODOT-SPECIFIC PLUMBING ONLY
------------------------------------

  - Crest GameObject/Transform -> Node3D/MeshInstance3D
  - Crest MaterialPropertyBlock/global shader params -> ShaderMaterial params
  - tile object translation comes from MODEL_MATRIX in vertex()
  - canonical AnimatedWaveField and Stage B committed state remain unchanged

NO CHANGES TO
-------------

  FFT spectrum/evolution/IFFT
  AnimatedWave direct placement
  ShapeCombine
  AnimatedWaveField ownership
  point queries / buoyancy
  visual normal-map math
  optics

FILES TO REPLACE / ADD
----------------------

Ocean/Rendering/AnimatedWaveSurfaceRenderer.cs
Ocean/Rendering/AnimatedWaveSurfaceRenderer.Geometry.cs
Ocean/Rendering/AnimatedWaveSurfaceRenderer.Sampling.cs
Ocean/Shaders/Rendering/animated_wave_surface.gdshader
Ocean/Shaders/Rendering/animated_wave_surface_contract.gdshaderinc
Ocean/Shaders/Rendering/animated_wave_normal_debug.gdshader

AnimatedWaveSurfaceRenderer.Visual.cs remains from Stage B and is not included because it is unchanged.

EXPECTED STARTUP LOG
--------------------

At 384 AWF resolution, GeometryDownSampleFactor=2, 8 LODs:

  [Ocean] Crest4 nested surface ready: 8 LODs, 100 tiles, AWF 384²,
          geometry 192², tile density 48, 10 Crest patch types,
          LOD alpha black=0.066667, range=0.866667.

100 tiles is still expected:
  LOD0 = 16
  LOD1..LOD7 = 7 * 12 = 84
  total = 100

VALIDATION
----------

1. dotnet build OceanFrontierWaveSystem.csproj --no-restore
2. Run Nested LOD.
3. Pause ON.
4. Height Only.
5. Follow Camera ON.
6. Move laterally A/D across LOD2->3 and LOD3->4 while watching the same boundary.

The previous diagnostic early-return that disabled topology morph must be gone. The shader now
executes the Crest snap + morph path.

If a shader compile error appears, send the exact Godot shader compiler output before making
any further math/topology changes.
