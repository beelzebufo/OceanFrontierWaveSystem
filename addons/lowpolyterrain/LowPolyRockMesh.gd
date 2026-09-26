@tool
extends ArrayMesh
class_name LowPolyRockMesh


## Дешёвый procedural low-poly камень.
##
## Один Mesh создаётся один раз,
## затем IslandScatterLayer размножает его через MultiMesh.
##
## Геометрия специально faceted:
## каждый triangle получает свою normal.


@export_group("Shape")

@export_range(5, 16, 1)
var sides: int = 8:
	set(value):
		sides = clampi(value, 5, 16)
		_rebuild()


@export_range(0.1, 5.0, 0.05)
var width: float = 1.0:
	set(value):
		width = maxf(value, 0.05)
		_rebuild()


@export_range(0.1, 5.0, 0.05)
var depth: float = 0.85:
	set(value):
		depth = maxf(value, 0.05)
		_rebuild()


@export_range(0.1, 5.0, 0.05)
var height: float = 0.7:
	set(value):
		height = maxf(value, 0.05)
		_rebuild()


## Насколько камень сужается сверху.
@export_range(0.05, 1.0, 0.01)
var top_scale: float = 0.52:
	set(value):
		top_scale = clampf(value, 0.05, 1.0)
		_rebuild()


## Насколько камень сужается у основания.
@export_range(0.05, 1.2, 0.01)
var bottom_scale: float = 0.78:
	set(value):
		bottom_scale = clampf(value, 0.05, 1.2)
		_rebuild()


@export_group("Irregularity")

## Неровность радиуса.
@export_range(0.0, 0.8, 0.01)
var radial_variation: float = 0.22:
	set(value):
		radial_variation = clampf(value, 0.0, 0.8)
		_rebuild()


## Насколько вершины кольца могут гулять по Y.
@export_range(0.0, 0.5, 0.01)
var vertical_variation: float = 0.10:
	set(value):
		vertical_variation = clampf(value, 0.0, 0.5)
		_rebuild()


## Скручивает верхнюю часть относительно основания.
@export_range(-1.0, 1.0, 0.01)
var twist: float = 0.16:
	set(value):
		twist = clampf(value, -1.0, 1.0)
		_rebuild()


## Сдвиг верхушки относительно центра.
@export_range(0.0, 1.0, 0.01)
var top_offset: float = 0.16:
	set(value):
		top_offset = clampf(value, 0.0, 1.0)
		_rebuild()


@export_group("Generation")

@export var seed: int = 9127:
	set(value):
		seed = value
		_rebuild()


var _rebuilding := false


func _init() -> void:
	_rebuild()


