@tool
extends VBoxContainer
class_name LowPolyTerrainEditorDock


const ISLAND_SCATTER_PROFILE_SCRIPT := preload(
	"res://addons/lowpolyterrain/IslandScatterProfile.gd"
)


signal scatter_profile_changed(profile)
signal scatter_profile_settings_changed(profile)
signal rebuild_scatter_zones_requested(profile)


var _terrain: LowPolyTerrainManager = null
var _undo_redo: EditorUndoRedoManager = null
var _selected_layer: int = 1
var _updating_ui: bool = false
var _refresh_time: float = 0.0

var _page_group: ButtonGroup = null
var _surface_button: Button = null
var _scatter_button: Button = null
var _surface_panel: VBoxContainer = null
var _scatter_panel: VBoxContainer = null

var _layer_buttons: Array[Button] = []
var _routing_label: Label = null
var _color_picker: ColorPickerButton = null
var _roughness_spin: SpinBox = null
var _slope_min_spin: SpinBox = null
var _slope_max_spin: SpinBox = null
var _slope_feather_spin: SpinBox = null

var _active_scatter_profile: Resource = null
var _profile_picker: EditorResourcePicker = null
var _profile_name_edit: LineEdit = null
var _mesh_picker: EditorResourcePicker = null
var _material_picker: EditorResourcePicker = null
var _volume_x_spin: SpinBox = null
var _volume_y_spin: SpinBox = null
var _volume_z_spin: SpinBox = null
var _volume_y_offset_spin: SpinBox = null
var _density_spin: SpinBox = null
var _max_instances_spin: SpinBox = null
var _seed_spin: SpinBox = null
var _height_min_spin: SpinBox = null
var _height_max_spin: SpinBox = null
var _slope_hard_min_spin: SpinBox = null
var _slope_full_min_spin: SpinBox = null
var _slope_full_max_spin: SpinBox = null
var _slope_hard_max_spin: SpinBox = null
var _surface_checks: Array[CheckBox] = []
var _surface_density_spins: Array[SpinBox] = []
var _minimum_surface_weight_spin: SpinBox = null
var _min_scale_spin: SpinBox = null
var _max_scale_spin: SpinBox = null
var _random_y_check: CheckBox = null
var _scatter_status_label: Label = null


func _ready() -> void:
	custom_minimum_size = Vector2(320.0, 0.0)
	name = "Island Terrain"
	size_flags_horizontal = Control.SIZE_EXPAND_FILL
	size_flags_vertical = Control.SIZE_EXPAND_FILL

	_build_ui()
	_set_surface_controls_enabled(false)
	_routing_label.text = "Select LowPolyTerrainManager"
	_ensure_default_scatter_profile()
	set_process(true)


func set_undo_redo_manager(value: EditorUndoRedoManager) -> void:
	_undo_redo = value


func set_terrain(value: LowPolyTerrainManager) -> void:
	_terrain = value

	if _terrain == null:
		_set_surface_controls_enabled(false)
		_routing_label.text = "Select LowPolyTerrainManager"
		return

	_terrain.ensure_paint_material()
	_selected_layer = clampi(_terrain.paint_layer, 1, LowPolyTerrainManager.PAINT_LAYER_COUNT)
	_set_surface_controls_enabled(true)
	_refresh_layer_buttons()
	_refresh_surface_from_source()


func show_scatter_page() -> void:
	_show_page("scatter")


func get_active_scatter_profile() -> Resource:
	_ensure_default_scatter_profile()
	return _active_scatter_profile


func _process(delta: float) -> void:
	if _terrain == null:
		return

	if not is_instance_valid(_terrain):
		set_terrain(null)
		return

	_refresh_time += delta

	if _refresh_time < 0.20:
		return

	_refresh_time = 0.0
	_refresh_surface_from_source()


# =============================================================================
# ROOT UI
# =============================================================================

