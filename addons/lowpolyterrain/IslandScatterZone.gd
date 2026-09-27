@tool
extends Node3D
class_name IslandScatterZone


const ISLAND_SCATTER_PROFILE_SCRIPT := preload(
	"res://addons/lowpolyterrain/IslandScatterProfile.gd"
)


## Spatial placement volume for an IslandScatterProfile.
##
## The box is local to this node. Candidate X/Z positions are generated inside the
## box footprint, projected onto LowPolyTerrain, then rejected if the resulting terrain
## point is outside the box in local X/Y/Z.


const GENERATED_NAME: String = "_GeneratedScatter"


@export_group("Source")

@export var terrain: LowPolyTerrainManager = null
@export var profile: Resource = null


@export_group("Volume")

## Independent copy of the profile placement size at the moment this zone is created.
@export var volume_size: Vector3 = Vector3(20.0, 20.0, 20.0):
	set(value):
		volume_size = Vector3(
			maxf(absf(value.x), 0.05),
			maxf(absf(value.y), 0.05),
			maxf(absf(value.z), 0.05)
		)

		if Engine.is_editor_hint() and is_inside_tree():
			update_gizmos()


@export_group("Build")

@export_tool_button("Build Scatter", "MultiMeshInstance3D")
var build_button: Callable = build

@export_tool_button("Clear Scatter", "Remove")
var clear_button: Callable = clear


func build() -> void:
	clear()

	if terrain == null:
		push_warning("IslandScatterZone: Terrain is not assigned.")
		return

	if profile == null:
		push_warning("IslandScatterZone: Profile is not assigned.")
		return

	if profile.get_script() != ISLAND_SCATTER_PROFILE_SCRIPT:
		push_warning("IslandScatterZone: Profile must be an IslandScatterProfile resource.")
		return

	if profile.source_mesh == null:
		push_warning("IslandScatterZone: Profile Source Mesh is not assigned.")
		return

	if profile.candidates_per_square_meter <= 0.0:
		return

	var width: float = maxf(absf(volume_size.x), 0.0)
	var depth: float = maxf(absf(volume_size.z), 0.0)
	var half_size: Vector3 = volume_size * 0.5

	if width <= 0.0001 or depth <= 0.0001:
		return

	# World-space area of the transformed local X/Z rectangle. This remains correct
	# if the zone is rotated around Y or scaled in the editor.
	var world_x_edge: Vector3 = global_transform.basis.x * width
	var world_z_edge: Vector3 = global_transform.basis.z * depth
	var area: float = world_x_edge.cross(world_z_edge).length()

	if area <= 0.0001:
		return

	var candidate_count: int = ceili(area * profile.candidates_per_square_meter)

	if candidate_count <= 0:
		return

	var rng := RandomNumberGenerator.new()
	rng.seed = profile.seed

	var transforms: Array[Transform3D] = []
	var custom_data: Array[Color] = []

	var lower_height: float = minf(profile.min_height, profile.max_height)
	var upper_height: float = maxf(profile.min_height, profile.max_height)

	for candidate_index in range(candidate_count):
		if transforms.size() >= profile.max_instances:
			break

		var local_candidate := Vector3(
			rng.randf_range(-half_size.x, half_size.x),
			0.0,
			rng.randf_range(-half_size.z, half_size.z)
		)

		var candidate_world: Vector3 = global_transform * local_candidate
		var world_x: float = candidate_world.x
		var world_z: float = candidate_world.z

		if not terrain.is_inside_terrain(world_x, world_z):
			continue

		var world_y: float = terrain.get_height_at_world_coords(world_x, world_z)

		if world_y < lower_height or world_y > upper_height:
			continue

		var world_position := Vector3(world_x, world_y, world_z)
		var zone_local: Vector3 = to_local(world_position)

		# The terrain point itself must be inside the full oriented box, including Y.
		if absf(zone_local.x) > half_size.x:
			continue
		if absf(zone_local.y) > half_size.y:
			continue
		if absf(zone_local.z) > half_size.z:
			continue

		var surface_normal: Vector3 = _get_surface_normal(world_x, world_z)
		var slope_degrees: float = _get_slope_degrees_from_normal(surface_normal)
		var slope_density: float = _get_slope_density(slope_degrees)

		if slope_density <= 0.0:
			continue

		var surface_density: float = _get_surface_density(world_x, world_z)

		if surface_density <= 0.0:
			continue

		var acceptance_probability: float = clampf(
			slope_density * surface_density,
			0.0,
			1.0
		)

		if rng.randf() > acceptance_probability:
			continue

		var local_position: Vector3 = to_local(world_position)
		var angle: float = 0.0

		if profile.random_y_rotation:
			angle = rng.randf_range(0.0, TAU)

		var scale_low: float = minf(profile.min_scale, profile.max_scale)
		var scale_high: float = maxf(profile.min_scale, profile.max_scale)
		var scale_value: float = rng.randf_range(scale_low, scale_high)

		var basis := Basis(Vector3.UP, angle)
		basis = basis.scaled(Vector3.ONE * scale_value)

		transforms.append(Transform3D(basis, local_position))
		custom_data.append(Color(rng.randf(), rng.randf(), rng.randf(), 1.0))

	if transforms.is_empty():
		print("IslandScatterZone: no instances passed filters.")
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
	multimesh.mesh = profile.source_mesh
	multimesh.instance_count = transforms.size()

	for i in range(transforms.size()):
		multimesh.set_instance_transform(i, transforms[i])
		multimesh.set_instance_custom_data(i, custom_data[i])

	generated.multimesh = multimesh
	generated.material_override = profile.material

	print(
		"IslandScatterZone: ",
		transforms.size(),
		" instances from ",
		candidate_count,
		" candidates. Volume footprint = ",
		area,
		" m²."
	)


