#[compute]
#version 450

layout(
	local_size_x = 8,
	local_size_y = 8,
	local_size_z = 1
) in;


// -----------------------------------------------------------------------------
// Direct Animated Waves contribution.
//
// Crest equivalent:
//
//     LodDataMgrAnimWaves._waveBuffers
//
// Each layer contains ONLY the contribution assigned directly
// to that spatial LOD.
// -----------------------------------------------------------------------------

layout(
	rgba16f,
	set = 0,
	binding = 0
)
uniform readonly image2DArray u_direct_wave_field;


// -----------------------------------------------------------------------------
// Spatial Animated Waves LOD metadata.
//
// Must match:
//
//     AnimatedWaveLodGpuBuffer
//
// Crest equivalent:
//
//     CascadeParams
//
// std430 stride = 32 bytes.
// -----------------------------------------------------------------------------

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


layout(
	std430,
	set = 0,
	binding = 1
)
readonly buffer AnimatedWaveLodBuffer
{
	AnimatedWaveLodParams lods[];
}
u_lod_data;


// -----------------------------------------------------------------------------
// Canonical cumulative AnimatedWaveField.
//
// Crest equivalent:
//
//     _LD_TexArray_AnimatedWaves
//
// This image is processed strictly:
//
//     coarsest LOD
//         -> ...
//         -> finest LOD
//
// For current LOD:
//
//     Final[L] = Direct[L] + Resample(Final[L + 1])
//
// Last LOD:
//
//     Final[last] = Direct[last]
//
// XYZ = cumulative final displacement.
//
// A = variance / energy belonging to the DIRECT contribution only.
//
// This follows Crest ShapeCombine: variance is already cumulative in the
// wavelength input system and is NOT added from the next spatial LOD.
// Stage A currently writes zero variance.
// -----------------------------------------------------------------------------

layout(
	rgba16f,
	set = 0,
	binding = 2
)
uniform coherent image2DArray u_animated_wave_field;


// -----------------------------------------------------------------------------
// Push constants.
//
// One dispatch processes ONE spatial LOD.
//
//  0 : uint resolution
//  4 : uint lod_count
//  8 : uint current_lod
// 12 : uint padding
//
// Total = 16 bytes.
// -----------------------------------------------------------------------------

layout(
	push_constant,
	std430
)
uniform PushConstants
{
	uint resolution;

	uint lod_count;

	uint current_lod;

	uint padding_0;
}
pc;


// -----------------------------------------------------------------------------
// Crest WorldToUV / UVToWorld equivalents.
// -----------------------------------------------------------------------------

vec2 uv_to_world(
	vec2 uv,
	AnimatedWaveLodParams lod)
{
	return
		lod.texel_width *
		lod.texture_resolution *
		(
			uv -
			vec2(0.5)
		) +
		lod.center_xz;
}


vec2 world_to_uv(
	vec2 world_xz,
	AnimatedWaveLodParams lod)
{
	return
		(
			world_xz -
			lod.center_xz
		) /
		(
			lod.texel_width *
			lod.texture_resolution
		) +
		vec2(0.5);
}


// -----------------------------------------------------------------------------
// Crest ShapeCombine.compute equivalent of SampleDisplacementsCompute().
//
// Crest cannot use normal filtered sampling from the same texture that is also
// bound for UAV writes, so it performs the bilinear interpolation manually.
//
// We preserve that contract here instead of introducing a separate sampled
// view of AnimatedWaveField.
//
// Only XYZ displacement is inherited from the coarser final LOD.
// -----------------------------------------------------------------------------

