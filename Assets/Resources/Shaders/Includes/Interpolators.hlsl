#define PI    3.1415926535897932384626433
#define FLOAT_PI  1.5707963267948966192313216916398;
#define PI_SQ 9.8696044010893586188344910

Texture2D _PathTraceTexture;
float _PathTraceDownscaleFactor;

float4 NNSample(uint2 id, int x, int y)
{
    uint2 p0 = id;
    uint2 p1 = id + int2(0,1);
    uint2 p2 = id + int2(1,1);
    uint2 p3 = id + int2(1,0);
    
    float4 s0 = _PathTraceTexture[p0];
    float4 s1 = _PathTraceTexture[p1];
    float4 s2 = _PathTraceTexture[p2];
    float4 s3 = _PathTraceTexture[p3];
    
    return (s0 + s1 + s2 + s3) * 0.25;
}

float4 CubicHermite (float4 A, float4 B, float4 C, float4 D, float t)
{
    float t2 = t*t;
    float t3 = t*t*t;
    float4 a = -A/2.0 + (3.0*B)/2.0 - (3.0*C)/2.0 + D/2.0;
    float4 b = A - (5.0*B)/2.0 + 2.0*C - D / 2.0;
    float4 c = -A/2.0 + C/2.0;
    float4 d = B;
    
    return a*t3 + b*t2 + c*t + d;
}

SamplerState point_clamp_sampler;
SamplerState linear_clamp_sampler;

float4 BicubicHermiteSample(float2 uv)
{
    float2 fract = frac(uv * (_ScreenParams.xy / _PathTraceDownscaleFactor));
    float x = fract.x * _PathTraceDownscaleFactor;
    float y = fract.y * _PathTraceDownscaleFactor;;
    uint2 id = uv * _ScreenParams.xy / _PathTraceDownscaleFactor;
    
    float tx = x / _PathTraceDownscaleFactor;
    float ty = y / _PathTraceDownscaleFactor;
    
    float4 c00 = _PathTraceTexture[id + int2(-1,-1)];
    float4 c10 = _PathTraceTexture[id + int2(0,-1)];
    float4 c20 = _PathTraceTexture[id + int2(1,-1)];
    float4 c30 = _PathTraceTexture[id + int2(2,-1)];

    float4 c01 = _PathTraceTexture[id + int2(-1,0)];
    float4 c11 = _PathTraceTexture[id + int2(0,0)];
    float4 c21 = _PathTraceTexture[id + int2(1,0)];
    float4 c31 = _PathTraceTexture[id + int2(2,0)];

    float4 c02 = _PathTraceTexture[id + int2(-1,1)];
    float4 c12 = _PathTraceTexture[id + int2(0,1)];
    float4 c22 = _PathTraceTexture[id + int2(1,1)];
    float4 c32 = _PathTraceTexture[id + int2(2,1)];

    float4 c03 = _PathTraceTexture[id + int2(-1,2)];
    float4 c13 = _PathTraceTexture[id + int2(0,2)];
    float4 c23 = _PathTraceTexture[id + int2(1,2)];
    float4 c33 = _PathTraceTexture[id + int2(2,2)];

    float4 CP0X = CubicHermite(c00, c10, c20, c30, tx);
    float4 CP1X = CubicHermite(c01, c11, c21, c31, tx);
    float4 CP2X = CubicHermite(c02, c12, c22, c32, tx);
    float4 CP3X = CubicHermite(c03, c13, c23, c33, tx);
    
    return CubicHermite(CP0X, CP1X, CP2X, CP3X, ty);
}

float4 BilinearSample(float2 uv)
{
    uint2 id = uv * _ScreenParams.xy / _PathTraceDownscaleFactor;
    
    float4 c00 = _PathTraceTexture[id];
    float4 c01 = _PathTraceTexture[id + int2(0,1)];
    float4 c10 = _PathTraceTexture[id + int2(1,0)];
    float4 c11 = _PathTraceTexture[id + int2(1,1)];

    float2 t = frac(uv * (_ScreenParams.xy / _PathTraceDownscaleFactor));

    return lerp(lerp(c00, c10, t.x), lerp(c01, c11, t.x), t.y);
}

float4 lanczos(float4 x)
{
    float4 res;
    // res = (x==float4(0.0, 0.0, 0.0, 0.0)) ? PI*(PI * 0.5)  :  sin(x*(PI * 0.5))*sin(x*PI)/(x*x);
 
    // res = (x==float4(0.0, 0.0, 0.0, 0.0)) ?  1.0  :  (2/(PI*PI))*sin(x*(PI * 0.5))*sin(x*PI)/(x*x);
 
    // res = (x==float4(0.0, 0.0, 0.0, 0.0)) ?  0.5  :  PI*cos(x*PI)*PI*cos(x*PI*(1.22/2.233));
 
    // res = (x==float4(0.0, 0.0, 0.0, 0.0)) ?  0.5  :  2*(0.5-x*x/16.0+x*x*x*x/384.0)*sin(PI*x)/(PI*x);
 
    // res = (x==float4(0.0, 0.0, 0.0, 0.0)) ?  1.0  :  cos(x/2)*sin(x*PI)/(2*PI*x);
 
    res = x == float4(0.0, 0.0, 0.0, 0.0) ?  1.0  :  cos(x / PI * 0.5) * sin(x*PI) / (PI*x);
 
    return res;
}



