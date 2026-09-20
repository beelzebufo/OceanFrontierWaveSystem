shader_type spatial;
render_mode world_vertex_coords, cull_disabled, unshaded;

#include "res://Ocean/Shaders/Rendering/animated_wave_surface_contract.gdshaderinc"

uniform float diagnostic_normal_length = 0.1;

void vertex()
{
	vec2 world_xz = VERTEX.xz;
	vec3 current_disp;
	vec3 next_disp;
	float lod_alpha;
	vec3 composed_content;
	vec3 displayed_displacement = sample_render_displacement(world_xz, current_disp, next_disp, lod_alpha, composed_content);
	vec3 surface_normal = compute_surface_normal(world_xz, current_lod_texel_width, current_disp, next_disp, lod_alpha);
	vec3 surface_position = vec3(world_xz.x, 0.0, world_xz.y) + displayed_displacement;
	// COLOR.r is 0 for the base and 1 for the tip at the same world-space anchor.
	VERTEX = surface_position + surface_normal * diagnostic_normal_length * COLOR.r;
}

void fragment()
{
	ALBEDO = vec3(0.95, 0.15, 0.85);
}