vec3 sample_final_lod_bilinear(
	vec2 uv,
	uint lod_index)
{
	float resolution =
		float(
			pc.resolution);


	//
	// Convert UV to texel coordinates.
	//

	vec2 pixel_coord =
		uv *
		resolution;


	//
	// Texture values live at pixel centres.
	//

	vec2 pixel_coord_centres =
		pixel_coord -
		vec2(0.5);


	//
	// The next spatial LOD is twice the world size of the current one,
	// therefore normal combine sampling falls well inside its texture.
	//
	// Clamp remains here to match Crest's defensive behaviour.
	//

	pixel_coord_centres =
		clamp(
			pixel_coord_centres,
			vec2(0.0),
			vec2(
				resolution -
				1.0));


	ivec2 bottom_left =
		ivec2(
			floor(
				pixel_coord_centres));


	vec2 fraction =
		fract(
			pixel_coord_centres);


	//
	// Defensive integer clamp.
	//
	// Ordinary current -> next LOD sampling lies around the middle half
	// of the next texture, so this does not alter normal Crest behaviour.
	//

	ivec2 maximum_coord =
		ivec2(
			int(pc.resolution) -
			1);


	ivec2 bottom_right =
		min(
			bottom_left +
				ivec2(
					1,
					0),
			maximum_coord);


	ivec2 top_left =
		min(
			bottom_left +
				ivec2(
					0,
					1),
			maximum_coord);


	ivec2 top_right =
		min(
			bottom_left +
				ivec2(
					1,
					1),
			maximum_coord);


	vec3 data_bottom_left =
		imageLoad(
			u_animated_wave_field,
			ivec3(
				bottom_left,
				int(
					lod_index)))
			.xyz;


	vec3 data_bottom_right =
		imageLoad(
			u_animated_wave_field,
			ivec3(
				bottom_right,
				int(
					lod_index)))
			.xyz;


	vec3 data_top_left =
		imageLoad(
			u_animated_wave_field,
			ivec3(
				top_left,
				int(
					lod_index)))
			.xyz;


	vec3 data_top_right =
		imageLoad(
			u_animated_wave_field,
			ivec3(
				top_right,
				int(
					lod_index)))
			.xyz;


	vec3 bottom =
		mix(
			data_bottom_left,
			data_bottom_right,
			fraction.x);


	vec3 top =
		mix(
			data_top_left,
			data_top_right,
			fraction.x);


	return
		mix(
			bottom,
			top,
			fraction.y);
}


// -----------------------------------------------------------------------------
// ShapeCombine.
//
// This is the direct equivalent of Crest ShapeCombineBase:
//
//     result = direct/current wave buffer
//
//     if not last LOD:
//         world position = UVToWorld(current)
//         next UV       = WorldToUV(world position, next)
//         result.xyz   += Final[next]
//
//     Final[current] = result
//
// IMPORTANT:
//
// Dispatch order is NOT encoded in this shader.
//
// AnimatedWaveCombinePass must dispatch:
//
//     last
//     last - 1
//     ...
//     0
//
// with GPU ordering / memory visibility between dependent dispatches.
// -----------------------------------------------------------------------------

void main()
{
	uvec2 id =
		gl_GlobalInvocationID.xy;


	if (id.x >=
			pc.resolution ||
		id.y >=
			pc.resolution)
	{
		return;
	}


	if (pc.current_lod >=
		pc.lod_count)
	{
		return;
	}


	uint lod_index =
		pc.current_lod;


	//
	// Start with waves assigned directly to this spatial LOD.
	//

	vec4 direct_data =
		imageLoad(
			u_direct_wave_field,
			ivec3(
				ivec2(
					id),
				int(
					lod_index)));


	vec3 result =
		direct_data.xyz;


	float variance =
		direct_data.w;


	//
	// Crest final/coarsest LOD:
	//
	// no higher spatial LOD exists, so there is nothing to combine down.
	//

	if (lod_index + 1u <
		pc.lod_count)
	{
		AnimatedWaveLodParams current_lod =
			u_lod_data.lods[
				lod_index];


		AnimatedWaveLodParams next_lod =
			u_lod_data.lods[
				lod_index +
				1u];


		//
		// Pixel-centre UV of the CURRENT spatial LOD.
		//

		vec2 current_uv =
			(
				vec2(
					id) +
				vec2(0.5)
			) /
			current_lod.texture_resolution;


		//
		// Current LOD texture coordinate
		//     -> world position.
		//

		vec2 world_xz =
			uv_to_world(
				current_uv,
				current_lod);


		//
		// Same physical world position
		//     -> next/coarser LOD UV.
		//

		vec2 next_uv =
			world_to_uv(
				world_xz,
				next_lod);


		//
		// Crucial Crest contract:
		//
		// Read FINAL[next], not Direct[next].
		//
		// This is what makes the hierarchy cumulative.
		//

		result +=
			sample_final_lod_bilinear(
				next_uv,
				lod_index +
					1u);
	}


	imageStore(
		u_animated_wave_field,
		ivec3(
			ivec2(
				id),
			int(
				lod_index)),
		vec4(
			result,
			variance));
}
