uniform sampler2DArray animated_wave_field : filter_linear, repeat_disable;
uniform vec2 lod_center_xz;
uniform float lod_world_size;
uniform float current_lod_texel_width;
uniform vec2 lod_focus_xz;
uniform float selected_lod;
uniform vec2 next_lod_center_xz;
uniform float next_lod_world_size;
uniform float next_lod_texel_width;
uniform float next_lod;
uniform bool has_next_lod = false;
uniform int normal_method = 0;
uniform int wave_content_mode = 0;
uniform int display_mode = 0;
uniform float horizontal_display_scale = 1.0;
uniform float vertical_display_scale = 1.0;

const int NORMAL_METHOD_BLENDED_XYZ_FORWARD = 0;
const int NORMAL_METHOD_CREST_PER_LOD_FORWARD = 1;

vec2 world_to_lod_uv(vec2 world_xz, vec2 center_xz, float world_size)
{
	return (world_xz - center_xz) / world_size + vec2(0.5);
}

// Raw canonical-field sampling: one independent WorldToUV per slice.
vec3 sample_awf_lod(vec2 world_xz, vec2 center_xz, float world_size, float layer)
{
	vec2 uv = world_to_lod_uv(world_xz, center_xz, world_size);
	return textureLod(animated_wave_field, vec3(uv, layer), 0.0).xyz;
}

float compute_surface_lod_alpha(vec2 world_xz)
{
	float scale = lod_world_size * 0.25;
	vec2 offset = abs(world_xz - lod_focus_xz);
	float raw_alpha = max(offset.x, offset.y) / scale - 1.0;
	// Validation grid has no Crest OceanBuilder mesh-density remap.
	return clamp((raw_alpha - 0.15) / (0.85 - 0.15), 0.0, 1.0);
}

void sample_surface_components(vec2 world_xz, out vec3 current_disp, out vec3 next_disp, out float lod_alpha)
{
	current_disp = sample_awf_lod(world_xz, lod_center_xz, lod_world_size, selected_lod);
	if (has_next_lod) {
		next_disp = sample_awf_lod(world_xz, next_lod_center_xz, next_lod_world_size, next_lod);
		lod_alpha = compute_surface_lod_alpha(world_xz);
	} else {
		// The texture array has no extra physical layer after the last LOD.
		next_disp = current_disp;
		lod_alpha = 0.0;
	}
}

vec3 compose_wave_content(vec3 current_disp, vec3 next_disp, float lod_alpha)
{
	// Diagnostic residual AWF[L] - AWF[L+1], not an exact spectral band or a production field.
	// Different texel widths/filtering can leave small resampling residuals.
	if (wave_content_mode == 1) {
		return has_next_lod ? current_disp - next_disp : current_disp;
	}
	if (wave_content_mode == 0 && has_next_lod) {
		return mix(current_disp, next_disp, lod_alpha);
	}
	return current_disp;
}

vec3 apply_display_displacement(vec3 displacement)
{
	float horizontal = display_mode == 1 ? 0.0 : horizontal_display_scale;
	float vertical = display_mode == 2 ? 0.0 : vertical_display_scale;
	return displacement * vec3(horizontal, vertical, horizontal);
}

// The out values cache the center fetches for the future normal strategy.
vec3 sample_render_displacement(vec2 world_xz, out vec3 current_disp, out vec3 next_disp, out float lod_alpha, out vec3 composed_content)
{
	sample_surface_components(world_xz, current_disp, next_disp, lod_alpha);
	composed_content = compose_wave_content(current_disp, next_disp, lod_alpha);
	return apply_display_displacement(composed_content);
}

vec3 evaluate_render_surface_position(vec2 world_xz)
{
	vec3 current_disp;
	vec3 next_disp;
	float lod_alpha;
	vec3 composed_content;
	vec3 displacement = sample_render_displacement(world_xz, current_disp, next_disp, lod_alpha, composed_content);
	return vec3(world_xz.x, 0.0, world_xz.y) + displacement;
}

vec3 safe_surface_normal(vec3 cross_product)
{
	float len2 = dot(cross_product, cross_product);
	if (len2 < 1e-12) {
		return vec3(0.0, 1.0, 0.0);
	}
	return cross_product * inversesqrt(len2);
}

vec3 normal_blended_xyz_forward(vec2 world_xz, float finite_difference_step, vec3 center_current_disp, vec3 center_next_disp, float center_lod_alpha)
{
	vec3 center_content = compose_wave_content(center_current_disp, center_next_disp, center_lod_alpha);
	vec3 center_render_disp = apply_display_displacement(center_content);
	vec3 p0 = vec3(world_xz.x, 0.0, world_xz.y) + center_render_disp;
	vec3 px = evaluate_render_surface_position(world_xz + vec2(finite_difference_step, 0.0));
	vec3 pz = evaluate_render_surface_position(world_xz + vec2(0.0, finite_difference_step));
	return safe_surface_normal(cross(pz - p0, px - p0));
}

