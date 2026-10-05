shader_type spatial;
render_mode cull_disabled;

uniform sampler2D albedo_texture : source_color, filter_linear_mipmap;
uniform sampler2D normal_texture : hint_normal, filter_linear_mipmap;
uniform sampler2D roughness_texture : filter_linear_mipmap;

uniform vec4 albedo_tint : source_color = vec4(1.0);
uniform float alpha_cutoff : hint_range(0.0, 1.0) = 0.5;
uniform float normal_strength : hint_range(0.0, 2.0) = 1.0;
uniform float roughness_multiplier : hint_range(0.0, 2.0) = 1.0;

uniform vec2 wind_direction = vec2(1.0, 0.25);
uniform float wind_strength = 0.08;
uniform float wind_speed = 1.3;
uniform float wind_spatial_frequency = 0.7;
uniform float bend_start_height = 0.015;
uniform float bend_full_height = 0.25;

uniform bool interaction_enabled = false;
uniform vec3 player_world_position = vec3(0.0);
uniform float flatten_radius = 0.38;
uniform float bend_radius = 0.75;
uniform float flatten_amount = 0.95;
uniform float bend_strength = 0.20;

void vertex() {
	float original_y = VERTEX.y;
	float bend_weight = smoothstep(bend_start_height, bend_full_height, original_y);
	if (bend_weight > 0.0) {
		vec3 world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;

		// Terrain3D instance basis contains its Y rotation and uniform scale.
		float basis_length_squared = max(dot(MODEL_MATRIX[0].xyz, MODEL_MATRIX[0].xyz), 0.0001);
		vec2 world_wind = normalize(wind_direction);
		vec3 wind_axis = vec3(world_wind.x, 0.0, world_wind.y);
		vec2 local_wind = vec2(dot(wind_axis, MODEL_MATRIX[0].xyz), dot(wind_axis, MODEL_MATRIX[2].xyz)) / basis_length_squared;

		vec2 origin_xz = MODEL_MATRIX[3].xz;
		float variation = fract(dot(fract(origin_xz * vec2(0.1031, 0.11369)), vec2(17.17, 13.13)));
		float phase = TIME * wind_speed + dot(world_position.xz, world_wind) * wind_spatial_frequency + variation;
		float sway = sin(phase) * wind_strength * mix(0.85, 1.15, variation);
		float wind_attenuation = 1.0;

		if (interaction_enabled) {
			vec2 away = world_position.xz - player_world_position.xz;
			float distance_squared = dot(away, away);
			float bend_radius_squared = bend_radius * bend_radius;
			if (distance_squared < bend_radius_squared) {
				float flatten_radius_squared = flatten_radius * flatten_radius;
				float outer_bend = 1.0 - smoothstep(flatten_radius_squared, bend_radius_squared, distance_squared);
				float flatten = flatten_amount * (1.0 - smoothstep(0.0, flatten_radius_squared, distance_squared));
				vec2 away_unit = distance_squared > 0.0001 ? away * inversesqrt(distance_squared) : world_wind;
				vec3 away_world = vec3(away_unit.x, 0.0, away_unit.y);
				vec2 away_local = vec2(dot(away_world, MODEL_MATRIX[0].xyz), dot(away_world, MODEL_MATRIX[2].xyz)) / basis_length_squared;
				VERTEX.xz += away_local * (original_y * bend_weight * (bend_strength * outer_bend + flatten));
				VERTEX.y = original_y * (1.0 - flatten * bend_weight);
				wind_attenuation = 1.0 - 0.9 * flatten;
			}
		}
		VERTEX.xz += local_wind * (sway * bend_weight * wind_attenuation);
	}
}

void fragment() {
	vec4 base = texture(albedo_texture, UV);
	vec3 normal_tex = texture(normal_texture, UV).rgb;
	float roughness = texture(roughness_texture, UV).r;

	ALBEDO = base.rgb * albedo_tint.rgb * COLOR.rgb;
	ALPHA = base.a * albedo_tint.a;
	ALPHA_SCISSOR_THRESHOLD = alpha_cutoff;
	NORMAL_MAP = normal_tex;
	NORMAL_MAP_DEPTH = normal_strength;
	ROUGHNESS = clamp(roughness * roughness_multiplier, 0.0, 1.0);
	METALLIC = 0.0;
	AO = 1.0;
}
