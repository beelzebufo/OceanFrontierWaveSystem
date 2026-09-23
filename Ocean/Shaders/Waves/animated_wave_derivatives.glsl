#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0)
uniform readonly image2DArray u_animated_wave_field;

struct AnimatedWaveLodParams
{
	vec2 center_xz;
	float scale;
	float texture_resolution;
	float one_over_texture_resolution;
	float texel_width;
	float weight;
	float max_wavelength;
};

layout(std430, set = 0, binding = 1)
readonly buffer AnimatedWaveLodBuffer
{
	AnimatedWaveLodParams lods[];
}
u_lod_data;

// RGB = finite upward-oriented geometric normal.
// A   = raw horizontal displacement Jacobian determinant.
layout(rgba16f, set = 0, binding = 2)
uniform writeonly image2DArray u_derivative_field;

void main()
{
	ivec3 field_size = imageSize(u_animated_wave_field);
	ivec3 coord = ivec3(gl_GlobalInvocationID);

	if (any(greaterThanEqual(coord, field_size)))
	{
		return;
	}

	// Forward neighbours clamp inside the current array slice.
	ivec2 maximum_coord = field_size.xy - ivec2(1);
	ivec2 x_coord = min(coord.xy + ivec2(1, 0), maximum_coord);
	ivec2 z_coord = min(coord.xy + ivec2(0, 1), maximum_coord);

	vec3 displacement = imageLoad(u_animated_wave_field, coord).xyz;
	vec3 displacement_x = imageLoad(
		u_animated_wave_field,
		ivec3(x_coord, coord.z)).xyz;
	vec3 displacement_z = imageLoad(
		u_animated_wave_field,
		ivec3(z_coord, coord.z)).xyz;

	float texel_width = u_lod_data.lods[coord.z].texel_width;
	vec3 delta_x = displacement_x - displacement;
	vec3 delta_z = displacement_z - displacement;

	vec3 tangent_x = vec3(texel_width, 0.0, 0.0) + delta_x;
	vec3 tangent_z = vec3(0.0, 0.0, texel_width) + delta_z;
	vec3 cross_value = cross(tangent_z, tangent_x);
	cross_value.y = max(cross_value.y, 0.000001);

	vec3 normal = normalize(cross_value);
	if (any(isnan(normal)) || any(isinf(normal)))
	{
		normal = vec3(0.0, 1.0, 0.0);
	}

	vec3 derivative_x = delta_x / texel_width;
	vec3 derivative_z = delta_z / texel_width;
	float j00 = 1.0 + derivative_x.x;
	float j01 = derivative_z.x;
	float j10 = derivative_x.z;
	float j11 = 1.0 + derivative_z.z;
	float determinant = j00 * j11 - j01 * j10;

	imageStore(u_derivative_field, coord, vec4(normal, determinant));
}
