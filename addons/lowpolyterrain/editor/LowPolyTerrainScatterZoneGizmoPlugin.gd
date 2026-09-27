@tool
extends EditorNode3DGizmoPlugin


const ISLAND_SCATTER_ZONE_SCRIPT := preload(
	"res://addons/lowpolyterrain/IslandScatterZone.gd"
)


const HANDLE_POS_X: int = 0
const HANDLE_NEG_X: int = 1
const HANDLE_POS_Y: int = 2
const HANDLE_NEG_Y: int = 3
const HANDLE_POS_Z: int = 4
const HANDLE_NEG_Z: int = 5

const MIN_VOLUME_SIZE: float = 0.05
const EPSILON: float = 0.000001


func _init() -> void:
	create_material(
		"box",
		Color(0.10, 0.85, 1.0, 0.95),
		false,
		true
	)

	create_handle_material("handles")


func _get_gizmo_name() -> String:
	return "LowPoly Scatter Zone"


func _has_gizmo(for_node_3d: Node3D) -> bool:
	return (
		for_node_3d != null
		and for_node_3d.get_script() == ISLAND_SCATTER_ZONE_SCRIPT
	)


func _redraw(gizmo: EditorNode3DGizmo) -> void:
	gizmo.clear()

	var zone: Node3D = gizmo.get_node_3d()

	if zone == null:
		return

	if zone.get_script() != ISLAND_SCATTER_ZONE_SCRIPT:
		return

	var size: Vector3 = zone.get("volume_size")

	size = Vector3(
		maxf(absf(size.x), MIN_VOLUME_SIZE),
		maxf(absf(size.y), MIN_VOLUME_SIZE),
		maxf(absf(size.z), MIN_VOLUME_SIZE)
	)

	var half_size: Vector3 = size * 0.5
	var lines: PackedVector3Array = _build_box_lines(half_size)

	gizmo.add_lines(
		lines,
		get_material("box", gizmo),
		false
	)

	gizmo.add_collision_segments(lines)

	var handles := PackedVector3Array([
		Vector3(half_size.x, 0.0, 0.0),
		Vector3(-half_size.x, 0.0, 0.0),

		Vector3(0.0, half_size.y, 0.0),
		Vector3(0.0, -half_size.y, 0.0),

		Vector3(0.0, 0.0, half_size.z),
		Vector3(0.0, 0.0, -half_size.z),
	])

	var ids := PackedInt32Array([
		HANDLE_POS_X,
		HANDLE_NEG_X,
		HANDLE_POS_Y,
		HANDLE_NEG_Y,
		HANDLE_POS_Z,
		HANDLE_NEG_Z,
	])

	gizmo.add_handles(
		handles,
		get_material("handles", gizmo),
		ids
	)


func _get_handle_name(
	gizmo: EditorNode3DGizmo,
	handle_id: int,
	secondary: bool
) -> String:
	match handle_id:
		HANDLE_POS_X:
			return "Volume +X"
		HANDLE_NEG_X:
			return "Volume -X"
		HANDLE_POS_Y:
			return "Volume +Y"
		HANDLE_NEG_Y:
			return "Volume -Y"
		HANDLE_POS_Z:
			return "Volume +Z"
		HANDLE_NEG_Z:
			return "Volume -Z"

	return "Volume Size"


func _get_handle_value(
	gizmo: EditorNode3DGizmo,
	handle_id: int,
	secondary: bool
) -> Variant:
	var zone: Node3D = gizmo.get_node_3d()

	if zone == null:
		return Vector3.ONE

	return zone.get("volume_size")