func clear() -> void:
	var old: Node = get_node_or_null(GENERATED_NAME)

	if old != null:
		old.free()


func _get_slope_density(slope_degrees: float) -> float:
	var hard_min: float = minf(profile.min_slope_degrees, profile.max_slope_degrees)
	var hard_max: float = maxf(profile.min_slope_degrees, profile.max_slope_degrees)

	if slope_degrees < hard_min or slope_degrees > hard_max:
		return 0.0

	var full_min: float = clampf(profile.full_density_min_slope, hard_min, hard_max)
	var full_max: float = clampf(profile.full_density_max_slope, full_min, hard_max)
	var low_density: float = 1.0
	var high_density: float = 1.0

	if full_min > hard_min:
		low_density = _smoothstep_range(hard_min, full_min, slope_degrees)

	if full_max < hard_max:
		high_density = 1.0 - _smoothstep_range(full_max, hard_max, slope_degrees)

	return clampf(low_density * high_density, 0.0, 1.0)


func _get_slope_degrees_from_normal(normal: Vector3) -> float:
	var up_dot: float = clampf(normal.dot(Vector3.UP), 0.0, 1.0)
	return rad_to_deg(acos(up_dot))


func _get_surface_density(world_x: float, world_z: float) -> float:
	var paint: Color = _sample_paint(world_x, world_z)
	var base_weight: float = clampf(
		1.0 - (paint.r + paint.g + paint.b + paint.a),
		0.0,
		1.0
	)

	var allowed_weight: float = 0.0
	var weighted_density: float = 0.0

	if (profile.allowed_surfaces & (1 << 0)) != 0:
		allowed_weight += base_weight
		weighted_density += base_weight * profile.base_surface_density

	if (profile.allowed_surfaces & (1 << 1)) != 0:
		allowed_weight += paint.r
		weighted_density += paint.r * profile.paint1_surface_density

	if (profile.allowed_surfaces & (1 << 2)) != 0:
		allowed_weight += paint.g
		weighted_density += paint.g * profile.paint2_surface_density

	if (profile.allowed_surfaces & (1 << 3)) != 0:
		allowed_weight += paint.b
		weighted_density += paint.b * profile.paint3_surface_density

	if (profile.allowed_surfaces & (1 << 4)) != 0:
		allowed_weight += paint.a
		weighted_density += paint.a * profile.paint4_surface_density

	if allowed_weight < profile.minimum_allowed_surface_weight:
		return 0.0

	# Deliberately not normalized by allowed_weight. A partially allowed boundary
	# naturally receives proportionally lower density.
	return clampf(weighted_density, 0.0, 1.0)