// Crest Water 4 reference:
// OceanHelpersNew.hlsl / SampleDisplacementsNormals().
//
// Computes one LOD's full-XYZ forward-difference normal.
// The center displacement is supplied by the caller so the center AWF
// texture fetch performed for vertex displacement can be reused.
//
// Diagnostic display deformation is applied deliberately here so the
// debug normal remains consistent with the geometry currently displayed.
vec3 normal_crest_lod_forward(
	vec2 world_xz,
	vec2 center_xz,
	float world_size,
	float layer,
	float texel_width,
	vec3 center_disp)
{
	vec3 disp =
		apply_display_displacement(center_disp);

	vec3 disp_x =
		vec3(texel_width, 0.0, 0.0) +
		apply_display_displacement(
			sample_awf_lod(
				world_xz + vec2(texel_width, 0.0),
				center_xz,
				world_size,
				layer));

	vec3 disp_z =
		vec3(0.0, 0.0, texel_width) +
		apply_display_displacement(
			sample_awf_lod(
				world_xz + vec2(0.0, texel_width),
				center_xz,
				world_size,
				layer));

	vec3 cross_product =
		cross(
			disp_z - disp,
			disp_x - disp);

	// Match Crest SampleDisplacementsNormals() semantics.
	//
	// Crest deliberately forces the wave normal upward here to avoid
	// NaNs/downward normals caused by degeneracy or surface inversion.
	cross_product.y =
		max(cross_product.y, 0.0001);

	return normalize(cross_product);
}


// Crest-style adjacent-LOD wave normal.
//
// Important:
// Crest does NOT simply normalize(w0 * N0 + w1 * N1).
//
// Ocean.shader initializes:
//
//     n_pixel = vec3(0, 1, 0)
//
// and SampleDisplacementsNormals() accumulates:
//
//     n_pixel.xz += weight * lodNormal.xz
//
// before normalizing the final vector.
//
// Our physical AWF slices do not have Crest's extra terminal metadata
// element. Therefore the final LOD explicitly uses the current slice only.
vec3 normal_crest_per_lod_forward(
	vec2 world_xz,
	vec3 center_current_disp,
	vec3 center_next_disp,
	float center_lod_alpha)
{
	// Own Band is a project diagnostic surface (AWF[L] - AWF[L+1]).
	// Crest has no equivalent normal-composition semantics for it.
	// Derive its normal from the actual displayed surface instead.
	if (wave_content_mode != 0) {
		return normal_blended_xyz_forward(
			world_xz,
			current_lod_texel_width,
			center_current_disp,
			center_next_disp,
			center_lod_alpha);
	}

	// Crest cascade weights are 1 for ordinary physical slices.
	// Therefore:
	//
	//     wt_smaller = 1 - alpha
	//     wt_bigger  = alpha
	//
	// Crest's duplicated terminal metadata element with weight 0 is NOT
	// reproduced here because our texture array has no physical LOD8.
	float wt_smaller_lod =
		1.0 - center_lod_alpha;

	float wt_bigger_lod =
		has_next_lod
			? 1.0 - wt_smaller_lod
			: 0.0;

	vec2 accumulated_normal_xz =
		vec2(0.0);

	// Match Crest's small-weight skip.
	if (wt_smaller_lod > 0.001) {
		vec3 normal_current =
			normal_crest_lod_forward(
				world_xz,
				lod_center_xz,
				lod_world_size,
				selected_lod,
				current_lod_texel_width,
				center_current_disp);

		accumulated_normal_xz +=
			wt_smaller_lod *
			normal_current.xz;
	}

	if (has_next_lod &&
		wt_bigger_lod > 0.001)
	{
		vec3 normal_next =
			normal_crest_lod_forward(
				world_xz,
				next_lod_center_xz,
				next_lod_world_size,
				next_lod,
				next_lod_texel_width,
				center_next_disp);

		accumulated_normal_xz +=
			wt_bigger_lod *
			normal_next.xz;
	}

	// Match Crest Ocean.shader final wave-normal construction:
	//
	//     n_pixel starts at (0,1,0)
	//     only XZ from each LOD normal is accumulated
	//     final vector is normalized afterwards.
	return normalize(
		vec3(
			accumulated_normal_xz.x,
			1.0,
			accumulated_normal_xz.y));
}

vec3 compute_surface_normal(
	vec2 world_xz,
	float finite_difference_step,
	vec3 center_current_disp,
	vec3 center_next_disp,
	float center_lod_alpha)
{
	if (normal_method ==
		NORMAL_METHOD_CREST_PER_LOD_FORWARD)
	{
		return normal_crest_per_lod_forward(
			world_xz,
			center_current_disp,
			center_next_disp,
			center_lod_alpha);
	}

	return normal_blended_xyz_forward(
		world_xz,
		finite_difference_step,
		center_current_disp,
		center_next_disp,
		center_lod_alpha);
}