func _set_handle(
	gizmo: EditorNode3DGizmo,
	handle_id: int,
	secondary: bool,
	camera: Camera3D,
	screen_pos: Vector2
) -> void:
	var zone: Node3D = gizmo.get_node_3d()

	if zone == null:
		return

	var local_axis: Vector3 = _get_handle_axis(handle_id)

	if local_axis == Vector3.ZERO:
		return

	var handle_sign: float = _get_handle_sign(handle_id)

	# Includes possible Node3D scaling.
	var world_axis_vector: Vector3 = (
		zone.global_transform.basis * local_axis
	)

	var axis_scale: float = world_axis_vector.length()

	if axis_scale <= EPSILON:
		return

	var world_axis: Vector3 = world_axis_vector / axis_scale

	var ray_origin: Vector3 = camera.project_ray_origin(screen_pos)
	var ray_direction: Vector3 = (
		camera.project_ray_normal(screen_pos).normalized()
	)

	# Build a plane which:
	#
	# - contains the resize axis;
	# - faces the editor camera as much as possible.
	#
	# This is more stable than raw 3D line-line closest-point dragging.
	var camera_forward: Vector3 = (
		-camera.global_transform.basis.z.normalized()
	)

	var plane_normal: Vector3 = (
		camera_forward
		- world_axis * camera_forward.dot(world_axis)
	)

	if plane_normal.length_squared() <= EPSILON:
		var camera_up: Vector3 = (
			camera.global_transform.basis.y.normalized()
		)

		plane_normal = (
			camera_up
			- world_axis * camera_up.dot(world_axis)
		)

	if plane_normal.length_squared() <= EPSILON:
		var camera_right: Vector3 = (
			camera.global_transform.basis.x.normalized()
		)

		plane_normal = (
			camera_right
			- world_axis * camera_right.dot(world_axis)
		)

	if plane_normal.length_squared() <= EPSILON:
		return

	plane_normal = plane_normal.normalized()

	var denominator: float = ray_direction.dot(plane_normal)

	if absf(denominator) <= EPSILON:
		return

	var axis_origin: Vector3 = zone.global_position

	var ray_distance: float = (
		(axis_origin - ray_origin).dot(plane_normal)
		/ denominator
	)

	if ray_distance < 0.0:
		return

	var world_hit: Vector3 = (
		ray_origin
		+ ray_direction * ray_distance
	)

	var signed_world_distance: float = (
		(world_hit - axis_origin).dot(world_axis)
	)

	# Convert world distance back into the zone's local coordinates.
	var half_extent: float = (
		signed_world_distance
		* handle_sign
		/ axis_scale
	)

	half_extent = maxf(
		half_extent,
		MIN_VOLUME_SIZE * 0.5
	)

	var new_size: Vector3 = zone.get("volume_size")
	var full_extent: float = half_extent * 2.0

	match handle_id:
		HANDLE_POS_X, HANDLE_NEG_X:
			new_size.x = full_extent

		HANDLE_POS_Y, HANDLE_NEG_Y:
			new_size.y = full_extent

		HANDLE_POS_Z, HANDLE_NEG_Z:
			new_size.z = full_extent

	zone.set("volume_size", new_size)
	zone.update_gizmos()


func _commit_handle(
	gizmo: EditorNode3DGizmo,
	handle_id: int,
	secondary: bool,
	restore: Variant,
	cancel: bool
) -> void:
	var zone: Node3D = gizmo.get_node_3d()

	if zone == null:
		return

	if not (restore is Vector3):
		return

	var old_size: Vector3 = restore
	var new_size: Vector3 = zone.get("volume_size")

	if cancel:
		zone.set("volume_size", old_size)
		zone.update_gizmos()
		return

	if old_size.distance_to(new_size) <= 0.00001:
		return

	var undo_redo: EditorUndoRedoManager = (
		EditorInterface.get_editor_undo_redo()
	)

	undo_redo.create_action(
		"Resize Scatter Zone",
		UndoRedo.MERGE_ENDS,
		zone
	)

	undo_redo.add_do_property(
		zone,
		"volume_size",
		new_size
	)

	undo_redo.add_do_method(
		zone,
		"update_gizmos"
	)

	# Rebuild only once after releasing the handle.
	undo_redo.add_do_method(
		zone,
		"build"
	)

	undo_redo.add_undo_property(
		zone,
		"volume_size",
		old_size
	)

	undo_redo.add_undo_method(
		zone,
		"update_gizmos"
	)

	undo_redo.add_undo_method(
		zone,
		"build"
	)

	undo_redo.commit_action()


func _get_handle_axis(handle_id: int) -> Vector3:
	match handle_id:
		HANDLE_POS_X, HANDLE_NEG_X:
			return Vector3.RIGHT

		HANDLE_POS_Y, HANDLE_NEG_Y:
			return Vector3.UP

		HANDLE_POS_Z, HANDLE_NEG_Z:
			return Vector3.BACK

	return Vector3.ZERO


func _get_handle_sign(handle_id: int) -> float:
	match handle_id:
		HANDLE_POS_X, HANDLE_POS_Y, HANDLE_POS_Z:
			return 1.0

		HANDLE_NEG_X, HANDLE_NEG_Y, HANDLE_NEG_Z:
			return -1.0

	return 1.0


func _build_box_lines(
	half_size: Vector3
) -> PackedVector3Array:
	var corners: Array[Vector3] = [
		Vector3(-half_size.x, -half_size.y, -half_size.z),
		Vector3(half_size.x, -half_size.y, -half_size.z),
		Vector3(half_size.x, -half_size.y, half_size.z),
		Vector3(-half_size.x, -half_size.y, half_size.z),

		Vector3(-half_size.x, half_size.y, -half_size.z),
		Vector3(half_size.x, half_size.y, -half_size.z),
		Vector3(half_size.x, half_size.y, half_size.z),
		Vector3(-half_size.x, half_size.y, half_size.z),
	]

	var edges: Array[Vector2i] = [
		Vector2i(0, 1),
		Vector2i(1, 2),
		Vector2i(2, 3),
		Vector2i(3, 0),

		Vector2i(4, 5),
		Vector2i(5, 6),
		Vector2i(6, 7),
		Vector2i(7, 4),

		Vector2i(0, 4),
		Vector2i(1, 5),
		Vector2i(2, 6),
		Vector2i(3, 7),
	]

	var lines := PackedVector3Array()

	for edge in edges:
		lines.push_back(corners[edge.x])
		lines.push_back(corners[edge.y])

	return lines
