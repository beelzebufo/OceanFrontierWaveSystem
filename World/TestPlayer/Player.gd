extends CharacterBody3D

@export var MOVE_SPEED: float = 8.0
@export var SPRINT_MULTIPLIER: float = 2.0
@export var GRAVITY: float = 24.0
@export var first_person: bool = false : 
	set(p_value):
		first_person = p_value
		if is_node_ready():
			_apply_first_person()

@export var gravity_enabled: bool = true :
	set(p_value):
		gravity_enabled = p_value
		if not gravity_enabled:
			velocity.y = 0
			
@export var collision_enabled: bool = true :
	set(p_value):
		collision_enabled = p_value
		if is_node_ready():
			_apply_collision_enabled()

@onready var _arm: SpringArm3D = %Arm
@onready var _camera: Camera3D = %Camera3D
@onready var _body: MeshInstance3D = $Body
@onready var _collision_shape_body: CollisionShape3D = $CollisionShapeBody
@onready var _collision_shape_ray: CollisionShape3D = $CollisionShapeRay


func _ready() -> void:
	_apply_first_person()
	_apply_collision_enabled()
	_camera.current = true


func _apply_first_person() -> void:
	_arm.spring_length = 0.0 if first_person else 6.0
	_body.visible = not first_person


func _apply_collision_enabled() -> void:
	_collision_shape_body.disabled = not collision_enabled
	_collision_shape_ray.disabled = not collision_enabled


func _physics_process(p_delta) -> void:
	var direction: Vector3 = get_camera_relative_input()
	var h_veloc: Vector2 = Vector2(direction.x, direction.z).normalized() * MOVE_SPEED
	if Input.is_key_pressed(KEY_SHIFT):
		h_veloc *= SPRINT_MULTIPLIER
	velocity.x = h_veloc.x
	velocity.z = h_veloc.y
	if gravity_enabled:
		velocity.y -= GRAVITY * p_delta
	move_and_slide()


# Returns the input vector relative to the camera. Forward is always the direction the camera is facing
func get_camera_relative_input() -> Vector3:
	var input_dir: Vector3 = Vector3.ZERO
	if Input.is_key_pressed(KEY_A): # Left
		input_dir -= %Camera3D.global_transform.basis.x
	if Input.is_key_pressed(KEY_D): # Right
		input_dir += %Camera3D.global_transform.basis.x
	if Input.is_key_pressed(KEY_W): # Forward
		input_dir -= %Camera3D.global_transform.basis.z
	if Input.is_key_pressed(KEY_S): # Backward
		input_dir += %Camera3D.global_transform.basis.z
	return input_dir