float4 GaussianBlur(float2 uv)
{
    float3x3 gaussianKernel = {
        0.075, 0.124, 0.075,
        0.124, 0.204, 0.124,
        0.075, 0.124, 0.075
    };
    
    const float dx = _PathTraceDownscaleFactor / _ScreenParams.x;
    const float dy = _PathTraceDownscaleFactor / _ScreenParams.y;
    
    float4 col = 0;
    sampler s = point_clamp_sampler;
    col += _PathTraceTexture.Sample(s, uv) * gaussianKernel._m11;;
    col += _PathTraceTexture.Sample(s, uv + float2(-dx, -dy)) * gaussianKernel._m00;
    col += _PathTraceTexture.Sample(s, uv + float2(-dx, 0)) * gaussianKernel._m01;
    col += _PathTraceTexture.Sample(s, uv + float2(-dx, dy)) * gaussianKernel._m02;
    col += _PathTraceTexture.Sample(s, uv + float2(0, dy)) * gaussianKernel._m12;
    col += _PathTraceTexture.Sample(s, uv + float2(dx, dy)) * gaussianKernel._m22;
    col += _PathTraceTexture.Sample(s, uv + float2(dx, 0)) * gaussianKernel._m21;
    col += _PathTraceTexture.Sample(s, uv + float2(dx, -dy)) * gaussianKernel._m20;
    return col;
}


float4 LanczosSample(float2 uv) {
    float3 color;
    float4x4 weights;
 
    float2 dx = float2(1.0, 0.0);
    float2 dy = float2(0.0, 1.0);

    float2 texture_size = _ScreenParams.xy / _PathTraceDownscaleFactor;
    
    float2 pc = uv*texture_size;
 
    float2 tc = floor(pc-float2(0.5,0.5))+float2(0.5,0.5); 
 
    weights[0] = lanczos(float4(distance(pc, tc    -dx    -dy), distance(pc, tc           -dy), distance(pc, tc    +dx    -dy), distance(pc, tc+2.0*dx    -dy)));
    weights[1] = lanczos(float4(distance(pc, tc    -dx       ), distance(pc, tc              ), distance(pc, tc    +dx       ), distance(pc, tc+2.0*dx       )));
    weights[2] = lanczos(float4(distance(pc, tc    -dx    +dy), distance(pc, tc           +dy), distance(pc, tc    +dx    +dy), distance(pc, tc+2.0*dx    +dy)));
    weights[3] = lanczos(float4(distance(pc, tc    -dx+2.0*dy), distance(pc, tc       +2.0*dy), distance(pc, tc    +dx+2.0*dy), distance(pc, tc+2.0*dx+2.0*dy)));
 
    dx = dx/texture_size;
    dy = dy/texture_size;
    tc = tc/texture_size;
 
    // reading the texels
    sampler s = point_clamp_sampler;
    float3 c00 = _PathTraceTexture.Sample(s, tc    -dx    -dy).xyz;
    float3 c10 = _PathTraceTexture.Sample(s, tc           -dy).xyz;
    float3 c20 = _PathTraceTexture.Sample(s, tc    +dx    -dy).xyz;
    float3 c30 = _PathTraceTexture.Sample(s, tc+2.0*dx    -dy).xyz;
    float3 c01 = _PathTraceTexture.Sample(s, tc    -dx       ).xyz;
    float3 c11 = _PathTraceTexture.Sample(s, tc              ).xyz;
    float3 c21 = _PathTraceTexture.Sample(s, tc    +dx       ).xyz;
    float3 c31 = _PathTraceTexture.Sample(s, tc+2.0*dx       ).xyz;
    float3 c02 = _PathTraceTexture.Sample(s, tc    -dx    +dy).xyz;
    float3 c12 = _PathTraceTexture.Sample(s, tc           +dy).xyz;
    float3 c22 = _PathTraceTexture.Sample(s, tc    +dx    +dy).xyz;
    float3 c32 = _PathTraceTexture.Sample(s, tc+2.0*dx    +dy).xyz;
    float3 c03 = _PathTraceTexture.Sample(s, tc    -dx+2.0*dy).xyz;
    float3 c13 = _PathTraceTexture.Sample(s, tc       +2.0*dy).xyz;
    float3 c23 = _PathTraceTexture.Sample(s, tc    +dx+2.0*dy).xyz;
    float3 c33 = _PathTraceTexture.Sample(s, tc+2.0*dx+2.0*dy).xyz;

    color = mul(weights[0], float4x3(c00, c10, c20, c30));
    color+= mul(weights[1], float4x3(c01, c11, c21, c31));
    color+= mul(weights[2], float4x3(c02, c12, c22, c32));
    color+= mul(weights[3], float4x3(c03, c13, c23, c33));
 
    // final sum and weight normalization
    return float4(color/dot(mul(weights, float4(1,1,1,1)), 1), 1);
    
}