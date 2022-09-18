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


struct Sphere {
    float3 center;
    float radius;

    bool hit (Ray r, float t_min, float t_max, HitRecord rec) {
        float3 oc = r.origin - center;
        float a = length_squared(r.direction);
        float half_b = dot(oc, r.direction);
        float c = length_squared(oc) - radius*radius;

        float discriminant = half_b*half_b - a*c;
        if (discriminant < 0) return false;
        float sqrtd = sqrt(discriminant);

        // Find the nearest root that lies in the acceptable range.
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

    float hit_sphere(Ray r) {
        float3 oc = r.origin - center;
        float a = dot(r.direction, r.direction);
        float b = 2.0 * dot(oc, r.direction);
        float c = dot(oc, oc) - radius*radius;
        float discriminant = b*b - 4*a*c;

        if (discriminant < 0) {
            return -1.0;
        }
        return (-b - sqrt(discriminant) ) / (2.0*a);
    }
};

struct SphereList{
    Sphere spheres[2];

    bool hit(Ray r, float t_min, float t_max, inout HitRecord rec) {
        HitRecord temp_rec;
        bool hit_anything = false; 
        float closest_so_far = t_max;

        for (int i = 0; i < 2; i++) {
            if (spheres[i].hit(r, t_min, closest_so_far, temp_rec)) {
                hit_anything = true;
                closest_so_far = temp_rec.t;
                rec = temp_rec;
            }
        }

        return hit_anything;
    }
};


