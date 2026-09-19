#[compute]
#version 450

// GPU inverse FFT for the three ocean displacement spectra.
//
// Crest reference:
// crest/Assets/Crest/Crest/Shaders/Resources/FFT/FFTCompute.compute
//
// Important:
// - X axis first.
// - Y axis second.
// - Input conjugation only on X pass.
// - Bit reversal occurs during butterfly pass 0.
// - No explicit 1/N^2 normalization.
// - Checkerboard sign is applied only on final Y pass.

#define MAX_FFT_SIZE 512u
#define LOCAL_SIZE 128u

layout(
	local_size_x = 128,
	local_size_y = 1,
	local_size_z = 1
) in;


// -----------------------------------------------------------------------------
// Inputs
// -----------------------------------------------------------------------------

layout(set = 0, binding = 0)
uniform sampler2DArray input_h;

layout(set = 0, binding = 1)
uniform sampler2DArray input_x;

layout(set = 0, binding = 2)
uniform sampler2DArray input_z;

layout(set = 0, binding = 3)
uniform sampler2D butterfly_texture;


// -----------------------------------------------------------------------------
// Intermediate outputs
// -----------------------------------------------------------------------------

layout(
	rg32f,
	set = 0,
	binding = 4
)
uniform writeonly image2DArray output_h;

layout(
	rg32f,
	set = 0,
	binding = 5
)
uniform writeonly image2DArray output_x;

layout(
	rg32f,
	set = 0,
	binding = 6
)
uniform writeonly image2DArray output_z;


// -----------------------------------------------------------------------------
// Final output
// -----------------------------------------------------------------------------

layout(
	rgba16f,
	set = 0,
	binding = 7
)
uniform writeonly image2DArray output_displacement;


// -----------------------------------------------------------------------------
// Push constants
// -----------------------------------------------------------------------------

layout(push_constant, std430) uniform Params
{
	// x = resolution
	// y = cascade count
	// z = FFT pass count = log2(resolution)
	// w = axis:
	//       0 = X / first pass
	//       1 = Y / final pass
	uvec4 config;
}
params;


// -----------------------------------------------------------------------------
// Shared memory
//
// Maximum:
// 6 arrays * 512 vec2 * 8 bytes = 24576 bytes.
// -----------------------------------------------------------------------------

shared vec2 intermediate_h[512];
shared vec2 scratch_h[512];

shared vec2 intermediate_x[512];
shared vec2 scratch_x[512];

shared vec2 intermediate_z[512];
shared vec2 scratch_z[512];


// -----------------------------------------------------------------------------
// Helpers
// -----------------------------------------------------------------------------

vec2 complex_multiply(
	vec2 a,
	vec2 b)
{
	return vec2(
		a.x * b.x - a.y * b.y,
		a.x * b.y + a.y * b.x);
}


vec2 complex_conjugate(vec2 value)
{
	return vec2(
		value.x,
		-value.y);
}


// Manual version retained so we do not depend on backend-specific handling
// of bitfieldReverse().
uint reverse_bits(uint x)
{
	x =
		((x >> 1u) & 0x55555555u) |
		((x & 0x55555555u) << 1u);

	x =
		((x >> 2u) & 0x33333333u) |
		((x & 0x33333333u) << 2u);

	x =
		((x >> 4u) & 0x0f0f0f0fu) |
		((x & 0x0f0f0f0fu) << 4u);

	x =
		((x >> 8u) & 0x00ff00ffu) |
		((x & 0x00ff00ffu) << 8u);

	x =
		((x >> 16u) & 0x0000ffffu) |
		((x & 0x0000ffffu) << 16u);

	return x;
}


ivec3 texture_coordinate(
	uint fft_coord,
	uint line,
	uint cascade,
	uint axis)
{
	if (axis == 0u)
	{
		// Transform X.
		return ivec3(
			int(fft_coord),
			int(line),
			int(cascade));
	}

	// Transform Y.
	return ivec3(
		int(line),
		int(fft_coord),
		int(cascade));
}


void butterfly_indices(
	uint coord,
	uint pass_index,
	uint pass_count,
	out uint index_a,
	out uint index_b)
{
	uint offset =
		1u << pass_index;

	if (((coord / offset) & 1u) == 1u)
	{
		index_a =
			coord - offset;

		index_b =
			coord;
	}
	else
	{
		index_a =
			coord;

		index_b =
			coord + offset;
	}

	// Crest performs bit reversal only for the first butterfly pass.
	if (pass_index == 0u)
	{
		uint shift =
			32u - pass_count;

		index_a =
			reverse_bits(index_a) >> shift;

		index_b =
			reverse_bits(index_b) >> shift;
	}
}


// -----------------------------------------------------------------------------
// Kernel
// -----------------------------------------------------------------------------

