@tool
extends MeshInstance3D
class_name LowPolyTerrainMacroShell


@export_group("Detail Terrain")

## LowPoly terrain whose stored Stitch Seam is the authoritative inner border.
## Can be left empty when this node is a child/descendant of LowPolyTerrainManager.
@export var detail_terrain: LowPolyTerrainManager:
	set(value):
		detail_terrain = value
		_rebind_terrain_signals()
		_request_rebuild()

@export var inherit_detail_material: bool = true:
	set(value):
		inherit_detail_material = value
		_apply_material()

@export var terrain_material: Material:
	set(value):
		terrain_material = value
		_apply_material()


@export_group("Macro Shell")

## Horizontal distance from the authoritative LowPoly Stitch Seam to the deep edge.
@export_range(1.0, 5000.0, 1.0, "or_greater")
var outer_margin_meters: float = 200.0:
	set(value):
		outer_margin_meters = maxf(value, 0.01)
		_request_rebuild()

## Absolute WORLD Y of the outer/deep edge.
@export_range(-5000.0, 5000.0, 1.0)
var deep_floor_y: float = -300.0:
	set(value):
		deep_floor_y = value
		_request_rebuild()

## Number of rings between the detailed terrain seam and the deep edge.
## 1 is the cheapest possible shell. 3-6 is normally enough for a broad seabed slope.
@export_range(1, 32, 1)
var radial_segments: int = 4:
	set(value):
		radial_segments = maxi(value, 1)
		_request_rebuild()

## Optional profile across the shell.
## X: 0 at Stitch Seam, 1 at deep edge.
## Y: 0 keeps seam height, 1 reaches deep_floor_y.
## Null uses smoothstep, giving zero vertical derivative at both ends.
@export var height_profile: Curve:
	set(value):
		height_profile = value
		_request_rebuild()


@export_group("Local Shape")

## X = 0..1 around the whole seam perimeter.
## Y = additional OUTWARD distance in metres.
## Fades to zero at the Stitch Seam automatically.
@export var perimeter_radius_offset: Curve:
	set(value):
		perimeter_radius_offset = value
		_request_rebuild()

## X = 0..1 around the perimeter.
## Y = additional height in metres.
## Fades to zero at the Stitch Seam automatically.
@export var perimeter_height_offset: Curve:
	set(value):
		perimeter_height_offset = value
		_request_rebuild()


@export_group("Stitching")

## Copies the current LowPoly boundary normal onto the shell's first ring.
## The geometry seam itself is always exact because both sides read the same stored Stitch Seam.
@export var match_detail_normals: bool = true:
	set(value):
		match_detail_normals = value
		_request_rebuild()

@export_range(0.1, 1000.0, 0.1, "or_greater")
var uv_meters_per_tile: float = 20.0:
	set(value):
		uv_meters_per_tile = maxf(value, 0.001)
		_request_rebuild()

@export_tool_button("Rebuild From Stitch Seam", "Mesh")
var rebuild_button: Callable = func() -> void:
	rebuild()


var _rebuild_pending: bool = false
var _bound_terrain: LowPolyTerrainManager = null


func _ready() -> void:
	_rebind_terrain_signals()

	# Children become ready before their parent. LowPolyTerrainManager restores active
	# world_chunks/chunk_size/cell_size in its own _ready(), so wait until that has completed.
	call_deferred("rebuild")


func _exit_tree() -> void:
	_unbind_terrain_signals()


func rebuild() -> void:
	_rebuild_pending = false
	_rebuild()


func _request_rebuild() -> void:
	if not is_inside_tree():
		return
	if _rebuild_pending:
		return

	_rebuild_pending = true
	call_deferred("_run_pending_rebuild")


func _run_pending_rebuild() -> void:
	_rebuild_pending = false
	_rebuild()


func _resolve_detail_terrain() -> LowPolyTerrainManager:
	if detail_terrain != null:
		return detail_terrain

	var current: Node = get_parent()
	while current != null:
		if current is LowPolyTerrainManager:
			return current as LowPolyTerrainManager
		current = current.get_parent()

	return null


func _unbind_terrain_signals() -> void:
	if _bound_terrain == null or not is_instance_valid(_bound_terrain):
		_bound_terrain = null
		return

	var seam_callable := Callable(self, "_on_stitch_seam_changed")
	if _bound_terrain.is_connected("signal_stitch_seam_changed", seam_callable):
		_bound_terrain.disconnect("signal_stitch_seam_changed", seam_callable)

	var geometry_callable := Callable(self, "_on_terrain_geometry_changed")
	if _bound_terrain.is_connected("signal_terrain_geometry_changed", geometry_callable):
		_bound_terrain.disconnect("signal_terrain_geometry_changed", geometry_callable)

	_bound_terrain = null


