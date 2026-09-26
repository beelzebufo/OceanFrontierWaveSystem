@tool
extends Node3D
class_name IslandScatterLayer


const GENERATED_NAME: String = "_GeneratedScatter"


# =============================================================================
# SOURCE
# =============================================================================

@export_group("Source")

@export var terrain: LowPolyTerrainManager

## GrassTuftMesh, LowPolyRockMesh, etc.
@export var source_mesh: Mesh

## grass_wind / rock_detail material.
@export var material: Material


# =============================================================================
# DENSITY
# =============================================================================

@export_group("Density")

## Initial candidate density over the whole scatter area.
##
## Final probability:
##
## candidates_per_square_meter
## × slope_density
## × surface_density
##
## Examples:
## Grass: 1.0 - 4.0
## Rocks: 0.02 - 0.20
@export_range(0.0, 50.0, 0.01)
var candidates_per_square_meter: float = 1.0


## Safety limit against accidental huge bake.
@export_range(1, 500000, 1)
var max_instances: int = 50000


@export var seed: int = 12345


# =============================================================================
# AREA
# =============================================================================

@export_group("Area")

## World-space X/Z rectangle centered on this IslandScatterLayer.
@export var area_size: Vector2 = Vector2(20.0, 20.0)


# =============================================================================
# HEIGHT
# =============================================================================

@export_group("Height Filter")

@export var min_height: float = 0.5
@export var max_height: float = 1000.0


# =============================================================================
# SLOPE
# =============================================================================

@export_group("Slope Density")

## Hard limits.
## Outside this range density is zero.
@export_range(0.0, 89.0, 0.5)
var min_slope_degrees: float = 0.0

@export_range(0.0, 89.0, 0.5)
var max_slope_degrees: float = 40.0


## Between these values slope density = 1.
##
## min_slope -> full_density_min:
## density rises smoothly 0 -> 1.
##
## full_density_max -> max_slope:
## density falls smoothly 1 -> 0.
@export_range(0.0, 89.0, 0.5)
var full_density_min_slope: float = 0.0

@export_range(0.0, 89.0, 0.5)
var full_density_max_slope: float = 25.0


# =============================================================================
# SURFACE
# =============================================================================

@export_group("Surface Density")

## Base = unpainted terrain.
##
## Paint 1..4 correspond directly to existing terrain paint channels.
@export_flags("Base", "Paint 1", "Paint 2", "Paint 3", "Paint 4")
var allowed_surfaces: int = 1


## Minimum total contribution of allowed surfaces.
##
## Example:
## 0.25 means at least 25% of this terrain point must belong
## to one or more allowed surface types.
@export_range(0.0, 1.0, 0.05)
var minimum_allowed_surface_weight: float = 0.25


## Per-surface density multipliers.
##
## Allowed Surfaces = hard permission.
## These values = soft density.
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


# =============================================================================
# INSTANCE VARIATION
# =============================================================================

@export_group("Instance Variation")

@export_range(0.1, 10.0, 0.01)
var min_scale: float = 0.75

@export_range(0.1, 10.0, 0.01)
var max_scale: float = 1.25


## Random rotation around world Y.
@export var random_y_rotation: bool = true


# =============================================================================
# BUILD
# =============================================================================

@export_group("Build")

@export_tool_button("Build Scatter", "MultiMeshInstance3D")
var build_button: Callable = build

@export_tool_button("Clear Scatter", "Remove")
var clear_button: Callable = clear


# =============================================================================
# BUILD
# =============================================================================