func _build_ui() -> void:
	var title := Label.new()
	title.text = "ISLAND TERRAIN"
	title.add_theme_font_size_override("font_size", 18)
	add_child(title)

	var tabs := HBoxContainer.new()
	tabs.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	add_child(tabs)

	_page_group = ButtonGroup.new()

	_surface_button = _make_page_button("Surface", true)
	_surface_button.pressed.connect(_show_page.bind("surface"))
	tabs.add_child(_surface_button)

	_scatter_button = _make_page_button("Scatter", false)
	_scatter_button.pressed.connect(_show_page.bind("scatter"))
	tabs.add_child(_scatter_button)

	var cliff_button := _make_page_button("Cliffs", false)
	cliff_button.disabled = true
	cliff_button.tooltip_text = "Editor Stage E3"
	tabs.add_child(cliff_button)

	var seabed_button := _make_page_button("Seabed", false)
	seabed_button.disabled = true
	seabed_button.tooltip_text = "Editor Stage E4"
	tabs.add_child(seabed_button)

	add_child(HSeparator.new())

	var scroll := ScrollContainer.new()
	scroll.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	add_child(scroll)

	var pages := VBoxContainer.new()
	pages.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	pages.size_flags_vertical = Control.SIZE_EXPAND_FILL
	scroll.add_child(pages)

	_surface_panel = VBoxContainer.new()
	_surface_panel.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	pages.add_child(_surface_panel)
	_build_surface_panel(_surface_panel)

	_scatter_panel = VBoxContainer.new()
	_scatter_panel.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	pages.add_child(_scatter_panel)
	_build_scatter_panel(_scatter_panel)

	_show_page("surface")


func _make_page_button(text_value: String, pressed: bool) -> Button:
	var button := Button.new()
	button.text = text_value
	button.toggle_mode = true
	button.button_group = _page_group
	button.button_pressed = pressed
	button.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	return button


func _show_page(page_name: String) -> void:
	var surface_visible: bool = page_name == "surface"
	_surface_panel.visible = surface_visible
	_scatter_panel.visible = not surface_visible
	_surface_button.set_pressed_no_signal(surface_visible)
	_scatter_button.set_pressed_no_signal(not surface_visible)


# =============================================================================
# SURFACE PAGE
# =============================================================================

func _build_surface_panel(parent: VBoxContainer) -> void:
	var header := Label.new()
	header.text = "Surface Paint"
	header.add_theme_font_size_override("font_size", 16)
	parent.add_child(header)

	_routing_label = Label.new()
	parent.add_child(_routing_label)

	var layer_label := Label.new()
	layer_label.text = "Paint Layer"
	parent.add_child(layer_label)

	var layer_row := HBoxContainer.new()
	layer_row.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	parent.add_child(layer_row)

	for layer in range(1, LowPolyTerrainManager.PAINT_LAYER_COUNT + 1):
		var button := Button.new()
		button.text = str(layer)
		button.toggle_mode = true
		button.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		button.tooltip_text = "Paint Layer %d" % layer
		button.pressed.connect(_on_layer_pressed.bind(layer))
		layer_row.add_child(button)
		_layer_buttons.append(button)

	parent.add_child(HSeparator.new())

	_color_picker = ColorPickerButton.new()
	_color_picker.text = "Layer Color"
	_color_picker.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_color_picker.color_changed.connect(_on_color_changed)
	parent.add_child(_color_picker)

	_roughness_spin = _create_spin_row(parent, "Roughness", 0.0, 1.0, 0.01)
	_roughness_spin.value_changed.connect(_on_roughness_changed)

	parent.add_child(HSeparator.new())

	var slope_header := Label.new()
	slope_header.text = "Placement / Paint Slope"
	parent.add_child(slope_header)

	_slope_min_spin = _create_spin_row(parent, "Minimum", 0.0, 90.0, 0.5)
	_slope_min_spin.suffix = "°"
	_slope_min_spin.value_changed.connect(_on_slope_min_changed)

	_slope_max_spin = _create_spin_row(parent, "Maximum", 0.0, 90.0, 0.5)
	_slope_max_spin.suffix = "°"
	_slope_max_spin.value_changed.connect(_on_slope_max_changed)

	_slope_feather_spin = _create_spin_row(parent, "Feather", 0.0, 45.0, 0.5)
	_slope_feather_spin.suffix = "°"
	_slope_feather_spin.value_changed.connect(_on_slope_feather_changed)

	var info := Label.new()
	info.text = "Paint 1–4 remain the authoritative terrain surfaces used by scatter filters."
	info.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	parent.add_child(info)