func _rebind_terrain_signals() -> void:
	if not is_inside_tree():
		return

	var resolved := _resolve_detail_terrain()
	if resolved == _bound_terrain:
		return

	_unbind_terrain_signals()
	_bound_terrain = resolved

	if _bound_terrain == null:
		return

	var seam_callable := Callable(self, "_on_stitch_seam_changed")
	if not _bound_terrain.is_connected("signal_stitch_seam_changed", seam_callable):
		_bound_terrain.connect("signal_stitch_seam_changed", seam_callable)

	var geometry_callable := Callable(self, "_on_terrain_geometry_changed")
	if not _bound_terrain.is_connected("signal_terrain_geometry_changed", geometry_callable):
		_bound_terrain.connect("signal_terrain_geometry_changed", geometry_callable)


func _on_stitch_seam_changed(_revision: int) -> void:
	_request_rebuild()


func _on_terrain_geometry_changed() -> void:
	# Stitch positions remain locked, but SMOOTH LowPoly normals can change when the
	# protected inner rings are sculpted. Rebuild this cheap shell once per completed stroke.
	_request_rebuild()


func _rebuild() -> void:
	if not is_inside_tree():
		return

	_rebind_terrain_signals()

	var terrain := _resolve_detail_terrain()
	if terrain == null:
		mesh = null
		return

	var seam_local: PackedVector3Array = terrain.get_stitch_seam_local_positions()
	var boundary_count: int = seam_local.size()
	if boundary_count < 4:
		mesh = null
		return

	var inner_world := PackedVector3Array()
	var inner_normals_world := PackedVector3Array()
	inner_world.resize(boundary_count)
	inner_normals_world.resize(boundary_count)

	for i in range(boundary_count):
		inner_world[i] = terrain.global_transform * seam_local[i]

		var grid_coord: Vector2i = terrain.get_stitch_seam_grid_coord(i)
		inner_normals_world[i] = _detail_grid_normal_world(
			terrain,
			grid_coord.x,
			grid_coord.y
		)

	# Arc-length parameter around the authoritative seam.
	var perimeter_u := PackedFloat32Array()
	perimeter_u.resize(boundary_count)

	var total_perimeter: float = 0.0
	for i in range(boundary_count):
		var next_i: int = (i + 1) % boundary_count
		var a := inner_world[i]
		var b := inner_world[next_i]
		total_perimeter += Vector2(b.x - a.x, b.z - a.z).length()

	if total_perimeter <= 0.000001:
		mesh = null
		return

	var accumulated: float = 0.0
	for i in range(boundary_count):
		perimeter_u[i] = accumulated / total_perimeter
		var next_i: int = (i + 1) % boundary_count
		var a := inner_world[i]
		var b := inner_world[next_i]
		accumulated += Vector2(b.x - a.x, b.z - a.z).length()

	# Seam order is clockwise in XZ. For a clockwise edge tangent, its left side is outward.
	var outward_world := PackedVector2Array()
	outward_world.resize(boundary_count)

	for i in range(boundary_count):
		var previous_i: int = (i - 1 + boundary_count) % boundary_count
		var next_i: int = (i + 1) % boundary_count

		var previous := inner_world[previous_i]
		var next := inner_world[next_i]
		var tangent := Vector2(next.x - previous.x, next.z - previous.z)

		if tangent.length_squared() < 0.000001:
			tangent = Vector2.RIGHT
		else:
			tangent = tangent.normalized()

		outward_world[i] = Vector2(-tangent.y, tangent.x)

	var ring_count: int = radial_segments + 1
	var total_vertices: int = ring_count * boundary_count

	var vertices := PackedVector3Array()
	var normals := PackedVector3Array()
	var uvs := PackedVector2Array()
	vertices.resize(total_vertices)
	normals.resize(total_vertices)
	uvs.resize(total_vertices)

	for ring in range(ring_count):
		var t: float = float(ring) / float(radial_segments)
		var height_alpha: float = _profile_height(t)

		# Both local shape modifiers are exactly zero at the shared seam.
		var deformation_weight: float = t * t * (3.0 - 2.0 * t)

		for i in range(boundary_count):
			var u: float = perimeter_u[i]
			var radius_offset: float = _sample_offset_curve(perimeter_radius_offset, u)
			var height_offset: float = _sample_offset_curve(perimeter_height_offset, u)

			var horizontal_distance: float = (
				outer_margin_meters * t + radius_offset * deformation_weight
			)
			horizontal_distance = maxf(horizontal_distance, 0.0)

			var inner := inner_world[i]
			var outward := outward_world[i]

			var world_position := Vector3(
				inner.x + outward.x * horizontal_distance,
				lerpf(inner.y, deep_floor_y, height_alpha) + height_offset * deformation_weight,
				inner.z + outward.y * horizontal_distance
			)

			var index: int = ring * boundary_count + i
			vertices[index] = to_local(world_position)
			uvs[index] = Vector2(
				world_position.x / uv_meters_per_tile,
				world_position.z / uv_meters_per_tile
			)

	# Same vertex count on every ring keeps topology simple and guarantees that the first ring
	# cannot introduce a T-junction against the detailed heightfield border.
	var indices := PackedInt32Array()
	indices.resize(radial_segments * boundary_count * 6)

	var cursor: int = 0
	for ring in range(radial_segments):
		var current_base: int = ring * boundary_count
		var next_base: int = (ring + 1) * boundary_count

		for i in range(boundary_count):
			var j: int = (i + 1) % boundary_count
			var a: int = current_base + i
			var b: int = current_base + j
			var c: int = next_base + i
			var d: int = next_base + j

			# Project convention: clockwise triangles are the visible top side.
			indices[cursor] = a
			indices[cursor + 1] = b
			indices[cursor + 2] = c
			indices[cursor + 3] = b
			indices[cursor + 4] = d
			indices[cursor + 5] = c
			cursor += 6

	# Smooth shell normals, area weighted.
	for i in range(normals.size()):
		normals[i] = Vector3.ZERO

	for triangle in range(0, indices.size(), 3):
		var ia: int = indices[triangle]
		var ib: int = indices[triangle + 1]
		var ic: int = indices[triangle + 2]
		var a := vertices[ia]
		var b := vertices[ib]
		var c := vertices[ic]

		# Mathematical cross is opposite the visible normal with the project's clockwise winding.
		var face_normal := -((b - a).cross(c - a))
		if face_normal.length_squared() <= 0.000001:
			continue

		normals[ia] += face_normal
		normals[ib] += face_normal
		normals[ic] += face_normal

	for i in range(normals.size()):
		if normals[i].length_squared() <= 0.000001:
			normals[i] = Vector3.UP
		else:
			normals[i] = normals[i].normalized()

	# Position is already exact because both meshes consume the same stored seam.
	# For SMOOTH shading also make the first-ring lighting normal agree with LowPoly.
	if (
		match_detail_normals
		and terrain.shading_mode == LowPolyTerrainManager.ShadingMode.SMOOTH
	):
		for i in range(boundary_count):
			# N_world = inverse(transpose(B)) * N_local, therefore N_local = transpose(B) * N_world.
			normals[i] = (
				global_transform.basis.transposed() * inner_normals_world[i]
			).normalized()

	var arrays: Array = []
	arrays.resize(Mesh.ARRAY_MAX)
	arrays[Mesh.ARRAY_VERTEX] = vertices
	arrays[Mesh.ARRAY_NORMAL] = normals
	arrays[Mesh.ARRAY_TEX_UV] = uvs
	arrays[Mesh.ARRAY_INDEX] = indices

	var result := ArrayMesh.new()
	result.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
	mesh = result
	_apply_material()


