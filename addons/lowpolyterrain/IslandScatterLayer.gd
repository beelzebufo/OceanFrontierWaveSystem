@tool
extends Node3D
class_name IslandScatterLayer


@export_group("Source")

@export var terrain: LowPolyTerrainManager

## Пока сюда назначаем тот самый PlaneMesh,
## на котором только что проверили траву.
@export var source_mesh: Mesh

## ShaderMaterial с grass_wind.gdshader.
@export var material: Material


@export_group("Placement")

## Сколько экземпляров создать для первого теста.
@export_range(1, 100000, 1)
var instance_count: int = 1000

@export var seed: int = 12345

## Ограничение по мировой высоте.
@export var min_height: float = 0.5
@export var max_height: float = 1000.0

## Случайный масштаб.
@export_range(0.1, 5.0, 0.01)
var min_scale: float = 0.75

@export_range(0.1, 5.0, 0.01)
var max_scale: float = 1.25


@export_group("Test Area")

## Пока специально ограничиваем область,
## чтобы хорошо видеть результат.
@export var area_size: Vector2 = Vector2(20.0, 20.0)


@export_group("Build")

@export_tool_button("Build Scatter", "MultiMeshInstance3D")
var build_button: Callable = build

@export_tool_button("Clear Scatter", "Remove")
var clear_button: Callable = clear


const GENERATED_NAME := "_GeneratedScatter"


func build() -> void:
	clear()

	if terrain == null:
		push_warning("IslandScatterLayer: Terrain is not assigned.")
		return

	if source_mesh == null:
		push_warning("IslandScatterLayer: Source Mesh is not assigned.")
		return

	if instance_count <= 0:
		return


	var generated := MultiMeshInstance3D.new()
	generated.name = GENERATED_NAME

	add_child(generated)

	if Engine.is_editor_hint():
		var scene_root := get_tree().edited_scene_root

		if scene_root != null:
			generated.owner = scene_root


	# -------------------------------------------------------------------------
	# MULTIMESH
	# -------------------------------------------------------------------------

	var multimesh := MultiMesh.new()

	multimesh.transform_format = MultiMesh.TRANSFORM_3D

	# Нужен grass_wind shader:
	# R = random phase
	# G = random wind strength
	# B = random colour
	multimesh.use_custom_data = true

	multimesh.mesh = source_mesh
	multimesh.instance_count = instance_count

	generated.multimesh = multimesh
	generated.material_override = material


	# -------------------------------------------------------------------------
	# RANDOM
	# -------------------------------------------------------------------------

	var rng := RandomNumberGenerator.new()
	rng.seed = seed


	# Центр тестовой области — позиция IslandScatterLayer.
	var center_world := global_position

	var half_x := area_size.x * 0.5
	var half_z := area_size.y * 0.5


	var written: int = 0

	# Некоторые random points могут попасть вне terrain
	# или вне диапазона высот, поэтому даём запас попыток.
	var max_attempts: int = instance_count * 10
	var attempts: int = 0


	while written < instance_count and attempts < max_attempts:
		attempts += 1


		var world_x := center_world.x + rng.randf_range(
				-half_x,
				half_x
			)

		var world_z := center_world.z + rng.randf_range(
				-half_z,
				half_z
			)


		if not terrain.is_inside_terrain(
			world_x,
			world_z
		):
			continue


		var world_y := terrain.get_height_at_world_coords(
				world_x,
				world_z
			)


		if world_y < min_height:
			continue

		if world_y > max_height:
			continue


		var world_position := Vector3(
				world_x,
				world_y,
				world_z
			)


		# MultiMesh transform должен быть локальным
		# относительно IslandScatterLayer.
		var local_position := to_local(world_position)


		# Random rotation around Y.
		var angle := rng.randf_range(
				0.0,
				TAU
			)


		var scale_value := rng.randf_range(
				min_scale,
				max_scale
			)


		var basis := Basis(
				Vector3.UP,
				angle
			)

		basis = basis.scaled(
			Vector3.ONE *
			scale_value
		)


		var instance_transform := Transform3D(
				basis,
				local_position
			)


		multimesh.set_instance_transform(
			written,
			instance_transform
		)


		# grass_wind.gdshader:
		#
		# R = phase
		# G = wind variation
		# B = colour variation
		# A = reserved
		var custom := Color(
				rng.randf(),
				rng.randf(),
				rng.randf(),
				1.0
			)


		multimesh.set_instance_custom_data(
			written,
			custom
		)


		written += 1


	# Не оставляем невидимые пустые instances,
	# если часть random attempts была отвергнута.
	multimesh.visible_instance_count = written


	print(
		"IslandScatterLayer: generated ",
		written,
		" instances."
	)


func clear() -> void:
	var old := get_node_or_null(
		GENERATED_NAME
	)

	if old != null:
		old.free()
