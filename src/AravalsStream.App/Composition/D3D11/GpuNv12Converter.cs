using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

namespace AravalsStream.App.Composition.D3D11;

/// <summary>
/// GPU-accelerated color converter and texture scaler.
/// Converts BGRA surfaces to NV12 format on the GPU, and performs GPU-based
/// downscaling (e.g. 1080p -> 720p) with bilinear filtering.
/// </summary>
public sealed class GpuNv12Converter : IDisposable
{
    private readonly D3D11DeviceManager _deviceManager;
    private readonly object _syncLock = new();
    private PixelShader? _nv12PixelShader;
    private VertexShader? _fullscreenVertexShader;
    private Buffer? _constantBuffer;
    private int _shaderGen = -1;
    private bool _disposed;

    [StructLayout(LayoutKind.Sequential)]
    private struct Nv12Constants
    {
        public float SourceWidth;
        public float SourceHeight;
        public float OutputWidth;
        public float OutputHeight;
    }

    private const string Nv12ShaderCode = @"
cbuffer Nv12Buffer : register(b0)
{
    float SourceWidth;
    float SourceHeight;
    float OutputWidth;
    float OutputHeight;
};

struct VS_OUTPUT
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

VS_OUTPUT VSMain(uint id : SV_VertexID)
{
    VS_OUTPUT output;
    output.TexCoord = float2((id << 1) & 2, id & 2);
    output.Position = float4(output.TexCoord * float2(2.0f, -2.0f) + float2(-1.0f, 1.0f), 0.0f, 1.0f);
    return output;
}

Texture2D SourceTexture : register(t0);
SamplerState LinearSampler : register(s0);

// BT.709 Color conversion constants
static const float3 Y_COEFF = float3(0.2126f, 0.7152f, 0.0722f);
static const float3 U_COEFF = float3(-0.1146f, -0.3854f, 0.5000f);
static const float3 V_COEFF = float3(0.5000f, -0.4542f, -0.0458f);

float4 PSMain(VS_OUTPUT input) : SV_TARGET
{
    float2 pixelPos = input.Position.xy;
    float origHeight = SourceHeight;

    if (pixelPos.y < origHeight)
    {
        // Y Plane: One Y value per destination pixel
        float2 uv = float2(pixelPos.x / SourceWidth, pixelPos.y / origHeight);
        float4 rgb = SourceTexture.Sample(LinearSampler, uv);
        float y = dot(rgb.rgb, Y_COEFF);
        // Studio range: Y = 16/255 + y * 219/255
        float yStudio = 16.0f / 255.0f + y * (219.0f / 255.0f);
        return float4(yStudio, 0, 0, 1);
    }
    else
    {
        // UV Plane: Bottom Height/2 lines. Interleaved U and V.
        float uvRow = (pixelPos.y - origHeight) * 2.0f;
        int colIndex = int(pixelPos.x);
        bool isU = (colIndex % 2) == 0;
        // Average each 2x2 source block around its center for proper 4:2:0 chroma.
        float uvCol = float(colIndex - (colIndex % 2)) + 1.0f;

        float2 uv = float2(uvCol / SourceWidth, uvRow / origHeight);
        float4 rgb = SourceTexture.Sample(LinearSampler, uv);

        if (isU)
        {
            float u = dot(rgb.rgb, U_COEFF) + 0.5f;
            float uStudio = 128.0f / 255.0f + (u - 0.5f) * (224.0f / 255.0f);
            return float4(uStudio, 0, 0, 1);
        }
        else
        {
            float v = dot(rgb.rgb, V_COEFF) + 0.5f;
            float vStudio = 128.0f / 255.0f + (v - 0.5f) * (224.0f / 255.0f);
            return float4(vStudio, 0, 0, 1);
        }
    }
}
";

    public GpuNv12Converter(D3D11DeviceManager? deviceManager = null)
    {
        _deviceManager = deviceManager ?? D3D11DeviceManager.Instance;
    }

    private void EnsureShaders(Device device)
    {
        if (_nv12PixelShader is null || _shaderGen != _deviceManager.DeviceGeneration)
        {
            _nv12PixelShader?.Dispose();
            _fullscreenVertexShader?.Dispose();
            _constantBuffer?.Dispose();

            using var vsBytecode = ShaderBytecode.Compile(Nv12ShaderCode, "VSMain", "vs_4_0");
            using var psBytecode = ShaderBytecode.Compile(Nv12ShaderCode, "PSMain", "ps_4_0");

            _fullscreenVertexShader = new VertexShader(device, vsBytecode);
            _nv12PixelShader = new PixelShader(device, psBytecode);
            _constantBuffer = new Buffer(device, Utilities.SizeOf<Nv12Constants>(), ResourceUsage.Dynamic,
                BindFlags.ConstantBuffer, CpuAccessFlags.Write, ResourceOptionFlags.None, 0);

            _shaderGen = _deviceManager.DeviceGeneration;
        }
    }

