shader_type spatial;
render_mode unshaded, world_vertex_coords, cull_disabled;

uniform sampler2DArray animated_wave_field : filter_linear, repeat_disable;
uniform vec2 lod_center_xz;
uniform float lod_world_size;
uniform float selected_lod;

varying float displacement_height;

void vertex()
{
	vec2 base_world_xz = VERTEX.xz;
	vec2 uv = (base_world_xz - lod_center_xz) / lod_world_size + vec2(0.5);
	vec3 displacement = textureLod(animated_wave_field, vec3(uv, selected_lod), 0.0).xyz;
	VERTEX += displacement;
	displacement_height = displacement.y;
}

void fragment()
{
	float signed_height = displacement_height / (1.0 + abs(displacement_height));
	ALBEDO = mix(vec3(0.03, 0.2, 0.4), vec3(0.2, 0.85, 0.9), clamp(0.5 + 0.5 * signed_height, 0.0, 1.0));
}
