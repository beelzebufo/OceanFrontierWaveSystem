@tool
extends Resource
class_name IslandScatterProfile


## Shared authoring profile for one kind of island scatter.
##
## A profile answers WHAT and HOW to spawn.
## IslandScatterZone answers WHERE the profile is allowed to spawn.


@export_group("Profile")

@export var display_name: String = "Scatter"


@export_group("Source")

@export var source_mesh: Mesh = null
@export var material: Material = null


@export_group("Placement Volume")

## Default box size used by the Scatter placement tool.
## Each placed IslandScatterZone copies this value, so zones can be resized later
## without changing every other zone that uses this profile.
@export var volume_size: Vector3 = Vector3(20.0, 20.0, 20.0)

## Vertical offset of the preview/placed box centre relative to the terrain hit point.
@export var volume_center_y_offset: float = 0.0


@export_group("Density")

## Initial candidate density over the local X/Z footprint of the placement volume.
@export_range(0.0, 50.0, 0.01)
var candidates_per_square_meter: float = 1.0

@export_range(1, 500000, 1)
var max_instances: int = 50000

@export var seed: int = 12345


@export_group("Height Filter")

@export var min_height: float = -10000.0
@export var max_height: float = 10000.0


@export_group("Slope Density")

@export_range(0.0, 89.0, 0.5)
var min_slope_degrees: float = 0.0

@export_range(0.0, 89.0, 0.5)
var full_density_min_slope: float = 0.0

@export_range(0.0, 89.0, 0.5)
var full_density_max_slope: float = 25.0

@export_range(0.0, 89.0, 0.5)
var max_slope_degrees: float = 40.0


@export_group("Surface Density")

## Base = unpainted terrain. Paint 1..4 are the existing terrain paint channels.
@export_flags("Base", "Paint 1", "Paint 2", "Paint 3", "Paint 4")
var allowed_surfaces: int = 1

@export_range(0.0, 1.0, 0.05)
var minimum_allowed_surface_weight: float = 0.25

@export_range(0.0, 1.0, 0.05)
var base_surface_density: float = 1.0

@export_range(0.0, 1.0, 0.05)
var paint1_surface_density: float = 1.0

@export_range(0.0, 1.0, 0.05)
var paint2_surface_density: float = 1.0

@export_range(0.0, 1.0, 0.05)
var paint3_surface_density: float = 1.0

@export_range(0.0, 1.0, 0.05)
var paint4_surface_density: float = 1.0


@export_group("Instance Variation")

@export_range(0.1, 10.0, 0.01)
var min_scale: float = 0.75

@export_range(0.1, 10.0, 0.01)
var max_scale: float = 1.25

@export var random_y_rotation: bool = true