func build() -> void:
	clear()

	if terrain == null:
		push_warning("IslandScatterLayer: Terrain is not assigned.")
		return

	if source_mesh == null:
		push_warning("IslandScatterLayer: Source Mesh is not assigned.")
		return

	if candidates_per_square_meter <= 0.0:
		return


	var width: float = maxf(absf(area_size.x), 0.0)
	var depth: float = maxf(absf(area_size.y), 0.0)

	var area: float = width * depth

	if area <= 0.0001:
		return


	var candidate_count: int = ceili(
		area * candidates_per_square_meter
	)

	if candidate_count <= 0:
		return


	var rng := RandomNumberGenerator.new()
	rng.seed = seed


	var transforms: Array[Transform3D] = []
	var custom_data: Array[Color] = []


	var center_world: Vector3 = global_position

	var half_x: float = width * 0.5
	var half_z: float = depth * 0.5


	var lower_height: float = minf(
		min_height,
		max_height
	)

	var upper_height: float = maxf(
		min_height,
		max_height
	)


	# =========================================================================
	# CANDIDATES
	# =========================================================================

	for candidate_index in range(candidate_count):
		if transforms.size() >= max_instances:
			break


		var world_x: float = (
			center_world.x
			+ rng.randf_range(-half_x, half_x)
		)

		var world_z: float = (
			center_world.z
			+ rng.randf_range(-half_z, half_z)
		)


		# ---------------------------------------------------------------------
		# TERRAIN BOUNDS
		# ---------------------------------------------------------------------

		if not terrain.is_inside_terrain(world_x, world_z):
			continue


		# ---------------------------------------------------------------------
		# HEIGHT
		# ---------------------------------------------------------------------

		var world_y: float = terrain.get_height_at_world_coords(
			world_x,
			world_z
		)

		if world_y < lower_height:
			continue

		if world_y > upper_height:
			continue


		# ---------------------------------------------------------------------
		# SLOPE
		# ---------------------------------------------------------------------

		var surface_normal: Vector3 = _get_surface_normal(
			world_x,
			world_z
		)

		var slope_degrees: float = _get_slope_degrees_from_normal(
			surface_normal
		)

		var slope_density: float = _get_slope_density(
			slope_degrees
		)

		if slope_density <= 0.0:
			continue


		# ---------------------------------------------------------------------
		# SURFACE
		# ---------------------------------------------------------------------

		var surface_density: float = _get_surface_density(
			world_x,
			world_z
		)

		if surface_density <= 0.0:
			continue


		# ---------------------------------------------------------------------
		# FINAL DENSITY
		# ---------------------------------------------------------------------

		var acceptance_probability: float = clampf(
			slope_density * surface_density,
			0.0,
			1.0
		)

		if rng.randf() > acceptance_probability:
			continue


		# ---------------------------------------------------------------------
		# POSITION
		# ---------------------------------------------------------------------

		var world_position := Vector3(
			world_x,
			world_y,
			world_z
		)

		var local_position: Vector3 = to_local(
			world_position
		)


		# ---------------------------------------------------------------------
		# ROTATION
		# ---------------------------------------------------------------------

		var angle: float = 0.0

		if random_y_rotation:
			angle = rng.randf_range(
				0.0,
				TAU
			)


		# ---------------------------------------------------------------------
		# SCALE
		# ---------------------------------------------------------------------

		var scale_low: float = minf(
			min_scale,
			max_scale
		)

		var scale_high: float = maxf(
			min_scale,
			max_scale
		)

		var scale_value: float = rng.randf_range(
			scale_low,
			scale_high
		)


		var basis := Basis(
			Vector3.UP,
			angle
		)

		basis = basis.scaled(
			Vector3.ONE * scale_value
		)


		transforms.append(
			Transform3D(
				basis,
				local_position
			)
		)


		# ---------------------------------------------------------------------
		# INSTANCE CUSTOM DATA
		# ---------------------------------------------------------------------
		#
		# Grass shader:
		# R = random phase
		# G = random wind multiplier
		# B = random colour variation
		#
		# Rock shader:
		# B = random colour variation
		#

		custom_data.append(
			Color(
				rng.randf(),
				rng.randf(),
				rng.randf(),
				1.0
			)
		)


	# =========================================================================
	# CREATE MULTIMESH
	# =========================================================================

	if transforms.is_empty():
		print(
			"IslandScatterLayer: no instances passed filters."
		)
		return


	var generated := MultiMeshInstance3D.new()
	generated.name = GENERATED_NAME

	add_child(generated)


	if Engine.is_editor_hint():
		var scene_root: Node = get_tree().edited_scene_root

		if scene_root != null:
			generated.owner = scene_root


	var multimesh := MultiMesh.new()

	multimesh.transform_format = MultiMesh.TRANSFORM_3D
	multimesh.use_custom_data = true
	multimesh.mesh = source_mesh
	multimesh.instance_count = transforms.size()


	for i in range(transforms.size()):
		multimesh.set_instance_transform(
			i,
			transforms[i]
		)

		multimesh.set_instance_custom_data(
			i,
			custom_data[i]
		)


	generated.multimesh = multimesh
	generated.material_override = material


	print(
		"IslandScatterLayer: ",
		transforms.size(),
		" instances from ",
		candidate_count,
		" candidates. Area = ",
		area,
		" m²."
	)


