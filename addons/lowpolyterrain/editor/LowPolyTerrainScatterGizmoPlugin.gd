@tool
extends EditorNode3DGizmoPlugin
class_name LowPolyTerrainScatterGizmoPlugin


const HANDLE_POS_X: int = 0
const HANDLE_NEG_X: int = 1
const HANDLE_POS_Z: int = 2
const HANDLE_NEG_Z: int = 3
const EDGE_SEGMENTS: int = 12
const HANDLE_HEIGHT_OFFSET: float = 0.35
const MIN_AREA_SIZE: float = 0.10


var _undo_redo: EditorUndoRedoManager = null


func _init() -> void:
	create_material(
		"scatter_area",
		Color(0.10, 0.85, 0.95, 0.95),
		false,
		true
	)
	create_handle_material("scatter_handles")


func set_undo_redo_manager(value: EditorUndoRedoManager) -> void:
	_undo_redo = value


func _get_gizmo_name() -> String:
	return "Island Scatter Area"


func _get_priority() -> int:
	return 2


func _has_gizmo(for_node_3d: Node3D) -> bool:
	return for_node_3d is IslandScatterLayer


func _redraw(gizmo: EditorNode3DGizmo) -> void:
	gizmo.clear()

	var layer: IslandScatterLayer = gizmo.get_node_3d() as IslandScatterLayer
	if layer == null:
		return

	var area := Vector2(
		maxf(absf(layer.area_size.x), MIN_AREA_SIZE),
		maxf(absf(layer.area_size.y), MIN_AREA_SIZE)
	)

	var half_x: float = area.x * 0.5
	var half_z: float = area.y * 0.5
	var center := Vector2(layer.global_position.x, layer.global_position.z)

	var min_x: float = center.x - half_x
	var max_x: float = center.x + half_x
	var min_z: float = center.y - half_z
	var max_z: float = center.y + half_z

	var lines := PackedVector3Array()
	_append_world_edge(layer, lines, Vector2(min_x, min_z), Vector2(max_x, min_z))
	_append_world_edge(layer, lines, Vector2(max_x, min_z), Vector2(max_x, max_z))
	_append_world_edge(layer, lines, Vector2(max_x, max_z), Vector2(min_x, max_z))
	_append_world_edge(layer, lines, Vector2(min_x, max_z), Vector2(min_x, min_z))

	gizmo.add_lines(
		lines,
		get_material("scatter_area", gizmo),
		false
	)

	var handles := PackedVector3Array()
	handles.append(_world_xz_to_local(layer, Vector2(max_x, center.y)))
	handles.append(_world_xz_to_local(layer, Vector2(min_x, center.y)))
	handles.append(_world_xz_to_local(layer, Vector2(center.x, max_z)))
	handles.append(_world_xz_to_local(layer, Vector2(center.x, min_z)))

	var ids := PackedInt32Array([
		HANDLE_POS_X,
		HANDLE_NEG_X,
		HANDLE_POS_Z,
		HANDLE_NEG_Z,
	])

	gizmo.add_handles(
		handles,
		get_material("scatter_handles", gizmo),
		ids
	)


func _append_world_edge(
	layer: IslandScatterLayer,
	lines: PackedVector3Array,
	from_xz: Vector2,
	to_xz: Vector2
) -> void:
	for segment in range(EDGE_SEGMENTS):
		var t0: float = float(segment) / float(EDGE_SEGMENTS)
		var t1: float = float(segment + 1) / float(EDGE_SEGMENTS)
		var p0: Vector2 = from_xz.lerp(to_xz, t0)
		var p1: Vector2 = from_xz.lerp(to_xz, t1)
		lines.append(_world_xz_to_local(layer, p0))
		lines.append(_world_xz_to_local(layer, p1))


