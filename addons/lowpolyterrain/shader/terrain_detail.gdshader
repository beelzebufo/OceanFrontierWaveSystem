shader_type spatial;

render_mode world_vertex_coords,
	blend_mix,
	depth_draw_opaque,
	cull_back,
	diffuse_lambert,
	specular_schlick_ggx;


/*
===============================================================================
	TERRAIN DETAIL

	- 3 slope zones
	- separate above / underwater palettes
	- cheap height-zone tinting
	- underwater depth tint
	- wet shoreline
	- macro variation
	- 2-way projected cliff diffuse
	- 2-way projected cliff normal
	- optional displacement map as cavity/detail
	- distance fade

	Heavy work runs only on Zone 3.
===============================================================================
*/


// ============================================================================
// ZONE 1 — FLAT
// ============================================================================

group_uniforms Zone_1;

uniform vec4 zone1_above_color : source_color =
	vec4(0.32, 0.46, 0.17, 1.0);

uniform vec4 zone1_underwater_color : source_color =
	vec4(0.42, 0.40, 0.26, 1.0);

uniform float zone1_above_roughness :
	hint_range(0.0, 1.0) = 0.82;

uniform float zone1_underwater_roughness :
	hint_range(0.0, 1.0) = 0.72;

group_uniforms;


// ============================================================================
// ZONE 2 — SLOPE / SCREE
// ============================================================================

group_uniforms Zone_2;

uniform vec4 zone2_above_color : source_color =
	vec4(0.42, 0.38, 0.27, 1.0);

uniform vec4 zone2_underwater_color : source_color =
	vec4(0.34, 0.35, 0.28, 1.0);

uniform float zone2_above_roughness :
	hint_range(0.0, 1.0) = 0.78;

uniform float zone2_underwater_roughness :
	hint_range(0.0, 1.0) = 0.70;

group_uniforms;


// ============================================================================
// ZONE 3 — CLIFF
// ============================================================================

group_uniforms Zone_3_Cliff;

uniform vec4 zone3_above_color : source_color =
	vec4(0.40, 0.39, 0.37, 1.0);

uniform vec4 zone3_underwater_color : source_color =
	vec4(0.30, 0.33, 0.32, 1.0);

uniform float zone3_above_roughness :
	hint_range(0.0, 1.0) = 0.72;

uniform float zone3_underwater_roughness :
	hint_range(0.0, 1.0) = 0.64;


// ----- DIFFUSE --------------------------------------------------------------

uniform bool use_cliff_texture = false;

uniform sampler2D cliff_texture :
	source_color,
	filter_linear_mipmap_anisotropic,
	repeat_enable;

uniform float cliff_texture_scale :
	hint_range(0.001, 4.0, 0.001) = 0.10;

uniform float cliff_texture_amount :
	hint_range(0.0, 1.0) = 0.65;


// Prevents a dark photographic diffuse from swallowing Zone 3 colour.
uniform float cliff_texture_contrast :
	hint_range(0.0, 2.0) = 0.55;

uniform float cliff_texture_color_strength :
	hint_range(0.0, 1.0) = 0.45;


// ----- NORMAL ---------------------------------------------------------------

uniform bool use_cliff_normal = false;

uniform sampler2D cliff_normal_texture :
	hint_normal,
	filter_linear_mipmap_anisotropic,
	repeat_enable;

uniform float cliff_normal_strength :
	hint_range(0.0, 2.0) = 0.30;


// ----- PROJECTION -----------------------------------------------------------

uniform float cliff_projection_sharpness :
	hint_range(1.0, 16.0) = 4.0;

group_uniforms;


// ============================================================================
// CLIFF HEIGHT / CAVITY
// ============================================================================

group_uniforms Cliff_Height_Cavity;


// Put seaside_rock_disp_4k.png here.
uniform bool use_cliff_height_cavity = false;

uniform sampler2D cliff_height_texture :
	filter_linear_mipmap_anisotropic,
	repeat_enable;


