// Liquid Glass refraction — Pixel Shader 3.0 port of the CPU EnsureMaps/Gather path.
// Compile: fxc /T ps_3_0 /E main /Fo LiquidGlassRefraction.ps LiquidGlassRefraction.hlsl
// s0 = raw captured desktop (srcW x srcH, incl. overscan + margin pad). The host fills
// the notch (Stretch=Fill), so output uv 0..1 spans the notch; map it into the source
// via the notch origin (offX, offY) so refraction can sample into the pad.

sampler2D input : register(s0);

float srcW       : register(c0);
float srcH       : register(c1);
float notchW     : register(c2);  // notch width in source px
float notchH     : register(c3);  // notch height in source px
float offX       : register(c4);  // source px of notch left edge
float offY       : register(c5);  // source px of notch top edge
float cornerR    : register(c6);  // corner radius px (scaled, clamped)
float zR         : register(c7);  // bevel z-radius px (scaled, clamped)
float uRefr      : register(c8);
float uChroma    : register(c9);
float uDistort   : register(c10);
float bevelMode  : register(c11); // >=0.5 => dome
float satFactor  : register(c12); // 1 + saturation
float brightAdd  : register(c13); // brightness (0..1 space)

float rrsdf(float px, float py, float bx, float by, float r)
{
    float qx = abs(px) - bx + r;
    float qy = abs(py) - by + r;
    float mx = max(qx, 0.0);
    float my = max(qy, 0.0);
    float outer = sqrt(mx * mx + my * my);
    float inner = min(max(qx, qy), 0.0);
    return inner + outer - r;
}

float bevel(float d, float z)
{
    if (d <= 0.0) return 0.0;
    if (d >= z) return z;
    return sqrt(d * (2.0 * z - d));
}

float sstep(float e0, float e1, float x)
{
    float t = saturate((x - e0) / (e1 - e0));
    return t * t * (3.0 - 2.0 * t);
}

float hashND(float px, float py)
{
    float s = sin(px * 127.1 + py * 311.7) * 43758.5453;
    return frac(s);
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float halfX = notchW * 0.5;
    float halfY = notchH * 0.5;
    float maxD = min(halfX, halfY);

    // Pixel within the notch, and notch-local coords centred on the notch.
    float npx = uv.x * notchW;
    float npy = uv.y * notchH;
    float lx = npx - (notchW - 1.0) * 0.5;
    float ly = npy - (notchH - 1.0) * 0.5;

    // Pass-through source pixel = notch origin + local pixel.
    float baseX = offX + npx;
    float baseY = offY + npy;

    float inside = -rrsdf(lx, ly, halfX, halfY, cornerR);

    float dispX = 0.0;
    float dispY = 0.0;
    float caX = 0.0;
    float caY = 0.0;

    if (inside > 0.0)
    {
        const float e = 2.0;
        float hC = bevel(inside, zR);
        float hR = bevel(-rrsdf(lx + e, ly, halfX, halfY, cornerR), zR);
        float hL = bevel(-rrsdf(lx - e, ly, halfX, halfY, cornerR), zR);
        float hU = bevel(-rrsdf(lx, ly + e, halfX, halfY, cornerR), zR);
        float hD = bevel(-rrsdf(lx, ly - e, halfX, halfY, cornerR), zR);

        float hGradX = (hR - hL) / (2.0 * e);
        float hGradY = (hU - hD) / (2.0 * e);

        float nx = -hGradX;
        float ny = -hGradY;
        float nLen = sqrt(nx * nx + ny * ny + 1.0);
        nx /= nLen;
        ny /= nLen;

        float depth = sstep(0.0, zR, inside);

        if (bevelMode < 0.5)
        {
            float thickNorm = (hC * 2.0) / max(zR * 2.0, 1.0);
            float k = 0.33333333 * (2.0 + thickNorm * 0.5) * uRefr * 30.0;
            dispX = hGradX * k;
            dispY = hGradY * k;
        }
        else
        {
            dispX = -lx * uRefr * depth * 0.35;
            dispY = -ly * uRefr * depth * 0.35;
        }

        if (uDistort > 0.0)
        {
            float ns = 0.08;
            dispX += (hashND(lx * ns, ly * ns) - 0.5) * uDistort * 4.0;
            dispY += (hashND(lx * ns + 37.0, ly * ns + 37.0) - 0.5) * uDistort * 4.0;
        }

        float edge = sstep(maxD * 0.35, 0.0, inside);
        float caS = uChroma * 18.0 * (edge * 0.7 + 0.3) * 2.0;
        caX = nx * caS;
        caY = ny * caS;
    }

    float gx = baseX + dispX;
    float gy = baseY + dispY;

    float g = tex2D(input, float2(gx / srcW, gy / srcH)).g;
    float r = tex2D(input, float2((gx + caX) / srcW, (gy + caY) / srcH)).r;
    float b = tex2D(input, float2((gx - caX) / srcW, (gy - caY) / srcH)).b;

    float3 col = float3(r, g, b);

    float lum = dot(col, float3(0.299, 0.587, 0.114));
    col = lum + (col - lum) * satFactor + brightAdd;
    col = saturate(col);

    return float4(col, 1.0);
}
