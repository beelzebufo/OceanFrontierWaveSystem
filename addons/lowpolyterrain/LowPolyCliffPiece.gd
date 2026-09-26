@tool
extends MeshInstance3D
class_name LowPolyCliffPiece


## Cheap editor-authored low-poly cliff attachment.
##
## Local axes:
##   X = along the cliff
##   Y = up
##   Z = outward from the terrain
##
## The origin is at the bottom centre. Place it at the cliff base, rotate Y so
## local +Z points away from the terrain, then shape Width / Height / Depth /
## Overhang in the Inspector.
##
## Geometry is generated only when parameters change. Runtime receives an
## ordinary ArrayMesh + Material; there is no per-frame procedural work.


# =============================================================================
# TERRAIN / MATERIAL
# =============================================================================

@export_group("Terrain / Material")

## Optional explicit terrain reference.
## Can be left empty when this node is under LowPolyTerrainManager / Terrain_Assets.
@export var detail_terrain: LowPolyTerrainManager:
	set(value):
		detail_terrain = value
		_apply_material()


## Reuse LowPolyTerrainManager.custom_material.
## terrain_detail.gdshader uses world-space projection, so no UV unwrap is needed.
@export var inherit_terrain_material: bool = true:
	set(value):
		inherit_terrain_material = value
		_apply_material()


## Fallback / override when inheritance is disabled or no terrain can be resolved.
@export var cliff_material: Material:
	set(value):
		cliff_material = value
		_apply_material()


# =============================================================================
# SHAPE
# =============================================================================

@export_group("Shape")

@export_range(0.25, 200.0, 0.05, "or_greater")
var width: float = 8.0:
	set(value):
		width = maxf(value, 0.25)
		_request_rebuild()


@export_range(0.25, 200.0, 0.05, "or_greater")
var height: float = 10.0:
	set(value):
		height = maxf(value, 0.25)
		_request_rebuild()


## Solid thickness measured along local Z.
@export_range(0.10, 50.0, 0.05, "or_greater")
var depth: float = 2.5:
	set(value):
		depth = maxf(value, 0.10)
		_request_rebuild()


## Forward displacement of the upper part along local +Z.
## Positive values create an overhang; negative values lean into the terrain.
@export_range(-50.0, 50.0, 0.05)
var overhang: float = 0.0:
	set(value):
		overhang = value
		_request_rebuild()


## Normalized height where the overhang begins.
## 0 = the whole piece leans.
## 0.55 = lower 55% remains essentially vertical.
@export_range(0.0, 0.95, 0.01)
var overhang_start: float = 0.45:
	set(value):
		overhang_start = clampf(value, 0.0, 0.95)
		_request_rebuild()


## Additional outward bulge around the middle of the face.
@export_range(-20.0, 20.0, 0.05)
var middle_bulge: float = 0.35:
	set(value):
		middle_bulge = value
		_request_rebuild()


## Width multiplier at the bottom and top.
@export_range(0.20, 2.0, 0.01)
var bottom_width_scale: float = 1.0:
	set(value):
		bottom_width_scale = clampf(value, 0.20, 2.0)
		_request_rebuild()


@export_range(0.20, 2.0, 0.01)
var top_width_scale: float = 0.88:
	set(value):
		top_width_scale = clampf(value, 0.20, 2.0)
		_request_rebuild()


# =============================================================================
# GEOMETRY
# =============================================================================

@export_group("Geometry")

## 2-4 is normally enough. More segments increase silhouette freedom but are
## rarely useful for the intended low-poly style.
@export_range(1, 12, 1)
var horizontal_segments: int = 3:
	set(value):
		horizontal_segments = clampi(value, 1, 12)
		_request_rebuild()


@export_range(1, 12, 1)
var vertical_segments: int = 3:
	set(value):
		vertical_segments = clampi(value, 1, 12)
		_request_rebuild()


## 0 = perfectly regular extrusion.
## Adds controlled silhouette and face variation without increasing triangle count.
@export_range(0.0, 1.0, 0.01)
var irregularity: float = 0.22:
	set(value):
		irregularity = clampf(value, 0.0, 1.0)
		_request_rebuild()


@export var seed: int = 4103:
	set(value):
		seed = value
		_request_rebuild()


