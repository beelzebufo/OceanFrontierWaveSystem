// Shared water-interface optics for the production ocean surface.
//
// Keep this file limited to the dielectric boundary itself.
// Volume extinction/scattering remains in the surface/compositor consumers.

float water_f0_from_ior(
    float ior)
{
    float ratio =
        (1.0 - ior) /
        (1.0 + ior);


    return
        ratio *
        ratio;
}


float water_fresnel_schlick(
    float f0,
    float ndotv)
{
    return
        f0 +
        (1.0 - f0) *
        pow(
            1.0 - ndotv,
            5.0);
}


// Exact dielectric Fresnel for a ray travelling from water into air.
//
// Unlike the above-water Schlick approximation, this explicitly handles
// the critical angle and total internal reflection.
float water_fresnel_water_to_air(
    float water_ior,
    float ndotv)
{
    float eta_i =
        max(
            water_ior,
            1.0001);


    const float eta_t =
        1.0;


    float cos_i =
        clamp(
            ndotv,
            0.0,
            1.0);


    float eta =
        eta_i /
        eta_t;


    float sin_t_squared =
        eta *
        eta *
        max(
            0.0,
            1.0 -
            cos_i *
            cos_i);


    if (sin_t_squared >= 1.0)
    {
        return 1.0;
    }


    float cos_t =
        sqrt(
            max(
                0.0,
                1.0 -
                sin_t_squared));


    float rs_numerator =
        eta_i *
            cos_i -
        eta_t *
            cos_t;


    float rs_denominator =
        eta_i *
            cos_i +
        eta_t *
            cos_t;


    float rp_numerator =
        eta_t *
            cos_i -
        eta_i *
            cos_t;


    float rp_denominator =
        eta_t *
            cos_i +
        eta_i *
            cos_t;


    float rs =
        rs_numerator /
        max(
            abs(
                rs_denominator),
            0.000001);


    float rp =
        rp_numerator /
        max(
            abs(
                rp_denominator),
            0.000001);


    return
        clamp(
            0.5 *
            (
                rs *
                    rs +
                rp *
                    rp
            ),
            0.0,
            1.0);
}


// Direction-aware Fresnel used by the water surface transmission path.
//
// Air -> water keeps the existing Schlick approximation.
// Water -> air uses the exact dielectric solution so the critical angle
// and total internal reflection are represented explicitly.
float water_interface_fresnel(
    float water_ior,
    float ndotv,
    bool viewing_from_underwater)
{
    if (viewing_from_underwater)
    {
        return
            water_fresnel_water_to_air(
                water_ior,
                ndotv);
    }


    return
        water_fresnel_schlick(
            water_f0_from_ior(
                water_ior),
            ndotv);
}
