#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;


// -----------------------------------------------------------------------------
// Resources
// -----------------------------------------------------------------------------

// RG = H0(k)
// BA = H0(-k)
layout(
	rgba32f,
	set = 0,
	binding = 0
)
uniform readonly image2DArray spectrum_initial;


// Complex H(k, t)
layout(
	rg32f,
	set = 0,
	binding = 1
)
uniform writeonly image2DArray spectrum_height;


// Complex horizontal X displacement spectrum.
layout(
	rg32f,
	set = 0,
	binding = 2
)
uniform writeonly image2DArray spectrum_displace_x;


// Complex horizontal Z displacement spectrum.
layout(
	rg32f,
	set = 0,
	binding = 3
)
uniform writeonly image2DArray spectrum_displace_z;


// -----------------------------------------------------------------------------
// Push constants
// -----------------------------------------------------------------------------

layout(push_constant, std430) uniform Params
{
	// x = resolution
	// y = cascade count
	// z/w = reserved
	uvec4 dimensions;

	// x = simulation time [s]
	// y = chop
	// z = gravity
	// w = loop period [s], 0 = disabled
	vec4 simulation;
}
params;


// -----------------------------------------------------------------------------
// Constants
// -----------------------------------------------------------------------------

const float TWO_PI =
	6.28318530717958647692;


// -----------------------------------------------------------------------------
// Complex math
// -----------------------------------------------------------------------------

vec2 complex_multiply(
	vec2 a,
	vec2 b)
{
	return vec2(
		a.x * b.x - a.y * b.y,
		a.x * b.y + a.y * b.x);
}


// -----------------------------------------------------------------------------
// Dispersion
// -----------------------------------------------------------------------------

float deep_dispersion(
	float k,
	float gravity,
	float loop_period)
{
	float omega =
		sqrt(abs(gravity * k));

	if (loop_period > 0.0)
	{
		float natural_period =
			TWO_PI / omega;

		float loops =
			ceil(loop_period / natural_period);

		float quantized_period =
			loop_period / loops;

		omega =
			TWO_PI / quantized_period;
	}

	return omega;
}


// -----------------------------------------------------------------------------
// Kernel
// -----------------------------------------------------------------------------

void main()
{
	uvec3 id =
		gl_GlobalInvocationID;

	uint resolution =
		params.dimensions.x;

	uint cascade_count =
		params.dimensions.y;

	if (id.x >= resolution ||
		id.y >= resolution ||
		id.z >= cascade_count)
	{
		return;
	}


	ivec2 center =
		ivec2(int(resolution / 2u));

	ivec2 coord =
		ivec2(id.xy) - center;


	// Explicit DC handling.
	//
	// SpectrumInitial is already zero here, but this prevents a
	// division-by-zero path when temporal looping is enabled.
	if (coord.x == 0 &&
		coord.y == 0)
	{
		ivec3 position =
			ivec3(id);

		imageStore(
			spectrum_height,
			position,
			vec4(0.0));

		imageStore(
			spectrum_displace_x,
			position,
			vec4(0.0));

		imageStore(
			spectrum_displace_z,
			position,
			vec4(0.0));

		return;
	}


	// Same spatial scale convention as SpectrumInit.
	float world_size =
		0.5 * exp2(float(id.z));


	vec2 k =
		TWO_PI *
		vec2(coord) /
		world_size;


	float k_magnitude =
		length(k);


	float omega =
		deep_dispersion(
			k_magnitude,
			params.simulation.z,
			params.simulation.w);


	float phase =
		omega *
		params.simulation.x;


	float sin_phase =
		sin(phase);

	float cos_phase =
		cos(phase);


	// exp(-iwt)
	vec2 forward_rotation =
		vec2(
			cos_phase,
			-sin_phase);


	// exp(+iwt)
	vec2 backward_rotation =
		vec2(
			cos_phase,
			sin_phase);


	vec4 h0 =
		imageLoad(
			spectrum_initial,
			ivec3(id));


	vec2 h =
		complex_multiply(
			h0.xy,
			forward_rotation)
		+
		complex_multiply(
			h0.zw,
			backward_rotation);


	imageStore(
		spectrum_height,
		ivec3(id),
		vec4(h, 0.0, 0.0));


	float inverse_k =
		1.0 /
		(k_magnitude + 0.00001);


	float chop =
		params.simulation.y;


	// Equivalent to Crest:
	//
	// chop * (-h.y * k.x, h.x * k.x) / |k|
	vec2 displacement_x =
		chop *
		vec2(
			-h.y * k.x,
			 h.x * k.x)
		*
		inverse_k;


	vec2 displacement_z =
		chop *
		vec2(
			-h.y * k.y,
			 h.x * k.y)
		*
		inverse_k;


	imageStore(
		spectrum_displace_x,
		ivec3(id),
		vec4(displacement_x, 0.0, 0.0));


	imageStore(
		spectrum_displace_z,
		ivec3(id),
		vec4(displacement_z, 0.0, 0.0));
}