func _world_xz_to_local(layer: IslandScatterLayer, world_xz: Vector2) -> Vector3:
	var world_y: float = layer.global_position.y
	var terrain: LowPolyTerrainManager = _resolve_terrain(layer)

	if terrain != null and terrain.is_inside_terrain(world_xz.x, world_xz.y):
		world_y = terrain.get_height_at_world_coords(world_xz.x, world_xz.y)

	return layer.to_local(
		Vector3(
			world_xz.x,
			world_y + HANDLE_HEIGHT_OFFSET,
			world_xz.y
		)
	)


func _resolve_terrain(layer: IslandScatterLayer) -> LowPolyTerrainManager:
	if layer.terrain != null and is_instance_valid(layer.terrain):
		return layer.terrain

	var current: Node = layer.get_parent()
	while current != null:
		if current is LowPolyTerrainManager:
			return current as LowPolyTerrainManager
		current = current.get_parent()

	return null


func _get_handle_name(
	_gizmo: EditorNode3DGizmo,
	handle_id: int,
	_secondary: bool
) -> String:
	if handle_id == HANDLE_POS_X or handle_id == HANDLE_NEG_X:
		return "Scatter Width X"
	return "Scatter Depth Z"


func _get_handle_value(
	gizmo: EditorNode3DGizmo,
	_handle_id: int,
	_secondary: bool
) -> Variant:
	var layer: IslandScatterLayer = gizmo.get_node_3d() as IslandScatterLayer
	if layer == null:
		return Vector2.ZERO
	return layer.area_size


func _set_handle(
	gizmo: EditorNode3DGizmo,
	handle_id: int,
	_secondary: bool,
	camera: Camera3D,
	screen_pos: Vector2
) -> void:
	var layer: IslandScatterLayer = gizmo.get_node_3d() as IslandScatterLayer
	if layer == null:
		return

	var center: Vector3 = layer.global_position
	var axis: Vector3 = Vector3.RIGHT

	if handle_id == HANDLE_POS_Z or handle_id == HANDLE_NEG_Z:
		axis = Vector3.FORWARD

	var ray_from: Vector3 = camera.project_ray_origin(screen_pos)
	var ray_to: Vector3 = ray_from + camera.project_ray_normal(screen_pos) * 100000.0
	var axis_from: Vector3 = center - axis * 100000.0
	var axis_to: Vector3 = center + axis * 100000.0

	var closest: PackedVector3Array = Geometry3D.get_closest_points_between_segments(
		ray_from,
		ray_to,
		axis_from,
		axis_to
	)

	if closest.size() < 2:
		return

	var axis_point: Vector3 = closest[1]
	var area: Vector2 = layer.area_size

	if handle_id == HANDLE_POS_X or handle_id == HANDLE_NEG_X:
		area.x = maxf(absf(axis_point.x - center.x) * 2.0, MIN_AREA_SIZE)
	else:
		area.y = maxf(absf(axis_point.z - center.z) * 2.0, MIN_AREA_SIZE)

	layer.area_size = area
	layer.update_gizmos()


func _commit_handle(
	gizmo: EditorNode3DGizmo,
	_handle_id: int,
	_secondary: bool,
	restore: Variant,
	cancel: bool
) -> void:
	var layer: IslandScatterLayer = gizmo.get_node_3d() as IslandScatterLayer
	if layer == null:
		return

	var old_area: Vector2 = restore

	if cancel:
		layer.area_size = old_area
		layer.update_gizmos()
		return

	var new_area: Vector2 = layer.area_size
	if new_area.is_equal_approx(old_area):
		return

	if _undo_redo == null:
		return

	_undo_redo.create_action(
		"Resize Scatter Area",
		UndoRedo.MERGE_ENDS,
		layer
	)
	_undo_redo.add_do_property(layer, "area_size", new_area)
	_undo_redo.add_undo_property(layer, "area_size", old_area)
	_undo_redo.add_do_method(layer, "update_gizmos")
	_undo_redo.add_undo_method(layer, "update_gizmos")
	_undo_redo.commit_action()
