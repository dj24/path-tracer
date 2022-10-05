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
    bool front_face;

    void set_face_normal(Ray r, float3 outward_normal) {
        front_face = dot(r.direction, outward_normal) < 0;
        normal = front_face ? outward_normal : -outward_normal;
    }
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

struct Sphere {
    float3 center;
    float radius;
    Material material;

    bool hit(Ray r, float t_min, float t_max, out HitRecord rec) {
        float3 oc = r.origin - center;
        float a = length_squared(r.direction);
        float half_b = dot(oc, r.direction);
        float c = length_squared(oc) - radius*radius;
        float discriminant = half_b*half_b - a*c;

        if (discriminant < 0) {
            return false;
        }
        float sqrtd = sqrt(discriminant);

        float root = (-half_b - sqrtd) / a;
        if (root < t_min || t_max < root) {
            root = (-half_b + sqrtd) / a;
            if (root < t_min || t_max < root)
                return false;
        }

        rec.t = root;
        rec.p = r.at(rec.t);
        rec.normal = (rec.p - center) / radius;
        float3 outward_normal = (rec.p - center) / radius;
        rec.set_face_normal(r, outward_normal);
        
        return true;
    }
};

struct Triangle {
    float3 v0;
    float3 v1;
    float3 v2;
    Material material;

    float3 normal()
    {
        float3 n;
        float3 e1 = v1 - v0; 
        float3 e2 = v2 - v0;
        n.x = (e1.y * e2.z) - (e1.z * e2.y);
        n.y = (e1.z * e2.x) - (e1.x * e2.z);
        n.z = (e1.x * e2.y) - (e1.y * e2.x);
        return normalize(n);
    }
    
    bool hit(Ray r, float t_min, float t_max, out HitRecord rec)
    {
        float3 e1 = v1 - v0; 
        float3 e2 = v2 - v0; 
        float3 pvec = cross(r.direction,e2); 
        float det = dot(e1,pvec);
        
        if (det < t_min) return false; 

        float invDet = 1.0 / det; 
 
        float3 tvec = r.origin - v0; 
        float u = dot(tvec,pvec) * invDet; 
        if (u < 0 || u > 1) return false; 
 
        float3 qvec = cross(tvec,e1); 
        float v = dot(r.direction,qvec) * invDet; 
        if (v < 0 || u + v > 1) return false; 
 
        float t = dot(e2,qvec) * invDet; 

        rec.t = t;
        rec.p = r.at(t);
        rec.set_face_normal(r, normal());

        return true;
    }
};



