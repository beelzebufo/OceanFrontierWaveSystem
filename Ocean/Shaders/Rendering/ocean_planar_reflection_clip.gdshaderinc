// Camera-only Godot layer 16. Keep in sync with OceanPlanarReflectionDiagnostic.
// Layers 17/18 are reserved by the player and must not identify this pass.
const uint OCEAN_PLANAR_REFLECTION_CAMERA_MARKER_MASK = 32768u;

bool ocean_should_clip_planar_reflection(
	bool enabled,
	float clip_y,
	float clip_bias,
	vec3 fragment_view_position,
	mat4 inv_view_matrix,
	uint camera_visible_layers)
{
	if (!enabled ||
		(camera_visible_layers & OCEAN_PLANAR_REFLECTION_CAMERA_MARKER_MASK) == 0u)
	{
		return false;
	}

	vec3 world_position =
		(inv_view_matrix * vec4(fragment_view_position, 1.0)).xyz;

	return world_position.y < clip_y + clip_bias;
}
