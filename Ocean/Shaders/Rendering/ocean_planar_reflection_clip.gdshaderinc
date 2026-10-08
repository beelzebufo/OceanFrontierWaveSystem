// Planar material contract (world-horizontal mean plane, never instantaneous AWF):
// - Always above the plane: an ordinary reflection material is sufficient.
// - Always below the plane: prefer exclusion from planar candidate layers.
// - Can cross the plane: every planar-rendered material path must call this
//   helper (or equivalent world-space clipping) and discard matching fragments.
// C# supplies clip_y from the SAME authority as camera mirroring before use;
// scene/shader zeros are serialization defaults, not another plane authority.
// clip_bias is a separate rendering adjustment, default 0, not physical sea level.
// Object pitch/roll cannot rotate this world-space Y test. Main view is untouched.

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