func _rebuild() -> void:
	if _rebuilding:
		return

	_rebuilding = true
	clear_surfaces()


	var rng := RandomNumberGenerator.new()
	rng.seed = seed


	var bottom_ring := PackedVector3Array()
	var middle_ring := PackedVector3Array()
	var upper_ring := PackedVector3Array()


	# Один random radius на направление.
	# Это сохраняет связную форму по высоте.
	var radius_random: Array[float] = []

	for i in range(sides):
		radius_random.append(
			rng.randf_range(
				1.0 - radial_variation,
				1.0 + radial_variation
			)
		)


	for i in range(sides):
		var t := float(i) / float(sides)

		var base_angle := t * TAU

		var angle_jitter := rng.randf_range(
			-0.12,
			0.12
		)

		var angle := base_angle + angle_jitter

		var radius_factor: float = radius_random[i]


		# ---------------------------------------------------------
		# BOTTOM
		# ---------------------------------------------------------

		var bottom_radius := radius_factor * bottom_scale

		var bottom_y := rng.randf_range(
				0.0,
				vertical_variation * height
			)

		bottom_ring.append(
			Vector3(
				cos(angle) *
					width *
					0.5 *
					bottom_radius,

				bottom_y,

				sin(angle) *
					depth *
					0.5 *
					bottom_radius
			)
		)


		# ---------------------------------------------------------
		# MIDDLE — widest part
		# ---------------------------------------------------------

		var middle_y := height * 0.42 + rng.randf_range(
				-vertical_variation,
				vertical_variation
			) * height

		middle_ring.append(
			Vector3(
				cos(angle) *
					width *
					0.5 *
					radius_factor,

				middle_y,

				sin(angle) *
					depth *
					0.5 *
					radius_factor
			)
		)


		# ---------------------------------------------------------
		# UPPER
		# ---------------------------------------------------------

		var upper_angle := angle + twist

		var upper_y := height * 0.78 + rng.randf_range(
				-vertical_variation,
				vertical_variation
			) * height

		upper_ring.append(
			Vector3(
				cos(upper_angle) *
					width *
					0.5 *
					radius_factor *
					top_scale,

				upper_y,

				sin(upper_angle) *
					depth *
					0.5 *
					radius_factor *
					top_scale
			)
		)


	# -------------------------------------------------------------
	# TOP / BOTTOM CENTERS
	# -------------------------------------------------------------

	var top_angle := rng.randf_range(
			0.0,
			TAU
		)

	var top_center :=Vector3 (
			cos(top_angle) *
				top_offset *
				width *
				0.5,

			height,

			sin(top_angle) *
				top_offset *
				depth *
				0.5
		)


	var bottom_center := Vector3(
			0.0,
			0.0,
			0.0
		)


	# -------------------------------------------------------------
	# FLAT-SHADED TRIANGLES
	# -------------------------------------------------------------

	var vertices := PackedVector3Array()
	var normals := PackedVector3Array()
	var uvs := PackedVector2Array()


	for i in range(sides):
		var next := (i + 1) % sides


		# Bottom -> middle.
		_add_quad_flat(
			bottom_ring[i],
			bottom_ring[next],
			middle_ring[next],
			middle_ring[i],
			vertices,
			normals,
			uvs
		)


		# Middle -> upper.
		_add_quad_flat(
			middle_ring[i],
			middle_ring[next],
			upper_ring[next],
			upper_ring[i],
			vertices,
			normals,
			uvs
		)


		# Upper -> top.
		_add_triangle_flat(
			upper_ring[i],
			upper_ring[next],
			top_center,
			vertices,
			normals,
			uvs
		)


		# Bottom cap.
		_add_triangle_flat(
			bottom_ring[next],
			bottom_ring[i],
			bottom_center,
			vertices,
			normals,
			uvs
		)


	var arrays: Array = []
	arrays.resize(Mesh.ARRAY_MAX)

	arrays[Mesh.ARRAY_VERTEX] = vertices
	arrays[Mesh.ARRAY_NORMAL] = normals
	arrays[Mesh.ARRAY_TEX_UV] = uvs


	add_surface_from_arrays(
		Mesh.PRIMITIVE_TRIANGLES,
		arrays
	)


	_rebuilding = false


func _add_quad_flat(
	a: Vector3,
	b: Vector3,
	c: Vector3,
	d: Vector3,
	vertices: PackedVector3Array,
	normals: PackedVector3Array,
	uvs: PackedVector2Array
) -> void:

	_add_triangle_flat(
		a,
		b,
		c,
		vertices,
		normals,
		uvs
	)

	_add_triangle_flat(
		a,
		c,
		d,
		vertices,
		normals,
		uvs
	)


func _add_triangle_flat(
	a: Vector3,
	b: Vector3,
	c: Vector3,
	vertices: PackedVector3Array,
	normals: PackedVector3Array,
	uvs: PackedVector2Array
) -> void:

	var normal :=(c - a).cross(b - a).normalized()


	vertices.append(a)
	vertices.append(b)
	vertices.append(c)


	normals.append(normal)
	normals.append(normal)
	normals.append(normal)


	# Пока UV условные.
	# Для камней дальше лучше использовать world/triplanar shader,
	# поэтому нормальная UV-развёртка нам не нужна.
	uvs.append(Vector2(0.0, 0.0))
	uvs.append(Vector2(1.0, 0.0))
	uvs.append(Vector2(0.5, 1.0))
