// Dedicated additive ground-light effect for existing route lamp posts.
// It deliberately has no dependency on SceneryShader or train-light state.

float4x4 WorldViewProjection;
float Intensity;

struct VERTEX_INPUT
{
    float3 Position : POSITION0;
    float4 Color : COLOR0;
    float2 TexCoords : TEXCOORD0;
};

struct VERTEX_OUTPUT
{
    float4 Position : POSITION0;
    float2 TexCoords : TEXCOORD0;
};

VERTEX_OUTPUT VSSceneryLamp(in VERTEX_INPUT In)
{
    VERTEX_OUTPUT Out = (VERTEX_OUTPUT)0;
    Out.Position = mul(WorldViewProjection, float4(In.Position, 1));
    Out.TexCoords = In.TexCoords;
    return Out;
}

float4 PSSceneryLamp(in VERTEX_OUTPUT In) : COLOR0
{
    float2 offset = In.TexCoords * 2 - 1;
    float falloff = saturate(1 - dot(offset, offset));
    falloff *= falloff;
    return float4(float3(1.0, 0.52, 0.18) * falloff * Intensity, falloff * Intensity);
}

technique SceneryLamp
{
    pass Pass_0
    {
        VertexShader = compile vs_4_0_level_9_1 VSSceneryLamp();
        PixelShader = compile ps_4_0_level_9_1 PSSceneryLamp();
    }
}