func _get_surface_normal(world_x: float, world_z: float) -> Vector3:
	var grid: Vector2 = _get_grid_position(world_x, world_z)
	var max_x: float = float(terrain.world_chunks.x * terrain.chunk_size)
	var max_z: float = float(terrain.world_chunks.y * terrain.chunk_size)
	var gx0: float = clampf(grid.x - 1.0, 0.0, max_x)
	var gx1: float = clampf(grid.x + 1.0, 0.0, max_x)
	var gz0: float = clampf(grid.y - 1.0, 0.0, max_z)
	var gz1: float = clampf(grid.y + 1.0, 0.0, max_z)
	var cell: float = terrain.cell_size

	var px0_local := Vector3(
		gx0 * cell,
		terrain.sample_grid_height(gx0, grid.y),
		-grid.y * cell
	)
	var px1_local := Vector3(
		gx1 * cell,
		terrain.sample_grid_height(gx1, grid.y),
		-grid.y * cell
	)
	var pz0_local := Vector3(
		grid.x * cell,
		terrain.sample_grid_height(grid.x, gz0),
		-gz0 * cell
	)
	var pz1_local := Vector3(
		grid.x * cell,
		terrain.sample_grid_height(grid.x, gz1),
		-gz1 * cell
	)

	var px0: Vector3 = terrain.global_transform * px0_local
	var px1: Vector3 = terrain.global_transform * px1_local
	var pz0: Vector3 = terrain.global_transform * pz0_local
	var pz1: Vector3 = terrain.global_transform * pz1_local
	var tangent_x: Vector3 = px1 - px0
	var tangent_z: Vector3 = pz1 - pz0
	var normal: Vector3 = tangent_x.cross(tangent_z)

	if normal.length_squared() < 0.000001:
		return Vector3.UP

	normal = normal.normalized()

	if normal.y < 0.0:
		normal = -normal

	return normal


func _sample_paint(world_x: float, world_z: float) -> Color:
	var grid: Vector2 = _get_grid_position(world_x, world_z)
	var max_x: int = terrain.world_chunks.x * terrain.chunk_size
	var max_z: int = terrain.world_chunks.y * terrain.chunk_size
	var x0: int = clampi(floori(grid.x), 0, max_x)
	var z0: int = clampi(floori(grid.y), 0, max_z)
	var x1: int = mini(x0 + 1, max_x)
	var z1: int = mini(z0 + 1, max_z)
	var tx: float = clampf(grid.x - float(x0), 0.0, 1.0)
	var tz: float = clampf(grid.y - float(z0), 0.0, 1.0)
	var p00: Color = terrain.get_paint_at(x0, z0)
	var p10: Color = terrain.get_paint_at(x1, z0)
	var p01: Color = terrain.get_paint_at(x0, z1)
	var p11: Color = terrain.get_paint_at(x1, z1)
	var row0: Color = p00.lerp(p10, tx)
	var row1: Color = p01.lerp(p11, tx)
	return row0.lerp(row1, tz)


func _get_grid_position(world_x: float, world_z: float) -> Vector2:
	var local: Vector3 = terrain.to_local(Vector3(world_x, 0.0, world_z))
	return Vector2(local.x / terrain.cell_size, -local.z / terrain.cell_size)


func _smoothstep_range(edge0: float, edge1: float, value: float) -> float:
	if is_equal_approx(edge0, edge1):
		return 1.0 if value >= edge1 else 0.0

	var t: float = clampf((value - edge0) / (edge1 - edge0), 0.0, 1.0)
	return t * t * (3.0 - 2.0 * t)
