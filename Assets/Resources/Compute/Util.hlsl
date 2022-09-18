#include <UnityShaderVariables.cginc>
const float infinity = 99999999;
const float pi = 3.1415926535897932385;

double degrees_to_radians(float degrees) {
    return degrees * pi / 180.0;
}

float3 unit_vector(float3 v) {
    return v / length(v);  
}

float length_squared(float3 v) {
    return v[0]*v[0] + v[1]*v[1] + v[2]*v[2];  
}

float random_float(){
    float2 co = _Time;
    return frac(sin(dot(co.xy ,float2(12.9898,78.233))) * 43758.5453);
}

float random_float(float min, float max) {
    // Returns a random real in [min,max). 
    return min + (max-min)*random_float();
}

float3 write_color(float3 pixel_color, int samples_per_pixel) {
    float r = pixel_color.x;
    float g = pixel_color.y;
    float b = pixel_color.z;
    // Divide the color by the number of samples.
    float scale = 1.0 / samples_per_pixel;
    r *= scale;
    g *= scale;
    b *= scale;
    
    return float3(r,g,b);
}
