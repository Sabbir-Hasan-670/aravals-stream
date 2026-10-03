using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

namespace AravalsStream.App.Composition.D3D11;

[StructLayout(LayoutKind.Sequential)]
public struct TransformConstants
{
    public Matrix TransformMatrix; // 64 bytes
    public Vector4 CropRect;        // 16 bytes (left, top, right, bottom in UV)
    public float Opacity;           // 4 bytes
    public Vector3 Padding;         // 12 bytes
}

[StructLayout(LayoutKind.Sequential)]
public struct BlurConstants
{
    public Vector2 Direction; // (1, 0) for horizontal, (0, 1) for vertical
    public Vector2 TexelSize; // (1/Width, 1/Height)
}

public sealed class D3D11Shaders : IDisposable
{
    private const string TransformShaderCode = @"
cbuffer TransformBuffer : register(b0)
{
    float4x4 TransformMatrix;
    float4 CropRect; // left, top, right, bottom in UV
    float Opacity;
    float3 Padding;
};

struct VS_INPUT
{
    float2 Position : POSITION;
    float2 TexCoord : TEXCOORD0;
};

struct PS_INPUT
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

PS_INPUT VSMain(VS_INPUT input)
{
    PS_INPUT output;
    output.Position = mul(TransformMatrix, float4(input.Position, 0.0f, 1.0f));
    output.TexCoord = input.TexCoord;
    return output;
}

Texture2D SourceTexture : register(t0);
SamplerState LinearSampler : register(s0);

float4 PSMain(PS_INPUT input) : SV_TARGET
{
    // Crop the source then scale the remaining rectangle across the destination quad.
    float2 uv = lerp(CropRect.xy, CropRect.zw, input.TexCoord);
    float4 color = SourceTexture.Sample(LinearSampler, uv);
    color.rgb *= color.a;
    color.a *= Opacity;
    color.rgb *= Opacity; // Premultiplied alpha output
    return color;
}
";

    private const string BlurShaderCode = @"
cbuffer BlurBuffer : register(b0)
{
    float2 Direction;
    float2 TexelSize;
};

struct VS_INPUT
{
    float2 Position : POSITION;
    float2 TexCoord : TEXCOORD0;
};

struct PS_INPUT
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

PS_INPUT VSMain(VS_INPUT input)
{
    PS_INPUT output;
    output.Position = float4(input.Position, 0.0f, 1.0f);
    output.TexCoord = input.TexCoord;
    return output;
}

Texture2D SourceTexture : register(t0);
SamplerState LinearSampler : register(s0);

// 9-tap separable Gaussian blur
float4 PSMain(PS_INPUT input) : SV_TARGET
{
    float2 uv = input.TexCoord;
    float2 offset = Direction * TexelSize;

    float weights[5] = { 0.227027, 0.1945946, 0.1216216, 0.054054, 0.016216 };

    float4 color = SourceTexture.Sample(LinearSampler, uv) * weights[0];
    for (int i = 1; i < 5; ++i)
    {
        color += SourceTexture.Sample(LinearSampler, uv + offset * float(i)) * weights[i];
        color += SourceTexture.Sample(LinearSampler, uv - offset * float(i)) * weights[i];
    }
    return color;
}
";

    public VertexShader TransformVS { get; }
    public PixelShader TransformPS { get; }
    public VertexShader BlurVS { get; }
    public PixelShader BlurPS { get; }
    public InputLayout InputLayout { get; }
    public SamplerState LinearSampler { get; }
    public BlendState AlphaBlendState { get; }
    public BlendState OpaqueBlendState { get; }
    public Buffer TransformConstantBuffer { get; }
    public Buffer BlurConstantBuffer { get; }
    public Buffer QuadVertexBuffer { get; }