// Black = low / cavity
// White = high / protruding.
//
// These two values define which dark part becomes cavity.
uniform float cliff_cavity_low :
	hint_range(0.0, 1.0) = 0.20;

uniform float cliff_cavity_high :
	hint_range(0.0, 1.0) = 0.55;


// How much cavities darken the rock.
uniform float cliff_cavity_strength :
	hint_range(0.0, 1.0) = 0.22;


// Small roughness increase inside recesses.
uniform float cliff_cavity_roughness :
	hint_range(0.0, 0.5) = 0.08;


// Optional subtle brightening of high areas.
uniform float cliff_height_highlight :
	hint_range(0.0, 0.5) = 0.05;

group_uniforms;


// ============================================================================
// SLOPE ZONES
// ============================================================================

group_uniforms Slope_Zones;

/*
	steepness = 1 - normal.y

	approx:
	0.00 = 0 degrees
	0.06 = ~20°
	0.13 = ~30°
	0.29 = ~45°
	0.50 = ~60°
	1.00 = vertical
*/

uniform float zone12_steepness :
	hint_range(0.0, 1.0) = 0.10;

uniform float zone12_softness :
	hint_range(0.001, 0.3) = 0.05;


uniform float zone23_steepness :
	hint_range(0.0, 1.0) = 0.28;

uniform float zone23_softness :
	hint_range(0.001, 0.3) = 0.07;

group_uniforms;


// ============================================================================
// HEIGHT ZONES — ABOVE WATER
// ============================================================================

group_uniforms Height_Zones;

uniform bool use_height_zones = true;


// Absolute WORLD Y.
//
// Example:
// low terrain / beach -> mid terrain
uniform float height_zone12_y = 8.0;


// mid terrain -> high terrain
uniform float height_zone23_y = 55.0;


uniform float height_zone12_blend :
	hint_range(0.01, 50.0, 0.1) = 6.0;

uniform float height_zone23_blend :
	hint_range(0.01, 100.0, 0.1) = 18.0;


// These are MULTIPLIERS over the slope-zone colours.
//
// White means "do not change".
uniform vec4 height_low_tint : source_color =
	vec4(1.05, 0.96, 0.78, 1.0);

uniform vec4 height_mid_tint : source_color =
	vec4(0.95, 1.00, 0.90, 1.0);

uniform vec4 height_high_tint : source_color =
	vec4(0.90, 0.91, 0.92, 1.0);


uniform float height_tint_strength :
	hint_range(0.0, 1.0) = 0.35;

group_uniforms;


// ============================================================================
// UNDERWATER DEPTH
// ============================================================================

group_uniforms Underwater_Depth;


// Depth where shallow seabed colour begins.
// Metres BELOW Sea Level.
uniform float shallow_depth = 0.0;


// At and below this depth deep tint reaches full strength.
uniform float deep_depth = 35.0;


// Multipliers, not absolute colours.
uniform vec4 shallow_underwater_tint : source_color =
	vec4(1.08, 1.02, 0.82, 1.0);

uniform vec4 deep_underwater_tint : source_color =
	vec4(0.64, 0.78, 0.82, 1.0);


uniform float underwater_depth_tint_strength :
	hint_range(0.0, 1.0) = 0.45;

group_uniforms;


// ============================================================================
// MACRO VARIATION
// ============================================================================

group_uniforms Macro_Variation;

uniform bool use_macro_noise = false;

uniform sampler2D macro_noise_texture :
	filter_linear_mipmap_anisotropic,
	repeat_enable;

uniform float macro_noise_scale :
	hint_range(0.0001, 1.0, 0.0001) = 0.012;

uniform float macro_color_strength :
	hint_range(0.0, 1.0) = 0.14;


uniform float posterize_amount :
	hint_range(0.0, 1.0) = 0.08;

uniform float posterize_steps :
	hint_range(2.0, 16.0, 1.0) = 5.0;

group_uniforms;