# =============================================================================
# CLEAR
# =============================================================================

func clear() -> void:
	var old: Node = get_node_or_null(
		GENERATED_NAME
	)

	if old != null:
		old.free()


# =============================================================================
# SLOPE DENSITY
# =============================================================================

func _get_slope_density(
	slope_degrees: float
) -> float:

	var hard_min: float = minf(
		min_slope_degrees,
		max_slope_degrees
	)

	var hard_max: float = maxf(
		min_slope_degrees,
		max_slope_degrees
	)


	if slope_degrees < hard_min:
		return 0.0

	if slope_degrees > hard_max:
		return 0.0


	var full_min: float = clampf(
		full_density_min_slope,
		hard_min,
		hard_max
	)

	var full_max: float = clampf(
		full_density_max_slope,
		full_min,
		hard_max
	)


	var low_density: float = 1.0

	if full_min > hard_min:
		low_density = _smoothstep_range(
			hard_min,
			full_min,
			slope_degrees
		)


	var high_density: float = 1.0

	if full_max < hard_max:
		high_density = (
			1.0
			- _smoothstep_range(
				full_max,
				hard_max,
				slope_degrees
			)
		)


	return clampf(
		low_density * high_density,
		0.0,
		1.0
	)


func _get_slope_degrees_from_normal(
	normal: Vector3
) -> float:

	var up_dot: float = clampf(
		normal.dot(Vector3.UP),
		0.0,
		1.0
	)

	return rad_to_deg(
		acos(up_dot)
	)


# =============================================================================
# SURFACE DENSITY
# =============================================================================

func _get_surface_density(
	world_x: float,
	world_z: float
) -> float:

	var paint: Color = _sample_paint(
		world_x,
		world_z
	)


	# Anything not occupied by Paint 1..4 belongs to Base.
	var base_weight: float = clampf(
		1.0
		- (
			paint.r
			+ paint.g
			+ paint.b
			+ paint.a
		),
		0.0,
		1.0
	)


	var allowed_weight: float = 0.0
	var weighted_density: float = 0.0


	# -------------------------------------------------------------------------
	# BASE
	# -------------------------------------------------------------------------

	if (allowed_surfaces & (1 << 0)) != 0:
		allowed_weight += base_weight

		weighted_density += (
			base_weight
			* base_surface_density
		)


	# -------------------------------------------------------------------------
	# PAINT 1
	# -------------------------------------------------------------------------

	if (allowed_surfaces & (1 << 1)) != 0:
		allowed_weight += paint.r

		weighted_density += (
			paint.r
			* paint1_surface_density
		)


	# -------------------------------------------------------------------------
	# PAINT 2
	# -------------------------------------------------------------------------

	if (allowed_surfaces & (1 << 2)) != 0:
		allowed_weight += paint.g

		weighted_density += (
			paint.g
			* paint2_surface_density
		)


	# -------------------------------------------------------------------------
	# PAINT 3
	# -------------------------------------------------------------------------

	if (allowed_surfaces & (1 << 3)) != 0:
		allowed_weight += paint.b

		weighted_density += (
			paint.b
			* paint3_surface_density
		)


	# -------------------------------------------------------------------------
	# PAINT 4
	# -------------------------------------------------------------------------

	if (allowed_surfaces & (1 << 4)) != 0:
		allowed_weight += paint.a

		weighted_density += (
			paint.a
			* paint4_surface_density
		)


	# Wrong terrain type: reject completely.
	if allowed_weight < minimum_allowed_surface_weight:
		return 0.0


	# Deliberately NOT normalized by allowed_weight.
	#
	# Example:
	# 60% allowed grass surface
	# 40% forbidden rock
	#
	# => naturally around 60% density at the boundary.
	return clampf(
		weighted_density,
		0.0,
		1.0
	)


# =============================================================================
# TERRAIN NORMAL
# =============================================================================