# =============================================================================
# SCATTER PAGE
# =============================================================================

func _build_scatter_panel(parent: VBoxContainer) -> void:
	var header := Label.new()
	header.text = "Scatter Profile"
	header.add_theme_font_size_override("font_size", 16)
	parent.add_child(header)

	_profile_picker = EditorResourcePicker.new()
	_profile_picker.base_type = "Resource"
	_profile_picker.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_profile_picker.resource_changed.connect(_on_profile_resource_changed)
	parent.add_child(_profile_picker)

	var profile_buttons := HBoxContainer.new()
	profile_buttons.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	parent.add_child(profile_buttons)

	var new_profile_button := Button.new()
	new_profile_button.text = "New Profile"
	new_profile_button.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	new_profile_button.pressed.connect(_on_new_profile_pressed)
	profile_buttons.add_child(new_profile_button)

	var rebuild_button := Button.new()
	rebuild_button.text = "Rebuild Placed Zones"
	rebuild_button.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	rebuild_button.pressed.connect(_on_rebuild_zones_pressed)
	profile_buttons.add_child(rebuild_button)

	_profile_name_edit = _create_line_row(parent, "Name")
	_profile_name_edit.text_changed.connect(_on_profile_name_changed)

	parent.add_child(HSeparator.new())
	_add_section_label(parent, "Source")

	_mesh_picker = _create_resource_row(parent, "Mesh", "Mesh")
	_mesh_picker.resource_changed.connect(_on_mesh_changed)

	_material_picker = _create_resource_row(parent, "Material", "Material")
	_material_picker.resource_changed.connect(_on_material_changed)

	parent.add_child(HSeparator.new())
	_add_section_label(parent, "Placement Volume")

	_volume_x_spin = _create_spin_row(parent, "Size X", 0.1, 2000.0, 0.1)
	_volume_x_spin.suffix = " m"
	_volume_x_spin.value_changed.connect(_on_volume_x_changed)

	_volume_y_spin = _create_spin_row(parent, "Size Y", 0.1, 2000.0, 0.1)
	_volume_y_spin.suffix = " m"
	_volume_y_spin.value_changed.connect(_on_volume_y_changed)

	_volume_z_spin = _create_spin_row(parent, "Size Z", 0.1, 2000.0, 0.1)
	_volume_z_spin.suffix = " m"
	_volume_z_spin.value_changed.connect(_on_volume_z_changed)

	_volume_y_offset_spin = _create_spin_row(parent, "Center Y Offset", -1000.0, 1000.0, 0.1)
	_volume_y_offset_spin.suffix = " m"
	_volume_y_offset_spin.value_changed.connect(_on_volume_y_offset_changed)

	parent.add_child(HSeparator.new())
	_add_section_label(parent, "Distribution")

	_density_spin = _create_spin_row(parent, "Candidates / m²", 0.0, 50.0, 0.01)
	_density_spin.value_changed.connect(_on_density_changed)

	_max_instances_spin = _create_spin_row(parent, "Max Instances", 1.0, 500000.0, 1.0)
	_max_instances_spin.value_changed.connect(_on_max_instances_changed)

	_seed_spin = _create_spin_row(parent, "Seed", -2147483648.0, 2147483647.0, 1.0)
	_seed_spin.value_changed.connect(_on_seed_changed)

	parent.add_child(HSeparator.new())
	_add_section_label(parent, "Height Filter")

	_height_min_spin = _create_spin_row(parent, "Minimum Y", -10000.0, 10000.0, 0.1)
	_height_min_spin.value_changed.connect(_on_height_min_changed)

	_height_max_spin = _create_spin_row(parent, "Maximum Y", -10000.0, 10000.0, 0.1)
	_height_max_spin.value_changed.connect(_on_height_max_changed)

	parent.add_child(HSeparator.new())
	_add_section_label(parent, "Slope Density")

	_slope_hard_min_spin = _create_spin_row(parent, "Hard Min", 0.0, 89.0, 0.5)
	_slope_hard_min_spin.suffix = "°"
	_slope_hard_min_spin.value_changed.connect(_on_slope_hard_min_changed)

	_slope_full_min_spin = _create_spin_row(parent, "Full Min", 0.0, 89.0, 0.5)
	_slope_full_min_spin.suffix = "°"
	_slope_full_min_spin.value_changed.connect(_on_slope_full_min_changed)

	_slope_full_max_spin = _create_spin_row(parent, "Full Max", 0.0, 89.0, 0.5)
	_slope_full_max_spin.suffix = "°"
	_slope_full_max_spin.value_changed.connect(_on_slope_full_max_changed)

	_slope_hard_max_spin = _create_spin_row(parent, "Hard Max", 0.0, 89.0, 0.5)
	_slope_hard_max_spin.suffix = "°"
	_slope_hard_max_spin.value_changed.connect(_on_slope_hard_max_changed)

	parent.add_child(HSeparator.new())
	_add_section_label(parent, "Allowed Terrain Surfaces")

	var surface_names: PackedStringArray = ["Base", "Paint 1", "Paint 2", "Paint 3", "Paint 4"]

	for i in range(surface_names.size()):
		var check := CheckBox.new()
		check.text = surface_names[i]
		check.toggled.connect(_on_surface_allowed_toggled.bind(i))
		parent.add_child(check)
		_surface_checks.append(check)

	_minimum_surface_weight_spin = _create_spin_row(parent, "Min Allowed Weight", 0.0, 1.0, 0.05)
	_minimum_surface_weight_spin.value_changed.connect(_on_minimum_surface_weight_changed)

	var density_names: PackedStringArray = ["Base Density", "Paint 1 Density", "Paint 2 Density", "Paint 3 Density", "Paint 4 Density"]

	for i in range(density_names.size()):
		var spin := _create_spin_row(parent, density_names[i], 0.0, 1.0, 0.05)
		spin.value_changed.connect(_on_surface_density_changed.bind(i))
		_surface_density_spins.append(spin)

	parent.add_child(HSeparator.new())
	_add_section_label(parent, "Instance Variation")

	_min_scale_spin = _create_spin_row(parent, "Min Scale", 0.1, 10.0, 0.01)
	_min_scale_spin.value_changed.connect(_on_min_scale_changed)

	_max_scale_spin = _create_spin_row(parent, "Max Scale", 0.1, 10.0, 0.01)
	_max_scale_spin.value_changed.connect(_on_max_scale_changed)

	_random_y_check = CheckBox.new()
	_random_y_check.text = "Random Y Rotation"
	_random_y_check.toggled.connect(_on_random_y_toggled)
	parent.add_child(_random_y_check)

	parent.add_child(HSeparator.new())

	_scatter_status_label = Label.new()
	_scatter_status_label.text = "Configure the profile, then choose Scatter in the terrain toolbar and click the terrain to place a volume."
	_scatter_status_label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	parent.add_child(_scatter_status_label)