    public D3D11Shaders(Device device)
    {
        // 1. Compile and create Transform Vertex & Pixel Shaders
        using var vsTransformBytecode = ShaderBytecode.Compile(TransformShaderCode, "VSMain", "vs_4_0");
        using var psTransformBytecode = ShaderBytecode.Compile(TransformShaderCode, "PSMain", "ps_4_0");
        TransformVS = new VertexShader(device, vsTransformBytecode);
        TransformPS = new PixelShader(device, psTransformBytecode);

        // 2. Compile and create Blur Shaders
        using var vsBlurBytecode = ShaderBytecode.Compile(BlurShaderCode, "VSMain", "vs_4_0");
        using var psBlurBytecode = ShaderBytecode.Compile(BlurShaderCode, "PSMain", "ps_4_0");
        BlurVS = new VertexShader(device, vsBlurBytecode);
        BlurPS = new PixelShader(device, psBlurBytecode);

        // 3. Input Layout for Quad
        InputElement[] inputElements =
        [
            new InputElement("POSITION", 0, Format.R32G32_Float, 0, 0),
            new InputElement("TEXCOORD", 0, Format.R32G32_Float, 8, 0)
        ];
        InputLayout = new InputLayout(device, vsTransformBytecode, inputElements);

        // 4. Quad Vertex Buffer (Two triangles covering normalized device coordinates [-1, 1])
        float[] quadVertices =
        [
            // Pos (X, Y),   UV (U, V)
            -1.0f,  1.0f,    0.0f, 0.0f,
             1.0f,  1.0f,    1.0f, 0.0f,
            -1.0f, -1.0f,    0.0f, 1.0f,

             1.0f,  1.0f,    1.0f, 0.0f,
             1.0f, -1.0f,    1.0f, 1.0f,
            -1.0f, -1.0f,    0.0f, 1.0f
        ];
        QuadVertexBuffer = Buffer.Create(device, BindFlags.VertexBuffer, quadVertices);

        // 5. Sampler State
        var samplerDesc = new SamplerStateDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunction = Comparison.Never,
            MinimumLod = 0,
            MaximumLod = float.MaxValue
        };
        LinearSampler = new SamplerState(device, samplerDesc);

        // 6. Blend States: Premultiplied Alpha Blending
        var blendDesc = new BlendStateDescription();
        blendDesc.RenderTarget[0].IsBlendEnabled = true;
        blendDesc.RenderTarget[0].SourceBlend = BlendOption.One;
        blendDesc.RenderTarget[0].DestinationBlend = BlendOption.InverseSourceAlpha;
        blendDesc.RenderTarget[0].BlendOperation = BlendOperation.Add;
        blendDesc.RenderTarget[0].SourceAlphaBlend = BlendOption.One;
        blendDesc.RenderTarget[0].DestinationAlphaBlend = BlendOption.InverseSourceAlpha;
        blendDesc.RenderTarget[0].AlphaBlendOperation = BlendOperation.Add;
        blendDesc.RenderTarget[0].RenderTargetWriteMask = ColorWriteMaskFlags.All;
        AlphaBlendState = new BlendState(device, blendDesc);

        var opaqueDesc = new BlendStateDescription();
        opaqueDesc.RenderTarget[0].IsBlendEnabled = false;
        opaqueDesc.RenderTarget[0].RenderTargetWriteMask = ColorWriteMaskFlags.All;
        OpaqueBlendState = new BlendState(device, opaqueDesc);

        // 7. Constant Buffers
        TransformConstantBuffer = new Buffer(device, Utilities.SizeOf<TransformConstants>(), ResourceUsage.Dynamic,
            BindFlags.ConstantBuffer, CpuAccessFlags.Write, ResourceOptionFlags.None, 0);

        BlurConstantBuffer = new Buffer(device, Utilities.SizeOf<BlurConstants>(), ResourceUsage.Dynamic,
            BindFlags.ConstantBuffer, CpuAccessFlags.Write, ResourceOptionFlags.None, 0);
    }

    public void Dispose()
    {
        TransformVS.Dispose();
        TransformPS.Dispose();
        BlurVS.Dispose();
        BlurPS.Dispose();
        InputLayout.Dispose();
        LinearSampler.Dispose();
        AlphaBlendState.Dispose();
        OpaqueBlendState.Dispose();
        TransformConstantBuffer.Dispose();
        BlurConstantBuffer.Dispose();
        QuadVertexBuffer.Dispose();
    }
}