func _profile_height(t: float) -> float:
	t = clampf(t, 0.0, 1.0)
	if height_profile != null:
		return clampf(height_profile.sample_baked(t), 0.0, 1.0)

	# Flat derivative at both the LowPoly seam and the future deep-floor join.
	return t * t * (3.0 - 2.0 * t)


func _sample_offset_curve(curve: Curve, u: float) -> float:
	if curve == null:
		return 0.0
	return curve.sample_baked(clampf(u, 0.0, 1.0))


func _detail_grid_normal_world(
	terrain: LowPolyTerrainManager,
	gx: int,
	gz: int
) -> Vector3:
	var vertex_count_x: int = terrain.world_chunks.x * terrain.chunk_size + 1
	var vertex_count_z: int = terrain.world_chunks.y * terrain.chunk_size + 1

	if vertex_count_x <= 0 or vertex_count_z <= 0:
		return Vector3.UP

	var east_x: int = mini(gx + 1, vertex_count_x - 1)
	var west_x: int = maxi(gx - 1, 0)
	var north_z: int = mini(gz + 1, vertex_count_z - 1)
	var south_z: int = maxi(gz - 1, 0)
	var here: float = terrain.get_height_at(gx, gz)

	var east := Vector3(
		terrain.cell_size,
		terrain.get_height_at(east_x, gz) - here,
		0.0
	)
	var north := Vector3(
		0.0,
		terrain.get_height_at(gx, north_z) - here,
		-terrain.cell_size
	)
	var west := Vector3(
		-terrain.cell_size,
		terrain.get_height_at(west_x, gz) - here,
		0.0
	)
	var south := Vector3(
		0.0,
		terrain.get_height_at(gx, south_z) - here,
		terrain.cell_size
	)

	var local_normal := (
		east.cross(north)
		+ north.cross(west)
		+ west.cross(south)
		+ south.cross(east)
	)

	if local_normal.length_squared() <= 0.000001:
		local_normal = Vector3.UP
	else:
		local_normal = local_normal.normalized()

	var terrain_normal_matrix := terrain.global_transform.basis.inverse().transposed()
	return (terrain_normal_matrix * local_normal).normalized()


func _apply_material() -> void:
	var terrain := _resolve_detail_terrain()

	if (
		inherit_detail_material
		and terrain != null
		and terrain.custom_material != null
	):
		material_override = terrain.custom_material
	else:
		material_override = terrain_material