# =============================================================================
# COMMON UI HELPERS
# =============================================================================

func _add_section_label(parent: VBoxContainer, text_value: String) -> void:
	var label := Label.new()
	label.text = text_value
	label.add_theme_font_size_override("font_size", 15)
	parent.add_child(label)


func _create_spin_row(parent: VBoxContainer, label_text: String, min_value: float, max_value: float, step: float) -> SpinBox:
	var row := HBoxContainer.new()
	row.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	parent.add_child(row)

	var label := Label.new()
	label.text = label_text
	label.custom_minimum_size = Vector2(125.0, 0.0)
	row.add_child(label)

	var spin := SpinBox.new()
	spin.min_value = min_value
	spin.max_value = max_value
	spin.step = step
	spin.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	row.add_child(spin)
	return spin


func _create_line_row(parent: VBoxContainer, label_text: String) -> LineEdit:
	var row := HBoxContainer.new()
	row.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	parent.add_child(row)

	var label := Label.new()
	label.text = label_text
	label.custom_minimum_size = Vector2(125.0, 0.0)
	row.add_child(label)

	var edit := LineEdit.new()
	edit.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	row.add_child(edit)
	return edit


func _create_resource_row(parent: VBoxContainer, label_text: String, base_type: String) -> EditorResourcePicker:
	var row := HBoxContainer.new()
	row.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	parent.add_child(row)

	var label := Label.new()
	label.text = label_text
	label.custom_minimum_size = Vector2(125.0, 0.0)
	row.add_child(label)

	var picker := EditorResourcePicker.new()
	picker.base_type = base_type
	picker.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	row.add_child(picker)
	return picker


