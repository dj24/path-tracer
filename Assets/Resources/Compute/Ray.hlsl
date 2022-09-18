struct Ray
{
    float3 origin;
    float3 direction;

    float3 at(float t)
    {
        return origin + t*direction;
    }
};

float3 unit_vector(float3 v) {
    return v / length(v);  
}

float length_squared(float3 v) {
    return v[0]*v[0] + v[1]*v[1] + v[2]*v[2]; 
}

float hit_sphere(float3 center, float radius, Ray r) {
    float3 oc = r.origin - center;
    float a = length_squared(r.direction);
    float half_b = dot(oc, r.direction);
    float3 c = length_squared(oc) - radius*radius;
    float discriminant = half_b*half_b - a*c;
    if (discriminant < 0.0) {
        return -1.0;
    }
    return (-half_b - sqrt(discriminant) ) / a;
}

float3 ray_color(Ray r) {
    float t = hit_sphere(float3(0,0,-1), 0.5, r);
    if (t > 0.0) { 
        float3 N = unit_vector(r.at(t) - float3(0,0,-1));
        return 0.5*float3(N.x+1, N.y+1, N.z+1);
    }
    float3 unit_direction = unit_vector(r.direction);
    t = 0.5*(unit_direction.y + 1.0);
    return (1.0-t)*float3(1.0, 1.0, 1.0) + t*float3(0.5, 0.7, 1.0);
}

