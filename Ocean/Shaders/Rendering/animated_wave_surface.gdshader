shader_type spatial;
render_mode unshaded, world_vertex_coords, cull_disabled;

uniform sampler2DArray animated_wave_field : filter_linear, repeat_disable;
uniform vec2 lod_center_xz;
uniform float lod_world_size;
uniform float selected_lod;
uniform vec2 next_lod_center_xz;
uniform float next_lod_world_size;
uniform float next_lod;
uniform bool has_next_lod = false;
uniform int wave_content_mode = 0;
uniform int display_mode = 0;
uniform float horizontal_display_scale = 1.0;
uniform float vertical_display_scale = 1.0;
uniform bool show_surface_grid = true;
uniform bool show_surface_markers = true;

varying float displacement_height;
varying vec2 base_uv;

vec3 sample_lod(vec2 world_xz, vec2 center_xz, float world_size, float layer)
{
	vec2 uv = (world_xz - center_xz) / world_size + vec2(0.5);
	return textureLod(animated_wave_field, vec3(uv, layer), 0.0).xyz;
}

void vertex()
{
	vec2 base_world_xz = VERTEX.xz;
	vec2 uv = (base_world_xz - lod_center_xz) / lod_world_size + vec2(0.5);
	vec3 canonical_displacement = sample_lod(base_world_xz, lod_center_xz, lod_world_size, selected_lod);
	// Diagnostic residual AWF[L] - AWF[L+1], not an exact spectral band or a production field.
	// Different texel widths/filtering can leave small resampling residuals.
	if (wave_content_mode == 1 && has_next_lod) {
		canonical_displacement -= sample_lod(base_world_xz, next_lod_center_xz, next_lod_world_size, next_lod);
	}
	float horizontal = display_mode == 1 ? 0.0 : horizontal_display_scale;
	float vertical = display_mode == 2 ? 0.0 : vertical_display_scale;
	VERTEX += canonical_displacement * vec3(horizontal, vertical, horizontal);
	displacement_height = canonical_displacement.y;
	base_uv = uv;
}

void fragment()
{
	float signed_height = displacement_height / (1.0 + abs(displacement_height));
	ALBEDO = mix(vec3(0.03, 0.2, 0.4), vec3(0.2, 0.85, 0.9), clamp(0.5 + 0.5 * signed_height, 0.0, 1.0));
	if (show_surface_grid) {
		vec2 cell = abs(fract(base_uv * 8.0 + 0.5) - 0.5);
		float line = 1.0 - step(0.012, min(cell.x, cell.y));
		ALBEDO = mix(ALBEDO, vec3(0.04, 0.07, 0.1), line * 0.8);
	}
	if (show_surface_markers) {
		vec2 edge = min(base_uv, vec2(1.0) - base_uv);
		float boundary = 1.0 - step(0.006, min(edge.x, edge.y));
		ALBEDO = mix(ALBEDO, vec3(1.0, 0.7, 0.1), boundary);
		for (int x = 1; x <= 3; x++) {
			for (int y = 1; y <= 3; y++) {
				vec2 d = base_uv - vec2(float(x), float(y)) * 0.25;
				float marker = 1.0 - step(0.00015, dot(d, d));
				ALBEDO = mix(ALBEDO, vec3(1.0, 0.1, 0.15), marker);
			}
		}
	}
}
