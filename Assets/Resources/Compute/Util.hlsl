#include <UnityShaderVariables.cginc>
const float infinity = 99999999;

float degrees_to_radians(float degrees) {
    return degrees * (3.1415926535897932385 / 180.0);
}

float3 unit_vector(float3 v) {
    return v / length(v);  
}

float length_squared(float3 v) {
    return v[0]*v[0] + v[1]*v[1] + v[2]*v[2];  
}

float random(float2 co){
    float2 time_seed = float2(co.x + _SinTime.x, co.y + _SinTime.x) / 2000.0;
    // time_seed = co;
    return frac(sin(dot(time_seed ,float2(12.9898,78.233))) * 43758.5453);
}

float random(float2 co, float min, float max) {
    // Returns a random real in [min,max). 
    return min + (max-min)*random(co);
}

float3 random_float3(float2 co){
    return float3(random(co), random(co * 2), random(co * 3));
}

float3 random_float3(float2 co, float min, float max) {
    return float3(random(co, min,max), random(co * 2,min,max), random(co * 3,min,max));
}

float3 random_in_unit_sphere(float2 co) {
    float3 p = random_float3(co,-1,1);
    while (length_squared(p) < 1) {
        p = random_float3(co,-1,1);
    }
    return p;
}

float3 reflect(float3 v, float3 n) {
    return v - 2*dot(v,n)*n;
}

float3 random_unit_vector(float2 co) {
    return unit_vector(random_in_unit_sphere(co));
}

bool near_zero(float3 e) {
    // Return true if the vector is close to zero in all dimensions.
    const float s = 1e-8;
    return (abs(e[0]) < s) && (abs(e[1]) < s) && (abs(e[2]) < s);
}

float3 random_in_hemisphere(float2 co, float3 normal) {
    float3 in_unit_sphere = random_in_unit_sphere(co);
    if (dot(in_unit_sphere, normal) > 0.0) // In the same hemisphere as the normal
        return in_unit_sphere;
    return -in_unit_sphere;
}

float4 write_color(float4 pixel_color, int samples_per_pixel) {
    float r = pixel_color.x;
    float g = pixel_color.y;
    float b = pixel_color.z;
    float a = pixel_color.w;
    // Divide the color by the number of samples.
    float scale = 1.0 / samples_per_pixel;
    r *= scale;
    g *= scale;
    b *= scale;
    
    return float4(r,g,b,a);
}

uint pack_2_bytes(uint2 vals) {
    return vals.x + (vals.y << 8);
}

uint pack_4_bytes(uint4 vals) {
    return vals.x + (vals.y << 8) + (vals.z << 16) + (vals.w << 24);
}

uint load_uint16(ByteAddressBuffer buffer, uint offset)
{
    return asuint(buffer.Load(offset));
}

uint PackUint2x16(uint2 vals) {
    return vals.x + (vals.y << 16);
}

uint2 UnpackUint2x16(uint packed_values) {
    uint y = (packed_values >> 16) & 0xffffu;
    uint x = packed_values & 0xffffu;

    return uint2(x, y);
}
