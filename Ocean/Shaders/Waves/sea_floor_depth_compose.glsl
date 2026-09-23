#[compute]
#version 450

// Crest 4 Sea Floor Depth data contract (MIT), pinned commit
// db0658ff0b2e93e4a9e28cc2867509658b0ecc00:
// LodDataMgrSeaFloorDepth.cs and OceanDepths.shader.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

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

layout(std430, set = 0, binding = 0) readonly buffer AnimatedWaveLodBuffer
{
	AnimatedWaveLodParams lods[];
} u_lod_data;

// std430 stride = 48 bytes. Crest BlendOp Max makes registration order irrelevant.
struct SeaFloorDepthInputDescriptor
{
	vec4 center_xz_axis_x;
	vec4 axis_z_size_xz;
	vec4 bottom_height_padding;
};

layout(std430, set = 0, binding = 1) readonly buffer SeaFloorDepthInputBuffer
{
	SeaFloorDepthInputDescriptor inputs[];
} u_inputs;

layout(r16f, set = 0, binding = 2) uniform writeonly image2DArray u_depth_field;

layout(push_constant, std430) uniform PushConstants
{
	uint resolution;
	uint lod_count;
	uint input_count;
	uint padding;
} pc;

const float DEEP_SENTINEL = -65504.0;

void main()
{
	uvec3 id = gl_GlobalInvocationID;
	if (id.x >= pc.resolution || id.y >= pc.resolution || id.z >= pc.lod_count) return;

	AnimatedWaveLodParams lod = u_lod_data.lods[id.z];
	vec2 uv = (vec2(id.xy) + vec2(0.5)) / lod.texture_resolution;
	vec2 world_xz = lod.center_xz +
		(uv - vec2(0.5)) * lod.texel_width * lod.texture_resolution;

	float terrain_y = DEEP_SENTINEL;
	for (uint input_index = 0u; input_index < pc.input_count; ++input_index)
	{
		SeaFloorDepthInputDescriptor descriptor = u_inputs.inputs[input_index];
		vec2 delta = world_xz - descriptor.center_xz_axis_x.xy;
		vec2 local = vec2(
			dot(delta, descriptor.center_xz_axis_x.zw),
			dot(delta, descriptor.axis_z_size_xz.xy));
		vec2 half_size = 0.5 * descriptor.axis_z_size_xz.zw;
		if (all(lessThanEqual(abs(local), half_size)))
			terrain_y = max(terrain_y, descriptor.bottom_height_padding.x);
	}

	imageStore(u_depth_field, ivec3(id), vec4(terrain_y, 0.0, 0.0, 0.0));
}
