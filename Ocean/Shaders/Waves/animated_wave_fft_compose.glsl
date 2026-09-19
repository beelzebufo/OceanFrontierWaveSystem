#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;


// Raw FFT displacement.
//
// XYZ:
//   horizontal X
//   vertical Y
//   horizontal Z
//
// FFT cascades are periodic frequency-band sources.
layout(set = 0, binding = 0)
uniform sampler2DArray u_fft_displacement;


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


// Must match AnimatedWaveLodGpuBuffer.
// std430 stride = 32 bytes.
layout(std430, set = 0, binding = 1)
readonly buffer AnimatedWaveLodBuffer
{
	AnimatedWaveLodParams lods[];
}
u_lod_data;


// Canonical final wave field.
//
// XYZ = displacement.
// A   = reserved variance/energy. Zero for this stage.
layout(rgba16f, set = 0, binding = 2)
uniform writeonly image2DArray u_animated_wave_field;


layout(push_constant, std430)
uniform PushConstants
{
	uint resolution;
	uint lod_count;
	uint fft_cascade_count;
	float wave_resolution_multiplier;
}
pc;


// Crest ShapeFFT relation:
//
// world size of raw FFT cascade c:
//
//     0.5 * 2^c
//
// minimum wavelength:
//
//     worldSize / 8
//
float fft_world_size(uint cascade_index)
{
	return 0.5 * exp2(float(cascade_index));
}


float fft_min_wavelength(uint cascade_index)
{
	return fft_world_size(cascade_index) / 8.0;
}


void main()
{
	uvec3 id = gl_GlobalInvocationID;

	if (id.x >= pc.resolution ||
		id.y >= pc.resolution ||
		id.z >= pc.lod_count)
	{
		return;
	}

	uint lod_index = id.z;

	AnimatedWaveLodParams lod =
		u_lod_data.lods[lod_index];


	// Pixel center in the spatial Animated Wave LOD.
	vec2 uv =
		(vec2(id.xy) + vec2(0.5)) /
		lod.texture_resolution;


	// Crest UVToWorld equivalent.
	vec2 world_xz =
		lod.center_xz +
		(uv - vec2(0.5)) *
		(lod.texel_width * lod.texture_resolution);


	// Crest spatial LOD direct wavelength range:
	//
	// min = maxWavelength / 2
	//
	// Final Animated Waves are cumulative, therefore this LOD also
	// contains all larger/coarser wave bands.
	float lod_min_wavelength =
		lod.max_wavelength * 0.5;


	vec3 displacement =
		vec3(0.0);


	for (uint cascade = 0u;
		 cascade < pc.fft_cascade_count;
		 ++cascade)
	{
		float source_min_wavelength =
			fft_min_wavelength(cascade);

		// This FFT band is too fine for this spatial LOD.
		if (source_min_wavelength + 1e-6 <
			lod_min_wavelength * pc.wave_resolution_multiplier)
		{
			continue;
		}

		float source_world_size =
			fft_world_size(cascade);


		// Matches the global-wave path in Crest
		// AnimWavesSpectrum.shader:
		//
		// worldPos / waveBufferSize
		//
		// Sampler repeat handles the periodic FFT domain.
		vec2 fft_uv =
			world_xz /
			source_world_size;


		vec3 source_displacement =
			textureLod(
				u_fft_displacement,
				vec3(
					fft_uv,
					float(cascade)),
				0.0
			).xyz;


		displacement +=
			source_displacement;
	}


	imageStore(
		u_animated_wave_field,
		ivec3(id),
		vec4(
			displacement,
			0.0));
}
