shader_type canvas_item;

render_mode unshaded;


// Raw FFT displacement:
//   R = horizontal X
//   G = height Y
//   B = horizontal Z
uniform sampler2DArray displacement_texture :
    filter_nearest,
    repeat_enable;


// Texture-array layer.
uniform float cascade_index = 0.0;


// Used only by grayscale mode.
uniform float gain = 0.001;


// 0 = displacement X
// 1 = height Y
// 2 = displacement Z
uniform int channel = 1;


// 0 = signed grayscale
// 1 = sign diagnostic
// 2 = logarithmic magnitude
// 3 = UV/material diagnostic
// 4 = derivative normal RGB
// 5 = derivative raw Jacobian classification
uniform int debug_mode = 3;


float select_channel(vec4 displacement)
{
    if (channel == 0)
    {
        return displacement.x;
    }

    if (channel == 2)
    {
        return displacement.z;
    }

    return displacement.y;
}


void fragment()
{
    vec4 result_color;


    // -----------------------------------------------------------------
    // Mode 3:
    // Proves that this canvas shader/material is actually executing.
    //
    // Expected:
    // horizontal red gradient,
    // vertical green gradient.
    // -----------------------------------------------------------------

    if (debug_mode == 3)
    {
        result_color = vec4(
            UV.x,
            UV.y,
            0.0,
            1.0
        );
    }
    else
    {
        vec4 displacement =
            texture(
                displacement_texture,
                vec3(
                    UV,
                    cascade_index
                )
            );


        float value =
            select_channel(
                displacement
            );


        // -------------------------------------------------------------
        // Modes 4/5 read AnimatedWaveDerivativeField directly.
        // This shader never recomputes derivatives.
        // -------------------------------------------------------------

        if (debug_mode == 4)
        {
            vec3 normal = displacement.xyz;

            if (any(isnan(normal)) || any(isinf(normal)))
            {
                result_color = vec4(
                    1.0,
                    0.0,
                    1.0,
                    1.0
                );
            }
            else
            {
                result_color = vec4(
                    normal * 0.5 + 0.5,
                    1.0
                );
            }
        }
        else if (debug_mode == 5)
        {
            float determinant = displacement.a;

            if (isnan(determinant) || isinf(determinant))
            {
                result_color = vec4(
                    1.0,
                    0.0,
                    1.0,
                    1.0
                );
            }
            else if (determinant < 0.0)
            {
                // Magenta = overlap/inversion.
                float severity =
                    clamp(
                        -determinant,
                        0.0,
                        1.0
                    );

                result_color = vec4(
                    1.0,
                    0.0,
                    0.5 + 0.5 * severity,
                    1.0
                );
            }
            else if (determinant < 1.0)
            {
                // Yellow -> red = increasing compression.
                float compression =
                    1.0 - determinant;

                result_color = vec4(
                    1.0,
                    1.0 - compression,
                    0.0,
                    1.0
                );
            }
            else
            {
                // Green -> cyan = stretch above neutral detJ=1.
                float stretch =
                    clamp(
                        determinant - 1.0,
                        0.0,
                        1.0
                    );

                result_color = vec4(
                    0.0,
                    1.0,
                    stretch,
                    1.0
                );
            }
        }


        // -------------------------------------------------------------
        // Invalid floating-point data.
        // Magenta = NaN / Infinity.
        // -------------------------------------------------------------

        else if (isnan(value) || isinf(value))
        {
            result_color = vec4(
                1.0,
                0.0,
                1.0,
                1.0
            );
        }

        // -------------------------------------------------------------
        // Mode 1:
        // Sign visualization.
        //
        // Red   = positive
        // Blue  = negative
        // Green = approximately zero
        // -------------------------------------------------------------

        else if (debug_mode == 1)
        {
            const float epsilon =
                0.000001;


            if (value > epsilon)
            {
                result_color = vec4(
                    1.0,
                    0.0,
                    0.0,
                    1.0
                );
            }
            else if (value < -epsilon)
            {
                result_color = vec4(
                    0.0,
                    0.0,
                    1.0,
                    1.0
                );
            }
            else
            {
                result_color = vec4(
                    0.0,
                    1.0,
                    0.0,
                    1.0
                );
            }
        }

        // -------------------------------------------------------------
        // Mode 2:
        // Magnitude visualization.
        //
        // Log scale so unexpectedly large unnormalized FFT values
        // remain inspectable.
        // -------------------------------------------------------------

        else if (debug_mode == 2)
        {
            float magnitude =
                log2(
                    1.0 +
                    abs(value)
                );


            float visible =
                clamp(
                    magnitude / 16.0,
                    0.0,
                    1.0
                );


            result_color = vec4(
                vec3(visible),
                1.0
            );
        }

        // -------------------------------------------------------------
        // Mode 0:
        // Signed grayscale.
        //
        // 0.5 = zero
        // darker = negative
        // brighter = positive
        // -------------------------------------------------------------

        else
        {
            float scaled =
                value * gain;


            float signed_value =
                scaled /
                (
                    1.0 +
                    abs(scaled)
                );


            float grayscale =
                0.5 +
                0.5 * signed_value;


            result_color = vec4(
                vec3(grayscale),
                1.0
            );
        }
    }


    // Godot fragment processor:
    // no early return.
    COLOR = result_color;
}