// ============================================================================
// WATERLINE
// ============================================================================

group_uniforms Waterline;

uniform float sea_level_y = 0.0;


// Above ↔ underwater material transition.
uniform float waterline_blend :
	hint_range(0.01, 20.0, 0.01) = 0.75;


uniform float wet_band_above :
	hint_range(0.0, 20.0, 0.05) = 0.8;

uniform float wet_band_below :
	hint_range(0.0, 20.0, 0.05) = 1.5;


uniform float wet_darkening :
	hint_range(0.0, 0.8) = 0.14;

uniform float wet_roughness :
	hint_range(0.0, 1.0) = 0.42;

uniform float wet_strength :
	hint_range(0.0, 1.0) = 1.0;

group_uniforms;


// ============================================================================
// DETAIL FADE
// ============================================================================

group_uniforms Detail_Fade;

uniform float detail_fade_start :
	hint_range(0.0, 1000.0, 1.0) = 35.0;

uniform float detail_fade_end :
	hint_range(1.0, 3000.0, 1.0) = 140.0;

uniform float detail_softness :
	hint_range(0.0, 1.0) = 0.10;

group_uniforms;


// ============================================================================
// LIGHTING
// ============================================================================

group_uniforms Lighting;

uniform float specular :
	hint_range(0.0, 1.0) = 0.10;

group_uniforms;


// ============================================================================

varying vec3 world_position;


// ============================================================================
// HELPERS
// ============================================================================

float safe_smoothstep(
	float center,
	float softness,
	float value
)
{
	float s = max(softness, 0.0001);

	return smoothstep(
		center - s,
		center + s,
		value
	);
}


float signed_nonzero(float value)
{
	return value >= 0.0 ? 1.0 : -1.0;
}


vec3 decode_normal(vec3 encoded)
{
	return normalize(
		encoded * 2.0 - 1.0
	);
}


// X-facing wall.
vec3 cliff_normal_x(
	vec3 tangent_normal,
	float sign_x
)
{
	vec3 tangent =
		vec3(
			0.0,
			0.0,
			-sign_x
		);

	vec3 bitangent =
		vec3(
			0.0,
			1.0,
			0.0
		);

	vec3 plane_normal =
		vec3(
			sign_x,
			0.0,
			0.0
		);

	return normalize(
		tangent * tangent_normal.x +
		bitangent * tangent_normal.y +
		plane_normal * tangent_normal.z
	);
}


// Z-facing wall.
vec3 cliff_normal_z(
	vec3 tangent_normal,
	float sign_z
)
{
	vec3 tangent =
		vec3(
			sign_z,
			0.0,
			0.0
		);

	vec3 bitangent =
		vec3(
			0.0,
			1.0,
			0.0
		);

	vec3 plane_normal =
		vec3(
			0.0,
			0.0,
			sign_z
		);

	return normalize(
		tangent * tangent_normal.x +
		bitangent * tangent_normal.y +
		plane_normal * tangent_normal.z
	);
}


// ============================================================================
// VERTEX
// ============================================================================

void vertex()
{
	world_position = VERTEX;
}


// ============================================================================
// FRAGMENT
// ============================================================================