    /// <summary>
    /// Resizes a D3D11 BGRA texture on the GPU using bilinear sampling.
    /// </summary>
    public Texture2D ResizeGpu(Texture2D sourceTexture, int targetWidth, int targetHeight, D3D11Shaders shaders)
    {
        lock (_syncLock)
        {
            return _deviceManager.WithContext((device, context) =>
            {
                var targetTex = _deviceManager.RentTexture(
                    targetWidth, targetHeight,
                    Format.B8G8R8A8_UNorm,
                    BindFlags.RenderTarget | BindFlags.ShaderResource);

                using var rtv = new RenderTargetView(device, targetTex);
                using var srv = new ShaderResourceView(device, sourceTexture);

                context.OutputMerger.SetRenderTargets(rtv);
                context.Rasterizer.SetViewport(new ViewportF(0, 0, targetWidth, targetHeight));
                context.OutputMerger.SetBlendState(shaders.OpaqueBlendState, Color4.White, -1);

                var constants = new TransformConstants
                {
                    TransformMatrix = Matrix.Identity,
                    CropRect = new Vector4(0, 0, 1, 1),
                    Opacity = 1.0f
                };

                var mapped = context.MapSubresource(shaders.TransformConstantBuffer, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None);
                Marshal.StructureToPtr(constants, mapped.DataPointer, false);
                context.UnmapSubresource(shaders.TransformConstantBuffer, 0);

                context.InputAssembler.InputLayout = shaders.InputLayout;
                context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
                context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(shaders.QuadVertexBuffer, Utilities.SizeOf<float>() * 4, 0));

                context.VertexShader.Set(shaders.TransformVS);
                context.VertexShader.SetConstantBuffer(0, shaders.TransformConstantBuffer);
                context.PixelShader.Set(shaders.TransformPS);
                context.PixelShader.SetConstantBuffer(0, shaders.TransformConstantBuffer);
                context.PixelShader.SetSampler(0, shaders.LinearSampler);
                context.PixelShader.SetShaderResource(0, srv);

                context.Draw(6, 0);
                context.OutputMerger.SetRenderTargets((RenderTargetView?)null);

                return targetTex;
            });
        }
    }

    /// <summary>
    /// Converts a BGRA texture to NV12 format on the GPU and reads back to destination byte array.
    /// Eliminates all CPU color conversion calculations.
    /// </summary>
    public void ConvertBgraToNv12Gpu(
        Texture2D bgraTexture,
        int width,
        int height,
        byte[] destinationBuffer,
        D3D11Shaders shaders)
    {
        int nv12Height = height + (height / 2);
        int expectedSize = width * nv12Height;
        if (destinationBuffer.Length < expectedSize)
        {
            throw new ArgumentException($"Destination buffer is too small for NV12. Required: {expectedSize}, Actual: {destinationBuffer.Length}");
        }

        lock (_syncLock)
        {
            _deviceManager.WithContext((device, context) =>
            {
                EnsureShaders(device);

                // 1. Rent R8_UNorm render target texture of size Width x (Height * 1.5)
                var nv12RenderTex = _deviceManager.RentTexture(
                    width, nv12Height,
                    Format.R8_UNorm,
                    BindFlags.RenderTarget | BindFlags.ShaderResource);

                // 2. Rent staging texture for fast CPU readback
                var stagingTex = _deviceManager.RentTexture(
                    width, nv12Height,
                    Format.R8_UNorm,
                    BindFlags.None,
                    ResourceUsage.Staging,
                    CpuAccessFlags.Read);

                using var rtv = new RenderTargetView(device, nv12RenderTex);
                using var sourceSrv = new ShaderResourceView(device, bgraTexture);

                try
                {
                    context.OutputMerger.SetRenderTargets(rtv);
                    context.Rasterizer.SetViewport(new ViewportF(0, 0, width, nv12Height));

                    var constants = new Nv12Constants
                    {
                        SourceWidth = width,
                        SourceHeight = height,
                        OutputWidth = width,
                        OutputHeight = nv12Height
                    };

                    var mapped = context.MapSubresource(_constantBuffer, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None);
                    Marshal.StructureToPtr(constants, mapped.DataPointer, false);
                    context.UnmapSubresource(_constantBuffer, 0);

                    // Fullscreen quad without vertex buffer using SV_VertexID
                    context.InputAssembler.InputLayout = null;
                    context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;

                    context.VertexShader.Set(_fullscreenVertexShader);
                    context.PixelShader.Set(_nv12PixelShader);
                    context.PixelShader.SetConstantBuffer(0, _constantBuffer);
                    context.PixelShader.SetSampler(0, shaders.LinearSampler);
                    context.PixelShader.SetShaderResource(0, sourceSrv);

                    context.Draw(3, 0); // Fullscreen triangle
                    context.OutputMerger.SetRenderTargets((RenderTargetView?)null);

                    // 3. Copy to staging texture for fast readback
                    context.CopyResource(nv12RenderTex, stagingTex);

                    // 4. Map and copy to destination buffer
                    var mappedStaging = context.MapSubresource(stagingTex, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
                    try
                    {
                        for (int row = 0; row < nv12Height; row++)
                        {
                            Marshal.Copy(
                                IntPtr.Add(mappedStaging.DataPointer, row * mappedStaging.RowPitch),
                                destinationBuffer,
                                row * width,
                                width);
                        }
                    }
                    finally
                    {
                        context.UnmapSubresource(stagingTex, 0);
                    }
                }
                finally
                {
                    _deviceManager.ReturnTexture(nv12RenderTex);
                    _deviceManager.ReturnTexture(stagingTex);
                }
            });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_syncLock)
        {
            _nv12PixelShader?.Dispose();
            _fullscreenVertexShader?.Dispose();
            _constantBuffer?.Dispose();
        }
    }
}