func _get_surface_normal(
	world_x: float,
	world_z: float
) -> Vector3:

	var grid: Vector2 = _get_grid_position(
		world_x,
		world_z
	)


	var max_x: float = float(
		terrain.world_chunks.x
		* terrain.chunk_size
	)

	var max_z: float = float(
		terrain.world_chunks.y
		* terrain.chunk_size
	)


	var gx0: float = clampf(
		grid.x - 1.0,
		0.0,
		max_x
	)

	var gx1: float = clampf(
		grid.x + 1.0,
		0.0,
		max_x
	)

	var gz0: float = clampf(
		grid.y - 1.0,
		0.0,
		max_z
	)

	var gz1: float = clampf(
		grid.y + 1.0,
		0.0,
		max_z
	)


	var cell: float = terrain.cell_size


	var px0_local := Vector3(
		gx0 * cell,
		terrain.sample_grid_height(
			gx0,
			grid.y
		),
		-grid.y * cell
	)

	var px1_local := Vector3(
		gx1 * cell,
		terrain.sample_grid_height(
			gx1,
			grid.y
		),
		-grid.y * cell
	)


	var pz0_local := Vector3(
		grid.x * cell,
		terrain.sample_grid_height(
			grid.x,
			gz0
		),
		-gz0 * cell
	)

	var pz1_local := Vector3(
		grid.x * cell,
		terrain.sample_grid_height(
			grid.x,
			gz1
		),
		-gz1 * cell
	)


	var px0: Vector3 = terrain.global_transform * px0_local
	var px1: Vector3 = terrain.global_transform * px1_local

	var pz0: Vector3 = terrain.global_transform * pz0_local
	var pz1: Vector3 = terrain.global_transform * pz1_local


	var tangent_x: Vector3 = px1 - px0
	var tangent_z: Vector3 = pz1 - pz0


	var normal: Vector3 = tangent_x.cross(
		tangent_z
	)


	if normal.length_squared() < 0.000001:
		return Vector3.UP


	normal = normal.normalized()


	if normal.y < 0.0:
		normal = -normal


	return normal


# =============================================================================
# PAINT SAMPLE
# =============================================================================

func _sample_paint(
	world_x: float,
	world_z: float
) -> Color:

	var grid: Vector2 = _get_grid_position(
		world_x,
		world_z
	)


	var max_x: int = (
		terrain.world_chunks.x
		* terrain.chunk_size
	)

	var max_z: int = (
		terrain.world_chunks.y
		* terrain.chunk_size
	)


	var x0: int = clampi(
		floori(grid.x),
		0,
		max_x
	)

	var z0: int = clampi(
		floori(grid.y),
		0,
		max_z
	)


	var x1: int = mini(
		x0 + 1,
		max_x
	)

	var z1: int = mini(
		z0 + 1,
		max_z
	)


	var tx: float = clampf(
		grid.x - float(x0),
		0.0,
		1.0
	)

	var tz: float = clampf(
		grid.y - float(z0),
		0.0,
		1.0
	)


	var p00: Color = terrain.get_paint_at(
		x0,
		z0
	)

	var p10: Color = terrain.get_paint_at(
		x1,
		z0
	)

	var p01: Color = terrain.get_paint_at(
		x0,
		z1
	)

	var p11: Color = terrain.get_paint_at(
		x1,
		z1
	)


	var row0: Color = p00.lerp(
		p10,
		tx
	)

	var row1: Color = p01.lerp(
		p11,
		tx
	)


	return row0.lerp(
		row1,
		tz
	)


# =============================================================================
# GRID POSITION
# =============================================================================

func _get_grid_position(
	world_x: float,
	world_z: float
) -> Vector2:

	var local: Vector3 = terrain.to_local(
		Vector3(
			world_x,
			0.0,
			world_z
		)
	)


	return Vector2(
		local.x / terrain.cell_size,
		-local.z / terrain.cell_size
	)


# =============================================================================
# MATH
# =============================================================================

func _smoothstep_range(
	edge0: float,
	edge1: float,
	value: float
) -> float:

	if is_equal_approx(edge0, edge1):
		if value >= edge1:
			return 1.0

		return 0.0


	var t: float = clampf(
		(
			value - edge0
		)
		/
		(
			edge1 - edge0
		),
		0.0,
		1.0
	)


	return (
		t
		* t
		* (
			3.0
			- 2.0 * t
		)
	)