# =============================================================================
# SURFACE LOGIC
# =============================================================================

func _on_layer_pressed(layer: int) -> void:
	if _updating_ui or _terrain == null:
		return

	_selected_layer = clampi(layer, 1, LowPolyTerrainManager.PAINT_LAYER_COUNT)

	if _terrain.paint_layer != _selected_layer:
		_terrain.paint_layer = _selected_layer
		_terrain.signal_brush_settings_changed.emit()

	_refresh_layer_buttons()
	_refresh_surface_from_source()


func _refresh_layer_buttons() -> void:
	_updating_ui = true

	for index in range(_layer_buttons.size()):
		var button: Button = _layer_buttons[index]
		button.button_pressed = index + 1 == _selected_layer

	_updating_ui = false


func _refresh_surface_from_source() -> void:
	if _terrain == null:
		return

	var material: ShaderMaterial = _get_paint_material()

	if material == null:
		_routing_label.text = "Paint material unavailable."
		return

	_updating_ui = true
	_routing_label.text = "Routing: INLINE / Custom Material" if _terrain.paint_routing == LowPolyTerrainManager.PaintRouting.INLINE else "Routing: OVERLAY / Paint Material"

	var color: Color = _terrain.get_paint_layer_color(_selected_layer)
	if _color_picker.color != color:
		_color_picker.color = color

	var roughness: float = _terrain.get_paint_layer_roughness(_selected_layer)
	if not is_equal_approx(float(_roughness_spin.value), roughness):
		_roughness_spin.value = roughness

	var slope: Vector2 = _get_selected_slope()
	if not is_equal_approx(float(_slope_min_spin.value), slope.x):
		_slope_min_spin.value = slope.x
	if not is_equal_approx(float(_slope_max_spin.value), slope.y):
		_slope_max_spin.value = slope.y
	if not is_equal_approx(float(_slope_feather_spin.value), _terrain.paint_slope_feather):
		_slope_feather_spin.value = _terrain.paint_slope_feather

	_updating_ui = false


func _get_selected_slope() -> Vector2:
	if _terrain == null:
		return Vector2(0.0, 90.0)

	var property_name: String = "paint_layer_%d_slope" % _selected_layer
	var value: Variant = _terrain.get(property_name)
	return value if value is Vector2 else Vector2(0.0, 90.0)


