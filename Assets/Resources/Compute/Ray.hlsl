#include "Util.hlsl"

struct Ray
{
    float3 origin;
    float3 direction;

    float3 at(float t)
    {
        return origin + t*direction;
    }
};

struct HitRecord {
    float3 p;
    float3 normal;
    float t;
};

struct Material {
    float3 albedo;
    bool isMetal;
    float fuzz;

    Ray direct_light(float2 co, HitRecord rec) {
        Ray scattered;
        scattered.origin = rec.p; 
        if(isMetal)
        {
            scattered.origin = rec.p;
            scattered.direction = _WorldSpaceLightPos0.xyz + fuzz * random_in_unit_sphere(co);
        } else
        {
            scattered.direction = _WorldSpaceLightPos0.xyz + random_in_unit_sphere(co);
            if (near_zero(scattered.direction))
            {
                scattered.direction = rec.normal;
            }
        }
        return scattered;
    }
    
    Ray scatter(float2 co, Ray r_in, HitRecord rec,out float3 attenuation) {
        Ray scattered;
        scattered.origin = rec.p;
        attenuation = albedo;
        if(isMetal)
        {
            float3 reflected = reflect(unit_vector(r_in.direction), rec.normal);
            scattered.direction = reflected + fuzz * random_in_unit_sphere(co);
        }
        else
        {
            scattered.direction = rec.normal + random_in_unit_sphere(co);
            if (near_zero(scattered.direction))
            {
                scattered.direction = rec.normal;
            }
        }
        return scattered;
    }
};

struct Triangle {
    float3 v0, v1, v2;
    float3 n0, n1, n2;

    float3 get_normal(float3 uvw)
    {
        float3 n;
        float3 e1 = v1 - v0; 
        float3 e2 = v2 - v0;
        n.x = (e1.y * e2.z) - (e1.z * e2.y);
        n.y = (e1.z * e2.x) - (e1.x * e2.z);
        n.z = (e1.x * e2.y) - (e1.y * e2.x);
        return normalize(n);
    }
    
    bool hit(Ray r, out HitRecord rec, out float3 uvw)
    {
        float3 e1 = v1 - v0;  
        float3 e2 = v2 - v0; 
        float3 pvec = cross(r.direction,e2); 
        float det = dot(e1,pvec);
        
        if (det < 0.00001) return false; 

        float invDet = 1.0 / det; 
 
        float3 tvec = r.origin - v0; 
        float u = dot(tvec,pvec) * invDet; 
        if (u < 0 || u > 1) return false; 
 
        float3 qvec = cross(tvec,e1); 
        float v = dot(r.direction,qvec) * invDet; 
        if (v < 0 || u + v > 1) return false; 
 
        float t = dot(e2,qvec) * invDet; 

        float w = 1-u-v;
        rec.t = t; 
        rec.p = r.at(t);
       
        const float3 normal = n1 * u + n2 * v + n0 * w;
        rec.normal = normal;
        uvw = normal; 
        
        return t > 0;
    }
};

struct Camera {
    float3 lower_left_corner;
    float3 horizontal;
    float3 vertical;
    
    void setup(uint Width, uint Height, float VerticalFov, float3 CameraDirection) {
        float3 vup = float3(0,1,0);
        const float aspect_ratio = float(Width) / float(Height);
        float theta = degrees_to_radians(VerticalFov);
        float h = tan(theta/2.0);
        float viewport_height = 2.0 * h;
        float viewport_width = aspect_ratio * viewport_height;
        
        float3 w = unit_vector(CameraDirection);
        float3 u = unit_vector(cross(vup, w));
        float3 v = cross(w, u);
        
        horizontal = -viewport_width * u;
        vertical = viewport_height * v;
        lower_left_corner = _WorldSpaceCameraPos - horizontal/2 - vertical/2 - w;
    }

    Ray get_ray(float u, float v) {
        float3 direction = lower_left_corner + u*horizontal + v*vertical - _WorldSpaceCameraPos;
        Ray ray = {
            _WorldSpaceCameraPos.x, _WorldSpaceCameraPos.y, _WorldSpaceCameraPos.z,
            direction.x, direction.y, direction.z
        };
        return ray;
    }
};



