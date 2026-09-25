@tool
extends ArrayMesh
class_name GrassTuftMesh


## Один дешёвый объёмный пучок травы.
##
## Geometry:
##   несколько узких ribbons вокруг центра;
##   каждый ribbon имеет несколько сегментов по высоте;
##   корень находится на Y = 0;
##   верх слегка расходится наружу.
##
## UV:
##   U = 0..1 поперёк травинки
##   V = 1 у корня
##   V = 0 у верхушки
##
## Это соответствует текущему grass_wind.gdshader:
## invert_uv_y = false.


@export_group("Blades")

@export_range(3, 12, 1)
var blade_count: int = 6:
	set(value):
		blade_count = clampi(value, 3, 12)
		_rebuild()


## Сегментов ПО ВЫСОТЕ.
##
## 2:
## root -> middle -> top
##
## Для травы обычно достаточно 2.
@export_range(1, 5, 1)
var height_segments: int = 2:
	set(value):
		height_segments = clampi(value, 1, 5)
		_rebuild()


@export_range(0.02, 1.0, 0.01)
var blade_width: float = 0.16:
	set(value):
		blade_width = maxf(value, 0.01)
		_rebuild()


@export_range(0.1, 5.0, 0.05)
var blade_height: float = 0.9:
	set(value):
		blade_height = maxf(value, 0.05)
		_rebuild()


## Насколько узкой становится верхушка.
## 0.15 = верх примерно 15% ширины основания.
@export_range(0.02, 1.0, 0.01)
var tip_width: float = 0.12:
	set(value):
		tip_width = clampf(value, 0.02, 1.0)
		_rebuild()


@export_group("Shape")

## Насколько основания отдельных blades разбросаны
## вокруг центра пучка.
@export_range(0.0, 1.0, 0.01)
var root_spread: float = 0.10:
	set(value):
		root_spread = maxf(value, 0.0)
		_rebuild()


## Насколько верхушки расходятся наружу.
@export_range(0.0, 2.0, 0.01)
var outward_bend: float = 0.18:
	set(value):
		outward_bend = maxf(value, 0.0)
		_rebuild()


## Дополнительный случайный наклон отдельных blades.
@export_range(0.0, 1.0, 0.01)
var random_bend: float = 0.08:
	set(value):
		random_bend = maxf(value, 0.0)
		_rebuild()


## Разброс высоты внутри одного tuft.
@export_range(0.0, 0.8, 0.01)
var height_variation: float = 0.22:
	set(value):
		height_variation = clampf(value, 0.0, 0.8)
		_rebuild()


## Разброс ширины.
@export_range(0.0, 0.8, 0.01)
var width_variation: float = 0.18:
	set(value):
		width_variation = clampf(value, 0.0, 0.8)
		_rebuild()


@export_group("Generation")

## Меняет форму самого базового tuft.
## Все MultiMesh instances всё равно дополнительно
## получают собственные transform/custom data.
@export var seed: int = 3471:
	set(value):
		seed = value
		_rebuild()


var _is_rebuilding: bool = false


func _init() -> void:
	_rebuild()


func _rebuild() -> void:
	if _is_rebuilding:
		return

	_is_rebuilding = true

	clear_surfaces()

	if blade_count <= 0 or height_segments <= 0:
		_is_rebuilding = false
		return


	var vertices := PackedVector3Array()
	var normals := PackedVector3Array()
	var uvs := PackedVector2Array()
	var indices := PackedInt32Array()


	var rng := RandomNumberGenerator.new()
	rng.seed = seed


	for blade_index in range(blade_count):
		_build_blade(
			blade_index,
			rng,
			vertices,
			normals,
			uvs,
			indices
		)


	if vertices.is_empty():
		_is_rebuilding = false
		return


	var arrays: Array = []
	arrays.resize(Mesh.ARRAY_MAX)

	arrays[Mesh.ARRAY_VERTEX] = vertices
	arrays[Mesh.ARRAY_NORMAL] = normals
	arrays[Mesh.ARRAY_TEX_UV] = uvs
	arrays[Mesh.ARRAY_INDEX] = indices


	add_surface_from_arrays(
		Mesh.PRIMITIVE_TRIANGLES,
		arrays
	)


	_is_rebuilding = false