func _get_paint_material() -> ShaderMaterial:
	if _terrain == null:
		return null

	_terrain.ensure_paint_material()
	return _terrain.get_paint_settings_material()


func _on_color_changed(value: Color) -> void:
	if _updating_ui:
		return
	_set_shader_parameter("paint_layer_%d_color" % _selected_layer, value, "Change Terrain Paint Color")


func _on_roughness_changed(value: float) -> void:
	if _updating_ui:
		return
	_set_shader_parameter("paint_layer_%d_roughness" % _selected_layer, value, "Change Terrain Paint Roughness")


func _set_shader_parameter(parameter_name: String, value: Variant, action_name: String) -> void:
	var material: ShaderMaterial = _get_paint_material()
	if material == null:
		return

	var old_value: Variant = material.get_shader_parameter(parameter_name)
	if old_value == value:
		return

	if _undo_redo != null:
		_undo_redo.create_action(action_name, UndoRedo.MERGE_ENDS, _terrain)
		_undo_redo.add_do_method(material, "set_shader_parameter", parameter_name, value)
		_undo_redo.add_undo_method(material, "set_shader_parameter", parameter_name, old_value)
		_undo_redo.add_do_method(material, "emit_changed")
		_undo_redo.add_undo_method(material, "emit_changed")
		_undo_redo.commit_action()
	else:
		material.set_shader_parameter(parameter_name, value)
		material.emit_changed()


func _on_slope_min_changed(value: float) -> void:
	if _updating_ui:
		return
	var slope: Vector2 = _get_selected_slope()
	_set_selected_slope(Vector2(minf(value, slope.y), slope.y))


func _on_slope_max_changed(value: float) -> void:
	if _updating_ui:
		return
	var slope: Vector2 = _get_selected_slope()
	_set_selected_slope(Vector2(slope.x, maxf(value, slope.x)))


func _set_selected_slope(value: Vector2) -> void:
	_set_terrain_property("paint_layer_%d_slope" % _selected_layer, value, "Change Terrain Paint Slope")


func _on_slope_feather_changed(value: float) -> void:
	if _updating_ui:
		return
	_set_terrain_property("paint_slope_feather", value, "Change Terrain Paint Slope Feather")


func _set_terrain_property(property_name: String, value: Variant, action_name: String) -> void:
	if _terrain == null:
		return

	var old_value: Variant = _terrain.get(property_name)
	if old_value == value:
		return

	if _undo_redo != null:
		_undo_redo.create_action(action_name, UndoRedo.MERGE_ENDS, _terrain)
		_undo_redo.add_do_property(_terrain, property_name, value)
		_undo_redo.add_undo_property(_terrain, property_name, old_value)
		_undo_redo.commit_action()
	else:
		_terrain.set(property_name, value)


func _set_surface_controls_enabled(enabled: bool) -> void:
	for button in _layer_buttons:
		button.disabled = not enabled

	if _color_picker != null:
		_color_picker.disabled = not enabled
	if _roughness_spin != null:
		_roughness_spin.editable = enabled
	if _slope_min_spin != null:
		_slope_min_spin.editable = enabled
	if _slope_max_spin != null:
		_slope_max_spin.editable = enabled
	if _slope_feather_spin != null:
		_slope_feather_spin.editable = enabled


# =============================================================================
# SCATTER PROFILE LOGIC
# =============================================================================

func _ensure_default_scatter_profile() -> void:
	if _active_scatter_profile != null:
		return

	var profile := ISLAND_SCATTER_PROFILE_SCRIPT.new() as Resource
	if profile == null:
		push_error("Low Poly Terrain: could not instantiate scatter profile.")
		return

	profile.display_name = "Scatter"
	_set_active_scatter_profile(profile)


func _set_active_scatter_profile(profile: Resource) -> void:
	if profile == null:
		return

	_active_scatter_profile = profile
	_refresh_scatter_profile_ui()
	scatter_profile_changed.emit(_active_scatter_profile)