# =============================================================================
# PLACEMENT
# =============================================================================

@export_group("Placement")

## Applied only by the Snap Base To Terrain button.
## Positive values bury the attachment slightly into the heightfield.
@export_range(-20.0, 20.0, 0.05)
var sink_into_terrain: float = 0.35


@export_tool_button("Snap Base To Terrain", "Mesh")
var snap_button: Callable = snap_base_to_terrain


# =============================================================================
# PRESETS / BUILD
# =============================================================================

@export_group("Presets")

@export_tool_button("Preset: Wall", "Mesh")
var wall_preset_button: Callable = apply_wall_preset

@export_tool_button("Preset: Overhang", "Mesh")
var overhang_preset_button: Callable = apply_overhang_preset

@export_tool_button("Preset: Shelf", "Mesh")
var shelf_preset_button: Callable = apply_shelf_preset


@export_group("Build")

@export_tool_button("Rebuild Cliff", "Mesh")
var rebuild_button: Callable = rebuild


var _rebuild_pending: bool = false


# =============================================================================
# LIFECYCLE
# =============================================================================

func _ready() -> void:
	rebuild()


func rebuild() -> void:
	_rebuild_pending = false
	_build_mesh()
	_apply_material()


func _request_rebuild() -> void:
	if not is_inside_tree():
		return

	if _rebuild_pending:
		return

	_rebuild_pending = true
	call_deferred("_run_pending_rebuild")


func _run_pending_rebuild() -> void:
	_rebuild_pending = false
	_build_mesh()
	_apply_material()


# =============================================================================
# TERRAIN / MATERIAL
# =============================================================================

func _resolve_terrain() -> LowPolyTerrainManager:
	if detail_terrain != null:
		return detail_terrain

	var current: Node = get_parent()

	while current != null:
		if current is LowPolyTerrainManager:
			return current as LowPolyTerrainManager

		current = current.get_parent()

	return null


func _apply_material() -> void:
	var terrain := _resolve_terrain()

	if (
		inherit_terrain_material
		and terrain != null
		and terrain.custom_material != null
	):
		material_override = terrain.custom_material
		return

	material_override = cliff_material


func snap_base_to_terrain() -> void:
	var terrain := _resolve_terrain()

	if terrain == null:
		push_warning(
			"LowPolyCliffPiece: no LowPolyTerrainManager could be resolved."
		)
		return

	var p: Vector3 = global_position

	p.y = terrain.get_height_at_world_coords(
		p.x,
		p.z
	) - sink_into_terrain

	global_position = p


# =============================================================================
# PRESETS
# =============================================================================

func apply_wall_preset() -> void:
	overhang = 0.0
	overhang_start = 0.50
	middle_bulge = 0.30
	bottom_width_scale = 1.00
	top_width_scale = 0.90
	horizontal_segments = 3
	vertical_segments = 3
	irregularity = 0.20
	_request_rebuild()


func apply_overhang_preset() -> void:
	overhang = maxf(depth * 1.10, 1.5)
	overhang_start = 0.48
	middle_bulge = maxf(depth * 0.16, 0.20)
	bottom_width_scale = 1.00
	top_width_scale = 0.92
	horizontal_segments = 3
	vertical_segments = 4
	irregularity = 0.24
	_request_rebuild()


func apply_shelf_preset() -> void:
	height = maxf(height * 0.45, 2.0)
	depth = maxf(depth, 2.0)
	overhang = maxf(depth * 1.75, 3.0)
	overhang_start = 0.30
	middle_bulge = maxf(depth * 0.10, 0.15)
	bottom_width_scale = 0.92
	top_width_scale = 1.05
	horizontal_segments = 3
	vertical_segments = 3
	irregularity = 0.18
	_request_rebuild()


# =============================================================================
# MESH GENERATION
# =============================================================================