func _build_blade(
	blade_index: int,
	rng: RandomNumberGenerator,
	vertices: PackedVector3Array,
	normals: PackedVector3Array,
	uvs: PackedVector2Array,
	indices: PackedInt32Array
) -> void:

	# Равномерно раскладываем blades вокруг центра,
	# но добавляем небольшую случайность.
	var base_angle: float = (
		TAU *
		float(blade_index) /
		float(blade_count)
	)

	var angle_jitter: float = rng.randf_range(
		-0.22,
		0.22
	)

	var angle := base_angle + angle_jitter


	# Направление, куда смотрит поверхность blade.
	var facing := Vector3(
		cos(angle),
		0.0,
		sin(angle)
	)


	# Направление ширины ribbon.
	var width_direction := Vector3(
		-facing.z,
		0.0,
		facing.x
	)


	var this_height: float = blade_height * rng.randf_range(
		1.0 - height_variation,
		1.0 + height_variation
	)


	var this_width: float = blade_width * rng.randf_range(
		1.0 - width_variation,
		1.0 + width_variation
	)


	# Основание чуть смещено от центра.
	var root_radius := rng.randf_range(
		0.0,
		root_spread
	)


	var root_position := facing * root_radius


	# У каждого blade чуть свой bend.
	var this_bend: float = outward_bend + rng.randf_range(
		-random_bend,
		random_bend
	)

	this_bend = maxf(
		this_bend,
		0.0
	)


	var base_vertex: int = vertices.size()


	# ---------------------------------------------------------------------
	# ROWS
	# ---------------------------------------------------------------------

	# height_segments = 2
	#
	# row 0 = root
	# row 1 = middle
	# row 2 = top

	for row in range(height_segments + 1):
		var t: float = (
			float(row) /
			float(height_segments)
		)


		# Нелинейный изгиб.
		# Корень почти не двигается,
		# верх расходится заметнее.
		var bend_t := t * t


		var center := (
			root_position
			+ Vector3.UP * (this_height * t)
			+ facing * (this_bend * bend_t)
		)


		# Плавно сужаем blade.
		var width_factor := lerpf(
			1.0,
			tip_width,
			t
		)


		var half_width := (
			this_width *
			width_factor *
			0.5
		)


		var left := (
			center
			- width_direction * half_width
		)

		var right := (
			center
			+ width_direction * half_width
		)


		# Приблизительная касательная вдоль изгиба.
		#
		# center(t) =
		# up * height*t +
		# facing * bend*t²
		#
		# derivative:
		# up*height +
		# facing*2*bend*t
		var vertical_tangent := (
			Vector3.UP * this_height
			+ facing * (
				2.0 *
				this_bend *
				t
			)
		)


		var surface_normal := (
			vertical_tangent
			.cross(width_direction)
			.normalized()
		)


		vertices.append(left)
		vertices.append(right)

		normals.append(surface_normal)
		normals.append(surface_normal)


		# Наш grass_wind shader ожидает:
		#
		# V = 1 root
		# V = 0 top
		var uv_v := 1.0 - t

		uvs.append(
			Vector2(
				0.0,
				uv_v
			)
		)

		uvs.append(
			Vector2(
				1.0,
				uv_v
			)
		)


	# ---------------------------------------------------------------------
	# TRIANGLES
	# ---------------------------------------------------------------------

	for segment in range(height_segments):
		var i0 := base_vertex + segment * 2
		var i1 := i0 + 1
		var i2 := i0 + 2
		var i3 := i0 + 3


		indices.append(i0)
		indices.append(i2)
		indices.append(i1)

		indices.append(i1)
		indices.append(i2)
		indices.append(i3)