func _on_profile_resource_changed(resource: Resource) -> void:
	if _updating_ui:
		return

	var profile: Resource = resource
	if profile == null or profile.get_script() != ISLAND_SCATTER_PROFILE_SCRIPT:
		_ensure_default_scatter_profile()
		return

	_set_active_scatter_profile(profile)


func _on_new_profile_pressed() -> void:
	var profile := ISLAND_SCATTER_PROFILE_SCRIPT.new() as Resource
	if profile == null:
		push_error("Low Poly Terrain: could not instantiate scatter profile.")
		return

	profile.display_name = "Scatter"
	_set_active_scatter_profile(profile)


func _on_rebuild_zones_pressed() -> void:
	if _active_scatter_profile == null:
		return
	rebuild_scatter_zones_requested.emit(_active_scatter_profile)


func _refresh_scatter_profile_ui() -> void:
	if _active_scatter_profile == null:
		return

	_updating_ui = true
	_profile_picker.edited_resource = _active_scatter_profile
	_profile_name_edit.text = _active_scatter_profile.display_name
	_mesh_picker.edited_resource = _active_scatter_profile.source_mesh
	_material_picker.edited_resource = _active_scatter_profile.material
	_volume_x_spin.value = _active_scatter_profile.volume_size.x
	_volume_y_spin.value = _active_scatter_profile.volume_size.y
	_volume_z_spin.value = _active_scatter_profile.volume_size.z
	_volume_y_offset_spin.value = _active_scatter_profile.volume_center_y_offset
	_density_spin.value = _active_scatter_profile.candidates_per_square_meter
	_max_instances_spin.value = _active_scatter_profile.max_instances
	_seed_spin.value = _active_scatter_profile.seed
	_height_min_spin.value = _active_scatter_profile.min_height
	_height_max_spin.value = _active_scatter_profile.max_height
	_slope_hard_min_spin.value = _active_scatter_profile.min_slope_degrees
	_slope_full_min_spin.value = _active_scatter_profile.full_density_min_slope
	_slope_full_max_spin.value = _active_scatter_profile.full_density_max_slope
	_slope_hard_max_spin.value = _active_scatter_profile.max_slope_degrees
	_minimum_surface_weight_spin.value = _active_scatter_profile.minimum_allowed_surface_weight
	_min_scale_spin.value = _active_scatter_profile.min_scale
	_max_scale_spin.value = _active_scatter_profile.max_scale
	_random_y_check.button_pressed = _active_scatter_profile.random_y_rotation

	for i in range(_surface_checks.size()):
		_surface_checks[i].button_pressed = (_active_scatter_profile.allowed_surfaces & (1 << i)) != 0

	var densities: PackedFloat32Array = [
		_active_scatter_profile.base_surface_density,
		_active_scatter_profile.paint1_surface_density,
		_active_scatter_profile.paint2_surface_density,
		_active_scatter_profile.paint3_surface_density,
		_active_scatter_profile.paint4_surface_density,
	]

	for i in range(_surface_density_spins.size()):
		_surface_density_spins[i].value = densities[i]

	_updating_ui = false


func _set_profile_property(property_name: String, value: Variant, action_name: String) -> void:
	if _updating_ui or _active_scatter_profile == null:
		return

	var old_value: Variant = _active_scatter_profile.get(property_name)
	if old_value == value:
		return

	if _undo_redo != null:
		_undo_redo.create_action(action_name, UndoRedo.MERGE_ENDS, _active_scatter_profile)
		_undo_redo.add_do_property(_active_scatter_profile, property_name, value)
		_undo_redo.add_undo_property(_active_scatter_profile, property_name, old_value)
		_undo_redo.add_do_method(_active_scatter_profile, "emit_changed")
		_undo_redo.add_undo_method(_active_scatter_profile, "emit_changed")
		_undo_redo.commit_action()
	else:
		_active_scatter_profile.set(property_name, value)
		_active_scatter_profile.emit_changed()

	scatter_profile_settings_changed.emit(_active_scatter_profile)