void fragment()
{
	// ------------------------------------------------------------------------
	// NORMAL
	// ------------------------------------------------------------------------

	vec3 normal_view =
		normalize(NORMAL);

	vec3 normal_world =
		normalize(
			mat3(INV_VIEW_MATRIX) *
			normal_view
		);


	// ------------------------------------------------------------------------
	// SLOPE ZONES
	// ------------------------------------------------------------------------

	float upness =
		clamp(
			normal_world.y,
			0.0,
			1.0
		);

	float steepness =
		1.0 - upness;


	float zone12 =
		safe_smoothstep(
			zone12_steepness,
			zone12_softness,
			steepness
		);


	float zone23 =
		safe_smoothstep(
			zone23_steepness,
			zone23_softness,
			steepness
		);


	// ------------------------------------------------------------------------
	// ABOVE / UNDER WATER
	// ------------------------------------------------------------------------

	float underwater =
		1.0 -
		smoothstep(
			sea_level_y - waterline_blend,
			sea_level_y + waterline_blend,
			world_position.y
		);


	// ------------------------------------------------------------------------
	// BASE ZONE MATERIALS
	// ------------------------------------------------------------------------

	vec3 zone1_color =
		mix(
			zone1_above_color.rgb,
			zone1_underwater_color.rgb,
			underwater
		);

	vec3 zone2_color =
		mix(
			zone2_above_color.rgb,
			zone2_underwater_color.rgb,
			underwater
		);

	vec3 zone3_color =
		mix(
			zone3_above_color.rgb,
			zone3_underwater_color.rgb,
			underwater
		);


	float zone1_roughness =
		mix(
			zone1_above_roughness,
			zone1_underwater_roughness,
			underwater
		);

	float zone2_roughness =
		mix(
			zone2_above_roughness,
			zone2_underwater_roughness,
			underwater
		);

	float zone3_roughness =
		mix(
			zone3_above_roughness,
			zone3_underwater_roughness,
			underwater
		);


	vec3 base_color =
		mix(
			zone1_color,
			zone2_color,
			zone12
		);

	base_color =
		mix(
			base_color,
			zone3_color,
			zone23
		);


	float base_roughness =
		mix(
			zone1_roughness,
			zone2_roughness,
			zone12
		);

	base_roughness =
		mix(
			base_roughness,
			zone3_roughness,
			zone23
		);


	// ------------------------------------------------------------------------
	// ABOVE-WATER HEIGHT ZONES
	// ------------------------------------------------------------------------

	if (use_height_zones)
	{
		float h12 =
			smoothstep(
				height_zone12_y -
					height_zone12_blend,
				height_zone12_y +
					height_zone12_blend,
				world_position.y
			);


		float h23 =
			smoothstep(
				height_zone23_y -
					height_zone23_blend,
				height_zone23_y +
					height_zone23_blend,
				world_position.y
			);


		vec3 altitude_tint =
			mix(
				height_low_tint.rgb,
				height_mid_tint.rgb,
				h12
			);

		altitude_tint =
			mix(
				altitude_tint,
				height_high_tint.rgb,
				h23
			);


		// Only above water.
		float above_factor =
			1.0 - underwater;


		base_color *=
			mix(
				vec3(1.0),
				altitude_tint,
				height_tint_strength *
					above_factor
			);
	}


	// ------------------------------------------------------------------------
	// UNDERWATER DEPTH GRADIENT
	// ------------------------------------------------------------------------

	float water_depth =
		max(
			sea_level_y -
				world_position.y,
			0.0
		);


	float safe_deep_depth =
		max(
			deep_depth,
			shallow_depth + 0.001
		);


	float depth_factor =
		smoothstep(
			shallow_depth,
			safe_deep_depth,
			water_depth
		);


	vec3 underwater_depth_tint =
		mix(
			shallow_underwater_tint.rgb,
			deep_underwater_tint.rgb,
			depth_factor
		);


	base_color *=
		mix(
			vec3(1.0),
			underwater_depth_tint,
			underwater *
				underwater_depth_tint_strength
		);


	// ------------------------------------------------------------------------
	// MACRO NOISE
	// ------------------------------------------------------------------------

	if (use_macro_noise)
	{
		float noise_value =
			texture(
				macro_noise_texture,
				world_position.xz *
					macro_noise_scale
			).r;


		float steps =
			max(
				posterize_steps,
				2.0
			);


		float stepped =
			floor(
				noise_value *
					(steps - 1.0) +
				0.5
			) /
			(steps - 1.0);


		noise_value =
			mix(
				noise_value,
				stepped,
				posterize_amount
			);


		float centered =
			noise_value * 2.0 - 1.0;


		base_color *=
			1.0 +
			centered *
				macro_color_strength;
	}


	// ------------------------------------------------------------------------
	// DISTANCE DETAIL
	// ------------------------------------------------------------------------

	float camera_distance =
		length(
			CAMERA_POSITION_WORLD -
				world_position
		);


	float safe_detail_end =
		max(
			detail_fade_end,
			detail_fade_start + 0.01
		);


	float detail_distance =
		1.0 -
		smoothstep(
			detail_fade_start,
			safe_detail_end,
			camera_distance
		);


	float detail_weight =
		detail_distance *
		(1.0 - detail_softness * 0.75);


	// ------------------------------------------------------------------------
	// CLIFF DETAIL
	// ------------------------------------------------------------------------

	vec3 final_world_normal =
		normal_world;

	float cavity_mask = 0.0;


	if (
		zone23 > 0.001 &&
		(
			use_cliff_texture ||
			use_cliff_normal ||
			use_cliff_height_cavity
		)
	)
	{
		vec3 world_dx =
			dFdx(world_position);

		vec3 world_dy =
			dFdy(world_position);


		float sign_x =
			signed_nonzero(
				normal_world.x
			);

		float sign_z =
			signed_nonzero(
				normal_world.z
			);


		// --------------------------------------------------------------------
		// TWO-WAY CLIFF PROJECTION
		// --------------------------------------------------------------------

		vec2 uv_x =
			vec2(
				-world_position.z *
					sign_x,
				world_position.y
			) *
			cliff_texture_scale;


		vec2 uv_z =
			vec2(
				world_position.x *
					sign_z,
				world_position.y
			) *
			cliff_texture_scale;


		vec2 dx_x =
			vec2(
				-world_dx.z *
					sign_x,
				world_dx.y
			) *
			cliff_texture_scale;


		vec2 dy_x =
			vec2(
				-world_dy.z *
					sign_x,
				world_dy.y
			) *
			cliff_texture_scale;


		vec2 dx_z =
			vec2(
				world_dx.x *
					sign_z,
				world_dx.y
			) *
			cliff_texture_scale;


		vec2 dy_z =
			vec2(
				world_dy.x *
					sign_z,
				world_dy.y
			) *
			cliff_texture_scale;


		vec2 projection_weights =
			pow(
				max(
					abs(normal_world.xz),
					vec2(0.0001)
				),
				vec2(
					cliff_projection_sharpness
				)
			);


		float weight_sum =
			projection_weights.x +
			projection_weights.y;


		projection_weights /=
			max(
				weight_sum,
				0.0001
			);


		// --------------------------------------------------------------------
		// CLIFF DIFFUSE
		// --------------------------------------------------------------------

		if (use_cliff_texture)
		{
			vec4 rock_x =
				textureGrad(
					cliff_texture,
					uv_x,
					dx_x,
					dy_x
				);


			vec4 rock_z =
				textureGrad(
					cliff_texture,
					uv_z,
					dx_z,
					dy_z
				);


			vec3 rock =
				rock_x.rgb *
					projection_weights.x +
				rock_z.rgb *
					projection_weights.y;


			// Preserve texture chroma while constraining brightness.
			float rock_luma =
				dot(
					rock,
					vec3(
						0.2126,
						0.7152,
						0.0722
					)
				);


			vec3 rock_chroma =
				rock /
				max(
					rock_luma,
					0.08
				);


			rock_chroma =
				mix(
					vec3(1.0),
					clamp(
						rock_chroma,
						vec3(0.65),
						vec3(1.45)
					),
					cliff_texture_color_strength
				);


			float rock_brightness =
				mix(
					1.0,
					mix(
						0.80,
						1.20,
						rock_luma
					),
					cliff_texture_contrast
				);


			vec3 rock_detail =
				rock_chroma *
					rock_brightness;


			vec3 textured_cliff =
				zone3_color *
				mix(
					vec3(1.0),
					rock_detail,
					cliff_texture_amount *
						detail_weight
				);


			base_color =
				mix(
					base_color,
					textured_cliff,
					zone23
				);
		}


		// --------------------------------------------------------------------
		// DISPLACEMENT MAP AS CAVITY
		// --------------------------------------------------------------------

		if (use_cliff_height_cavity)
		{
			float height_x =
				textureGrad(
					cliff_height_texture,
					uv_x,
					dx_x,
					dy_x
				).r;


			float height_z =
				textureGrad(
					cliff_height_texture,
					uv_z,
					dx_z,
					dy_z
				).r;


			float rock_height =
				height_x *
					projection_weights.x +
				height_z *
					projection_weights.y;


			// Low parts of displacement become cavity.
			cavity_mask =
				1.0 -
				smoothstep(
					cliff_cavity_low,
					max(
						cliff_cavity_high,
						cliff_cavity_low + 0.001
					),
					rock_height
				);


			cavity_mask *=
				cliff_cavity_strength *
				detail_weight *
				zone23;


			base_color *=
				1.0 -
				cavity_mask;


			// Very cheap highlight for exposed/high rock.
			float high_mask =
				smoothstep(
					cliff_cavity_high,
					1.0,
					rock_height
				);


			base_color *=
				1.0 +
				high_mask *
					cliff_height_highlight *
					detail_weight *
					zone23;
		}


		// --------------------------------------------------------------------
		// CLIFF NORMAL
		// --------------------------------------------------------------------

		if (use_cliff_normal)
		{
			vec3 tangent_normal_x =
				decode_normal(
					textureGrad(
						cliff_normal_texture,
						uv_x,
						dx_x,
						dy_x
					).rgb
				);


			vec3 tangent_normal_z =
				decode_normal(
					textureGrad(
						cliff_normal_texture,
						uv_z,
						dx_z,
						dy_z
					).rgb
				);


			vec3 normal_x_world =
				cliff_normal_x(
					tangent_normal_x,
					sign_x
				);


			vec3 normal_z_world =
				cliff_normal_z(
					tangent_normal_z,
					sign_z
				);


			vec3 projected_normal =
				normalize(
					normal_x_world *
						projection_weights.x +
					normal_z_world *
						projection_weights.y
				);


			float normal_strength =
				clamp(
					cliff_normal_strength *
						detail_weight *
						zone23,
					0.0,
					1.0
				);


			final_world_normal =
				normalize(
					mix(
						normal_world,
						projected_normal,
						normal_strength
					)
				);
		}
	}


	// ------------------------------------------------------------------------
	// WET SHORELINE
	// ------------------------------------------------------------------------

	float relative_y =
		world_position.y -
			sea_level_y;


	float wet_above = 0.0;

	if (wet_band_above > 0.0001)
	{
		wet_above =
			1.0 -
			smoothstep(
				0.0,
				wet_band_above,
				max(
					relative_y,
					0.0
				)
			);
	}


	float wet_below = 0.0;

	if (wet_band_below > 0.0001)
	{
		wet_below =
			1.0 -
			smoothstep(
				0.0,
				wet_band_below,
				max(
					-relative_y,
					0.0
				)
			);
	}


	float wet_mask =
		mix(
			wet_below,
			wet_above,
			step(
				0.0,
				relative_y
			)
		);


	wet_mask *=
		wet_strength;


	base_color *=
		1.0 -
			wet_darkening *
				wet_mask;


	base_roughness =
		mix(
			base_roughness,
			wet_roughness,
			wet_mask
		);


	// ------------------------------------------------------------------------
	// FINAL
	// ------------------------------------------------------------------------

	ALBEDO =
		clamp(
			base_color,
			vec3(0.0),
			vec3(1.0)
		);


	NORMAL =
		normalize(
			mat3(VIEW_MATRIX) *
				final_world_normal
		);


	ROUGHNESS =
		clamp(
			base_roughness +
				cavity_mask *
					cliff_cavity_roughness,
			0.0,
			1.0
		);


	SPECULAR =
		specular;
}