void main()
{
	uint resolution =
		params.config.x;

	uint cascade_count =
		params.config.y;

	uint pass_count =
		params.config.z;

	uint axis =
		params.config.w;


	uint line =
		gl_WorkGroupID.x;

	uint cascade =
		gl_WorkGroupID.z;

	uint local_index =
		gl_LocalInvocationID.x;


	if (resolution == 0u ||
		resolution > MAX_FFT_SIZE ||
		line >= resolution ||
		cascade >= cascade_count)
	{
		return;
	}


	// -------------------------------------------------------------------------
	// Load one complete row/column into workgroup shared memory.
	//
	// LOCAL_SIZE is 128, so:
	//
	// N = 128 -> each invocation loads 1 value.
	// N = 256 -> each invocation loads 2 values.
	// N = 512 -> each invocation loads 4 values.
	// -------------------------------------------------------------------------

	for (
		uint coord = local_index;
		coord < resolution;
		coord += LOCAL_SIZE)
	{
		ivec3 source_coord =
			texture_coordinate(
				coord,
				line,
				cascade,
				axis);

		vec2 h =
			texelFetch(
				input_h,
				source_coord,
				0).rg;

		vec2 x =
			texelFetch(
				input_x,
				source_coord,
				0).rg;

		vec2 z =
			texelFetch(
				input_z,
				source_coord,
				0).rg;


		// Crest conjugates the spectrum only before the first dimension.
		if (axis == 0u)
		{
			h = complex_conjugate(h);
			x = complex_conjugate(x);
			z = complex_conjugate(z);
		}


		intermediate_h[coord] = h;
		intermediate_x[coord] = x;
		intermediate_z[coord] = z;
	}


	// -------------------------------------------------------------------------
	// Butterfly stages.
	// -------------------------------------------------------------------------

	for (
		uint pass_index = 0u;
		pass_index < pass_count;
		++pass_index)
	{
		// Wait for initial load / previous butterfly pass.
		barrier();


		for (
			uint coord = local_index;
			coord < resolution;
			coord += LOCAL_SIZE)
		{
			uint index_a;
			uint index_b;

			butterfly_indices(
				coord,
				pass_index,
				pass_count,
				index_a,
				index_b);


			bool read_intermediate =
				(pass_index & 1u) == 0u;


			vec2 value_a_h;
			vec2 value_b_h;

			vec2 value_a_x;
			vec2 value_b_x;

			vec2 value_a_z;
			vec2 value_b_z;


			if (read_intermediate)
			{
				value_a_h =
					intermediate_h[index_a];

				value_b_h =
					intermediate_h[index_b];

				value_a_x =
					intermediate_x[index_a];

				value_b_x =
					intermediate_x[index_b];

				value_a_z =
					intermediate_z[index_a];

				value_b_z =
					intermediate_z[index_b];
			}
			else
			{
				value_a_h =
					scratch_h[index_a];

				value_b_h =
					scratch_h[index_b];

				value_a_x =
					scratch_x[index_a];

				value_b_x =
					scratch_x[index_b];

				value_a_z =
					scratch_z[index_a];

				value_b_z =
					scratch_z[index_b];
			}


			vec2 weight =
				texelFetch(
					butterfly_texture,
					ivec2(
						int(coord),
						int(pass_index)),
					0).rg;


			vec2 result_h =
				value_a_h +
				complex_multiply(
					weight,
					value_b_h);

			vec2 result_x =
				value_a_x +
				complex_multiply(
					weight,
					value_b_x);

			vec2 result_z =
				value_a_z +
				complex_multiply(
					weight,
					value_b_z);


			if (read_intermediate)
			{
				scratch_h[coord] =
					result_h;

				scratch_x[coord] =
					result_x;

				scratch_z[coord] =
					result_z;
			}
			else
			{
				intermediate_h[coord] =
					result_h;

				intermediate_x[coord] =
					result_x;

				intermediate_z[coord] =
					result_z;
			}
		}
	}


	// Ensure final butterfly writes are visible before output.
	barrier();


	bool result_in_intermediate =
		(pass_count & 1u) == 0u;


	// -------------------------------------------------------------------------
	// Store result.
	// -------------------------------------------------------------------------

	for (
		uint coord = local_index;
		coord < resolution;
		coord += LOCAL_SIZE)
	{
		vec2 result_h =
			result_in_intermediate
				? intermediate_h[coord]
				: scratch_h[coord];

		vec2 result_x =
			result_in_intermediate
				? intermediate_x[coord]
				: scratch_x[coord];

		vec2 result_z =
			result_in_intermediate
				? intermediate_z[coord]
				: scratch_z[coord];


		ivec3 destination_coord =
			texture_coordinate(
				coord,
				line,
				cascade,
				axis);


		if (axis == 0u)
		{
			// First FFT dimension -> keep complex results.
			imageStore(
				output_h,
				destination_coord,
				vec4(result_h, 0.0, 0.0));

			imageStore(
				output_x,
				destination_coord,
				vec4(result_x, 0.0, 0.0));

			imageStore(
				output_z,
				destination_coord,
				vec4(result_z, 0.0, 0.0));
		}
		else
		{
			// Final dimension.
			//
			// Spectrum was generated around the center of the texture.
			// Undo that shift with (-1)^(x+y).
			float sign =
				(
					(
						destination_coord.x +
						destination_coord.y
					) & 1
				) != 0
					? -1.0
					: 1.0;


			// Deliberately NO 1/N^2 normalization here.
			//
			// This matches the Crest FFT convention and keeps amplitude
			// calibration comparable during validation.
			vec3 displacement =
				sign *
				vec3(
					result_x.x,
					result_h.x,
					result_z.x);


			imageStore(
				output_displacement,
				destination_coord,
				vec4(
					displacement,
					0.0));
		}
	}
}