func _on_profile_name_changed(value: String) -> void:
	_set_profile_property("display_name", value, "Rename Scatter Profile")


func _on_mesh_changed(resource: Resource) -> void:
	_set_profile_property("source_mesh", resource as Mesh, "Change Scatter Mesh")


func _on_material_changed(resource: Resource) -> void:
	_set_profile_property("material", resource as Material, "Change Scatter Material")


func _on_volume_x_changed(value: float) -> void:
	var size: Vector3 = _active_scatter_profile.volume_size
	_set_profile_property("volume_size", Vector3(maxf(value, 0.1), size.y, size.z), "Change Scatter Volume")


func _on_volume_y_changed(value: float) -> void:
	var size: Vector3 = _active_scatter_profile.volume_size
	_set_profile_property("volume_size", Vector3(size.x, maxf(value, 0.1), size.z), "Change Scatter Volume")


func _on_volume_z_changed(value: float) -> void:
	var size: Vector3 = _active_scatter_profile.volume_size
	_set_profile_property("volume_size", Vector3(size.x, size.y, maxf(value, 0.1)), "Change Scatter Volume")


func _on_volume_y_offset_changed(value: float) -> void:
	_set_profile_property("volume_center_y_offset", value, "Change Scatter Volume Offset")


func _on_density_changed(value: float) -> void:
	_set_profile_property("candidates_per_square_meter", value, "Change Scatter Density")


func _on_max_instances_changed(value: float) -> void:
	_set_profile_property("max_instances", int(value), "Change Scatter Max Instances")


func _on_seed_changed(value: float) -> void:
	_set_profile_property("seed", int(value), "Change Scatter Seed")


func _on_height_min_changed(value: float) -> void:
	_set_profile_property("min_height", value, "Change Scatter Height Filter")


func _on_height_max_changed(value: float) -> void:
	_set_profile_property("max_height", value, "Change Scatter Height Filter")


func _on_slope_hard_min_changed(value: float) -> void:
	_set_profile_property("min_slope_degrees", value, "Change Scatter Slope Filter")


func _on_slope_full_min_changed(value: float) -> void:
	_set_profile_property("full_density_min_slope", value, "Change Scatter Slope Filter")


func _on_slope_full_max_changed(value: float) -> void:
	_set_profile_property("full_density_max_slope", value, "Change Scatter Slope Filter")


func _on_slope_hard_max_changed(value: float) -> void:
	_set_profile_property("max_slope_degrees", value, "Change Scatter Slope Filter")


func _on_surface_allowed_toggled(enabled: bool, index: int) -> void:
	if _active_scatter_profile == null:
		return

	var flags: int = _active_scatter_profile.allowed_surfaces
	if enabled:
		flags |= 1 << index
	else:
		flags &= ~(1 << index)
	_set_profile_property("allowed_surfaces", flags, "Change Scatter Surface Filter")


func _on_minimum_surface_weight_changed(value: float) -> void:
	_set_profile_property("minimum_allowed_surface_weight", value, "Change Scatter Surface Filter")


func _on_surface_density_changed(value: float, index: int) -> void:
	var properties: PackedStringArray = [
		"base_surface_density",
		"paint1_surface_density",
		"paint2_surface_density",
		"paint3_surface_density",
		"paint4_surface_density",
	]
	_set_profile_property(properties[index], value, "Change Scatter Surface Density")


func _on_min_scale_changed(value: float) -> void:
	_set_profile_property("min_scale", value, "Change Scatter Scale")


func _on_max_scale_changed(value: float) -> void:
	_set_profile_property("max_scale", value, "Change Scatter Scale")


func _on_random_y_toggled(enabled: bool) -> void:
	_set_profile_property("random_y_rotation", enabled, "Change Scatter Rotation")