func _build_mesh() -> void:
	var hs: int = clampi(horizontal_segments, 1, 12)
	var vs: int = clampi(vertical_segments, 1, 12)

	var stride: int = hs + 1
	var point_count: int = stride * (vs + 1)


	var front := PackedVector3Array()
	var back := PackedVector3Array()

	front.resize(point_count)
	back.resize(point_count)


	var rng := RandomNumberGenerator.new()
	rng.seed = seed


	# One width perturbation per horizontal row gives a jagged silhouette
	# without allowing individual vertices to cross their neighbours.
	var row_width_noise := PackedFloat32Array()
	row_width_noise.resize(vs + 1)

	for y in range(vs + 1):
		row_width_noise[y] = rng.randf_range(-1.0, 1.0)


	# Interior column offsets keep broad faces from becoming a perfect grid.
	var column_offset := PackedFloat32Array()
	column_offset.resize(hs + 1)

	var nominal_segment_width: float = width / float(hs)

	for x in range(hs + 1):
		if x == 0 or x == hs:
			column_offset[x] = 0.0
		else:
			column_offset[x] = (
				rng.randf_range(-1.0, 1.0)
				* irregularity
				* nominal_segment_width
				* 0.28
			)


	# Interior row offsets do the same vertically while keeping the bottom
	# exactly at Y=0 and the top exactly at Y=height.
	var row_height_offset := PackedFloat32Array()
	row_height_offset.resize(vs + 1)

	var nominal_segment_height: float = height / float(vs)

	for y in range(vs + 1):
		if y == 0 or y == vs:
			row_height_offset[y] = 0.0
		else:
			row_height_offset[y] = (
				rng.randf_range(-1.0, 1.0)
				* irregularity
				* nominal_segment_height
				* 0.24
			)


	# Front/back depth noise is independent, but bounded by thickness.
	var front_depth_noise := PackedFloat32Array()
	var back_depth_noise := PackedFloat32Array()

	front_depth_noise.resize(point_count)
	back_depth_noise.resize(point_count)

	for i in range(point_count):
		front_depth_noise[i] = rng.randf_range(-1.0, 1.0)
		back_depth_noise[i] = rng.randf_range(-1.0, 1.0)


	var depth_noise_amplitude: float = (
		minf(
			depth * 0.22,
			minf(
				nominal_segment_width,
				nominal_segment_height
			) * 0.18
		)
		* irregularity
	)


	for y in range(vs + 1):
		var v: float = float(y) / float(vs)

		var width_scale: float = lerpf(
			bottom_width_scale,
			top_width_scale,
			v
		)

		width_scale *= (
			1.0
			+ row_width_noise[y]
			* irregularity
			* 0.10
		)

		width_scale = maxf(width_scale, 0.10)


		var overhang_t: float = clampf(
			(v - overhang_start) / maxf(1.0 - overhang_start, 0.001),
			0.0,
			1.0
		)

		overhang_t = (
			overhang_t
			* overhang_t
			* (3.0 - 2.0 * overhang_t)
		)


		var centre_z: float = (
			overhang * overhang_t
			+ middle_bulge * sin(v * PI)
		)


		var row_y: float = (
			v * height
			+ row_height_offset[y]
		)


		for x in range(hs + 1):
			var u: float = float(x) / float(hs)

			var row_x: float = (
				(u - 0.5)
				* width
				* width_scale
				+ column_offset[x]
			)

			var index: int = y * stride + x


			var front_z: float = (
				centre_z
				+ depth * 0.5
				+ front_depth_noise[index]
				* depth_noise_amplitude
			)

			var back_z: float = (
				centre_z
				- depth * 0.5
				+ back_depth_noise[index]
				* depth_noise_amplitude
				* 0.55
			)


			# Preserve a real solid even with extreme small dimensions.
			var minimum_thickness: float = maxf(
				depth * 0.18,
				0.02
			)

			if front_z < back_z + minimum_thickness:
				front_z = back_z + minimum_thickness


			front[index] = Vector3(
				row_x,
				row_y,
				front_z
			)

			back[index] = Vector3(
				row_x,
				row_y,
				back_z
			)


	var vertices := PackedVector3Array()
	var normals := PackedVector3Array()


	# -------------------------------------------------------------------------
	# FRONT (+Z)
	# -------------------------------------------------------------------------

	for y in range(vs):
		for x in range(hs):
			var p00: Vector3 = front[y * stride + x]
			var p10: Vector3 = front[y * stride + x + 1]
			var p01: Vector3 = front[(y + 1) * stride + x]
			var p11: Vector3 = front[(y + 1) * stride + x + 1]

			# Project convention: clockwise is the visible face.
			_add_quad_flat(
				p00,
				p01,
				p11,
				p10,
				vertices,
				normals
			)


	# -------------------------------------------------------------------------
	# BACK (-Z)
	# -------------------------------------------------------------------------

	for y in range(vs):
		for x in range(hs):
			var p00: Vector3 = back[y * stride + x]
			var p10: Vector3 = back[y * stride + x + 1]
			var p01: Vector3 = back[(y + 1) * stride + x]
			var p11: Vector3 = back[(y + 1) * stride + x + 1]

			_add_quad_flat(
				p00,
				p10,
				p11,
				p01,
				vertices,
				normals
			)


	# -------------------------------------------------------------------------
	# TOP (+Y)
	# -------------------------------------------------------------------------

	for x in range(hs):
		var back_left: Vector3 = back[vs * stride + x]
		var back_right: Vector3 = back[vs * stride + x + 1]
		var front_left: Vector3 = front[vs * stride + x]
		var front_right: Vector3 = front[vs * stride + x + 1]

		_add_quad_flat(
			back_left,
			back_right,
			front_right,
			front_left,
			vertices,
			normals
		)


	# -------------------------------------------------------------------------
	# BOTTOM (-Y)
	# -------------------------------------------------------------------------

	for x in range(hs):
		var back_left: Vector3 = back[x]
		var back_right: Vector3 = back[x + 1]
		var front_left: Vector3 = front[x]
		var front_right: Vector3 = front[x + 1]

		_add_quad_flat(
			back_left,
			front_left,
			front_right,
			back_right,
			vertices,
			normals
		)


	# -------------------------------------------------------------------------
	# LEFT (-X)
	# -------------------------------------------------------------------------

	for y in range(vs):
		var back_bottom: Vector3 = back[y * stride]
		var back_top: Vector3 = back[(y + 1) * stride]
		var front_bottom: Vector3 = front[y * stride]
		var front_top: Vector3 = front[(y + 1) * stride]

		_add_quad_flat(
			back_bottom,
			back_top,
			front_top,
			front_bottom,
			vertices,
			normals
		)


	# -------------------------------------------------------------------------
	# RIGHT (+X)
	# -------------------------------------------------------------------------

	for y in range(vs):
		var back_bottom: Vector3 = back[y * stride + hs]
		var back_top: Vector3 = back[(y + 1) * stride + hs]
		var front_bottom: Vector3 = front[y * stride + hs]
		var front_top: Vector3 = front[(y + 1) * stride + hs]

		_add_quad_flat(
			back_bottom,
			front_bottom,
			front_top,
			back_top,
			vertices,
			normals
		)


	var arrays: Array = []
	arrays.resize(Mesh.ARRAY_MAX)

	arrays[Mesh.ARRAY_VERTEX] = vertices
	arrays[Mesh.ARRAY_NORMAL] = normals


	var result := ArrayMesh.new()

	result.add_surface_from_arrays(
		Mesh.PRIMITIVE_TRIANGLES,
		arrays
	)

	mesh = result


# =============================================================================
# FLAT TRIANGLES
# =============================================================================

func _add_quad_flat(
	a: Vector3,
	b: Vector3,
	c: Vector3,
	d: Vector3,
	vertices: PackedVector3Array,
	normals: PackedVector3Array
) -> void:
	_add_triangle_flat(
		a,
		b,
		c,
		vertices,
		normals
	)

	_add_triangle_flat(
		a,
		c,
		d,
		vertices,
		normals
	)


func _add_triangle_flat(
	a: Vector3,
	b: Vector3,
	c: Vector3,
	vertices: PackedVector3Array,
	normals: PackedVector3Array
) -> void:
	# Project/Godot convention here matches LowPolyRockMesh:
	# triangle winding is kept for cull_back, while the visible normal is the
	# reverse of the mathematical (b-a)x(c-a) cross.
	var normal: Vector3 = (c - a).cross(b - a)

	if normal.length_squared() <= 0.0000001:
		return

	normal = normal.normalized()


	vertices.append(a)
	vertices.append(b)
	vertices.append(c)

	normals.append(normal)
	normals.append(normal)
	normals.append(normal)
