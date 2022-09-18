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
