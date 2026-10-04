using AravalsStream.Core.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AravalsStream.Core.Composition;
using AravalsStream.Core.Models;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

namespace AravalsStream.App.Composition.D3D11;

/// <summary>
/// High-performance Direct3D 11 GPU scene compositor.
/// Renders scenes with full transform matrices, crops, Smart Vertical blur,
/// and cached overlay blending directly on the GPU without CPU pixel loops.
/// </summary>
public sealed class D3D11FrameCompositor : IDisposable
{
    private readonly D3D11DeviceManager _deviceManager;
    private readonly object _renderLock = new();
    private D3D11Shaders? _shaders;
    private int _shaderDeviceGen = -1;

    // Cache for static images, overlays, and sources uploaded to GPU textures
    private sealed class CachedGpuTexture : IDisposable
    {
        public Texture2D Texture { get; }
        public ShaderResourceView Srv { get; }
        public int Width { get; }
        public int Height { get; }
        public long Version { get; set; }
        public long LastUsedTicks { get; set; }

        public CachedGpuTexture(Texture2D texture, ShaderResourceView srv, int width, int height, long version)
        {
            Texture = texture;
            Srv = srv;
            Width = width;
            Height = height;
            Version = version;
            LastUsedTicks = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            try { Srv.Dispose(); } catch { }
            try { Texture.Dispose(); } catch { }
        }
    }

    private readonly Dictionary<string, CachedGpuTexture> _textureCache = [];
    private const int MaxCacheEntries = 32;

    // Smart vertical intermediate textures (rented on demand)
    private const int BlurDownscaleWidth = 270;
    private const int BlurDownscaleHeight = 480;

    private long _frameCounter;
    private bool _disposed;

    public D3D11FrameCompositor(D3D11DeviceManager? deviceManager = null)
    {
        _deviceManager = deviceManager ?? D3D11DeviceManager.Instance;
        _deviceManager.DeviceReset += OnDeviceReset;
    }

    private void EnsureShaders(Device device)
    {
        if (_shaders is null || _shaderDeviceGen != _deviceManager.DeviceGeneration)
        {
            _shaders?.Dispose();
            _shaders = new D3D11Shaders(device);
            _shaderDeviceGen = _deviceManager.DeviceGeneration;
        }
    }

    private void OnDeviceReset()
    {
        lock (_renderLock)
        {
            foreach (var item in _textureCache.Values) item.Dispose();
            _textureCache.Clear();

            _shaders?.Dispose();
            _shaders = null;
            _shaderDeviceGen = -1;
        }
    }

    /// <summary>
    /// Render a scene onto a GPU surface for the specified output mode.
    /// Returns an immutable reference-counted GpuFrameLease.
    /// </summary>
    public GpuFrameLease RenderScene(
        OutputMode mode,
        Scene? scene,
        Func<SceneSource, SceneSourceFrameLease?> frameProvider,
        int targetWidth,
        int targetHeight)
    {
        lock (_renderLock)
        {
            return _deviceManager.WithContext((device, context) =>
            {
                EnsureShaders(device);
                var shaders = _shaders!;

                // Rent target render texture from D3D11DeviceManager pool
                var targetTex = _deviceManager.RentTexture(
                    targetWidth, targetHeight,
                    Format.B8G8R8A8_UNorm,
                    BindFlags.RenderTarget | BindFlags.ShaderResource);

                var rtv = new RenderTargetView(device, targetTex);

                // Set render target and viewport
                context.OutputMerger.SetRenderTargets(rtv);
                context.Rasterizer.SetViewport(new ViewportF(0, 0, targetWidth, targetHeight));

                // Clear render target on GPU (replaces full-canvas CPU memset)
                context.ClearRenderTargetView(rtv, new Color4(0.0f, 0.0f, 0.0f, 1.0f));

                if (scene is not null && scene.Sources.Count > 0)
                {
                    // Setup common D3D11 pipeline state
                    context.InputAssembler.InputLayout = shaders.InputLayout;
                    context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
                    context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(shaders.QuadVertexBuffer, Utilities.SizeOf<float>() * 4, 0));
                    context.PixelShader.SetSampler(0, shaders.LinearSampler);
                    context.OutputMerger.SetBlendState(shaders.AlphaBlendState, Color4.White, -1);

                    double logicalWidth = CanvasLayout.Size(mode).Width;
                    double logicalHeight = CanvasLayout.Size(mode).Height;
                    double scaleX = targetWidth / logicalWidth;
                    double scaleY = targetHeight / logicalHeight;

                    // Render sources bottom to top
                    foreach (var source in scene.Sources)
                    {
                        if (!source.Visible || !source.HasVideo) continue;

                        using var sourceLease = frameProvider(source);
                        var frame = sourceLease?.Frame;
                        if (frame is null || frame.Width <= 0 || frame.Height <= 0) continue;

                        var transform = mode == OutputMode.Vertical ? source.VerticalTransform : source.HorizontalTransform;
                        if (transform.Width <= 0 || transform.Height <= 0 || transform.Opacity <= 0.001) continue;

                        var sourceKey = source.Id.ToString();
                        var cachedTex = GetOrCreateSourceTexture(device, context, sourceKey, frame, sourceLease!.Version);
                        if (cachedTex is null) continue;

                        // 1. Smart Vertical Blurred Background
                        if (mode == OutputMode.Vertical && transform.BackgroundEnlarged)
                        {
                            RenderSmartVerticalBackground(device, context, shaders, cachedTex.Srv, rtv, targetWidth, targetHeight);
                            // Re-bind main render target after Smart Vertical blur passes
                            context.OutputMerger.SetRenderTargets(rtv);
                            context.Rasterizer.SetViewport(new ViewportF(0, 0, targetWidth, targetHeight));
                            context.OutputMerger.SetBlendState(shaders.AlphaBlendState, Color4.White, -1);
                        }

                        // 2. Render Main Transformed Source Quad
                        RenderTransformedQuad(context, shaders, cachedTex.Srv, transform, scaleX, scaleY, targetWidth, targetHeight, frame.Width, frame.Height);
                    }
                }

                // Unbind render target before creating shader resource view or completing frame
                context.OutputMerger.SetRenderTargets((RenderTargetView?)null);

                var srv = new ShaderResourceView(device, targetTex);
                var frameId = Interlocked.Increment(ref _frameCounter);
                var gpuFrame = new GpuVideoFrame(
                    _deviceManager,
                    targetTex,
                    rtv,
                    srv,
                    targetWidth,
                    targetHeight,
                    Format.B8G8R8A8_UNorm,
                    Stopwatch.GetTimestamp(),
                    frameId,
                    _deviceManager.DeviceGeneration);

                return gpuFrame.TransferOwnerToLease();
            });
        }
    }

    private void RenderTransformedQuad(
        DeviceContext context,
        D3D11Shaders shaders,
        ShaderResourceView srv,
        SourceTransform transform,
        double scaleX,
        double scaleY,
        int canvasWidth,
        int canvasHeight,
        int sourceWidth,
        int sourceHeight)
    {
        // Destination rectangle in pixel coordinates
        float destX = (float)(transform.X * scaleX);
        float destY = (float)(transform.Y * scaleY);
        float destW = (float)(transform.Width * scaleX);
        float destH = (float)(transform.Height * scaleY);

        // Convert pixel rectangle to Normalized Device Coordinates [-1, 1]
        // Top-left: (-1, 1), Bottom-right: (1, -1)
        float ndcX = (destX / canvasWidth) * 2.0f - 1.0f;
        float ndcY = 1.0f - (destY / canvasHeight) * 2.0f;
        float ndcW = (destW / canvasWidth) * 2.0f;
        float ndcH = (destH / canvasHeight) * 2.0f;

        // Quad in local coords is [-1, 1], so we scale by half width/height, then translate to center
        float centerX = ndcX + ndcW * 0.5f;
        float centerY = ndcY - ndcH * 0.5f;
        float scaleQuadX = ndcW * 0.5f * (float)Math.Max(0.01, transform.ScaleX);
        float scaleQuadY = ndcH * 0.5f * (float)Math.Max(0.01, transform.ScaleY);

        // Construct 2D affine transformation matrix (Scale -> Rotate -> Translate)
        var s = Matrix.Scaling(scaleQuadX, scaleQuadY, 1.0f);
        var r = transform.Rotation != 0
            ? Matrix.RotationZ((float)(-transform.Rotation * Math.PI / 180.0))
            : Matrix.Identity;
        var t = Matrix.Translation(centerX, centerY, 0.0f);
        var transformMatrix = s * r * t;

        // Crop rect in normalized UV coordinates [0, 1]
        float cropL = Math.Clamp((float)transform.CropLeft / sourceWidth, 0.0f, 1.0f - 1.0f / sourceWidth);
        float cropT = Math.Clamp((float)transform.CropTop / sourceHeight, 0.0f, 1.0f - 1.0f / sourceHeight);
        float cropR = Math.Clamp(1.0f - (float)transform.CropRight / sourceWidth, cropL + 1.0f / sourceWidth, 1.0f);
        float cropB = Math.Clamp(1.0f - (float)transform.CropBottom / sourceHeight, cropT + 1.0f / sourceHeight, 1.0f);

        // Update constant buffer
        var constants = new TransformConstants
        {
            // SharpDX stores matrices row-major. HLSL's default column-major
            // constant-buffer layout interprets these bytes as the transpose,
            // which is the column-vector equivalent of our row-vector matrix.
            TransformMatrix = transformMatrix,
            CropRect = new Vector4(cropL, cropT, cropR, cropB),
            Opacity = (float)Math.Clamp(transform.Opacity, 0.0, 1.0)
        };

        var mapped = context.MapSubresource(shaders.TransformConstantBuffer, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None);
        Marshal.StructureToPtr(constants, mapped.DataPointer, false);
        context.UnmapSubresource(shaders.TransformConstantBuffer, 0);

        context.VertexShader.Set(shaders.TransformVS);
        context.VertexShader.SetConstantBuffer(0, shaders.TransformConstantBuffer);
        context.PixelShader.Set(shaders.TransformPS);
        context.PixelShader.SetConstantBuffer(0, shaders.TransformConstantBuffer);
        context.PixelShader.SetShaderResource(0, srv);

        context.Draw(6, 0);
    }

    private void RenderSmartVerticalBackground(
        Device device,
        DeviceContext context,
        D3D11Shaders shaders,
        ShaderResourceView sourceSrv,
        RenderTargetView targetRtv,
        int targetWidth,
        int targetHeight)
    {
        // 1. Rent downscaled textures for 2-pass blur
        var downscaledTex = _deviceManager.RentTexture(
            BlurDownscaleWidth, BlurDownscaleHeight,
            Format.B8G8R8A8_UNorm,
            BindFlags.RenderTarget | BindFlags.ShaderResource);

        var blurIntermediateTex = _deviceManager.RentTexture(
            BlurDownscaleWidth, BlurDownscaleHeight,
            Format.B8G8R8A8_UNorm,
            BindFlags.RenderTarget | BindFlags.ShaderResource);

        try
        {
            using var downscaleRtv = new RenderTargetView(device, downscaledTex);
            using var downscaleSrv = new ShaderResourceView(device, downscaledTex);
            using var blurIntermediateRtv = new RenderTargetView(device, blurIntermediateTex);
            using var blurIntermediateSrv = new ShaderResourceView(device, blurIntermediateTex);

            // Pass 1: Downscale source to fill the vertical blur texture (aspect-fill)
            context.OutputMerger.SetRenderTargets(downscaleRtv);
            context.Rasterizer.SetViewport(new ViewportF(0, 0, BlurDownscaleWidth, BlurDownscaleHeight));
            context.OutputMerger.SetBlendState(shaders.OpaqueBlendState, Color4.White, -1);

            // Fullscreen quad transform
            var fsConstants = new TransformConstants
            {
                TransformMatrix = Matrix.Identity,
                CropRect = new Vector4(0, 0, 1, 1),
                Opacity = 1.0f
            };
            var mapped = context.MapSubresource(shaders.TransformConstantBuffer, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None);
            Marshal.StructureToPtr(fsConstants, mapped.DataPointer, false);
            context.UnmapSubresource(shaders.TransformConstantBuffer, 0);

            context.VertexShader.Set(shaders.TransformVS);
            context.VertexShader.SetConstantBuffer(0, shaders.TransformConstantBuffer);
            context.PixelShader.Set(shaders.TransformPS);
            context.PixelShader.SetConstantBuffer(0, shaders.TransformConstantBuffer);
            context.PixelShader.SetShaderResource(0, sourceSrv);
            context.Draw(6, 0);

            // Pass 2: Horizontal Blur (downscaled -> intermediate)
            context.OutputMerger.SetRenderTargets(blurIntermediateRtv);
            var hBlurConstants = new BlurConstants
            {
                Direction = new Vector2(1.0f, 0.0f),
                TexelSize = new Vector2(1.0f / BlurDownscaleWidth, 1.0f / BlurDownscaleHeight)
            };
            mapped = context.MapSubresource(shaders.BlurConstantBuffer, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None);
            Marshal.StructureToPtr(hBlurConstants, mapped.DataPointer, false);
            context.UnmapSubresource(shaders.BlurConstantBuffer, 0);

            context.VertexShader.Set(shaders.BlurVS);
            context.PixelShader.Set(shaders.BlurPS);
            context.PixelShader.SetConstantBuffer(0, shaders.BlurConstantBuffer);
            context.PixelShader.SetShaderResource(0, downscaleSrv);
            context.Draw(6, 0);

            // Pass 3: Vertical Blur (intermediate -> downscaled)
            context.PixelShader.SetShaderResource(0, (ShaderResourceView?)null);
            context.OutputMerger.SetRenderTargets(downscaleRtv);
            var vBlurConstants = new BlurConstants
            {
                Direction = new Vector2(0.0f, 1.0f),
                TexelSize = new Vector2(1.0f / BlurDownscaleWidth, 1.0f / BlurDownscaleHeight)
            };
            mapped = context.MapSubresource(shaders.BlurConstantBuffer, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None);
            Marshal.StructureToPtr(vBlurConstants, mapped.DataPointer, false);
            context.UnmapSubresource(shaders.BlurConstantBuffer, 0);

            context.PixelShader.SetShaderResource(0, blurIntermediateSrv);
            context.Draw(6, 0);

            // Pass 4: Composite blurred background scaled up onto main render target with slight darkening
            context.PixelShader.SetShaderResource(0, (ShaderResourceView?)null);
            context.OutputMerger.SetRenderTargets(targetRtv);
            context.Rasterizer.SetViewport(new ViewportF(0, 0, targetWidth, targetHeight));
            context.OutputMerger.SetBlendState(shaders.OpaqueBlendState, Color4.White, -1);

            var bgConstants = new TransformConstants
            {
                TransformMatrix = Matrix.Identity,
                CropRect = new Vector4(0, 0, 1, 1),
                Opacity = 0.85f // Slight dimming to emphasize foreground
            };
            mapped = context.MapSubresource(shaders.TransformConstantBuffer, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None);
            Marshal.StructureToPtr(bgConstants, mapped.DataPointer, false);
            context.UnmapSubresource(shaders.TransformConstantBuffer, 0);

            context.VertexShader.Set(shaders.TransformVS);
            context.PixelShader.Set(shaders.TransformPS);
            context.PixelShader.SetConstantBuffer(0, shaders.TransformConstantBuffer);
            context.PixelShader.SetShaderResource(0, downscaleSrv);
            context.Draw(6, 0);
        }
        finally
        {
            context.PixelShader.SetShaderResource(0, (ShaderResourceView?)null);
            context.OutputMerger.SetRenderTargets((RenderTargetView?)null);
            _deviceManager.ReturnTexture(downscaledTex);
            _deviceManager.ReturnTexture(blurIntermediateTex);
        }
    }

    private CachedGpuTexture? GetOrCreateSourceTexture(
        Device device,
        DeviceContext context,
        string key,
        RawVideoFrame frame,
        long version)
    {
        if (_textureCache.TryGetValue(key, out var cached))
        {
            cached.LastUsedTicks = Stopwatch.GetTimestamp();

            // If version matches and dimensions match, REUSE EXISTING GPU TEXTURE (0 upload cost!)
            if (cached.Version == version && cached.Width == frame.Width && cached.Height == frame.Height)
            {
                return cached;
            }

            // If dimensions match but version changed, upload new pixels
            if (cached.Width == frame.Width && cached.Height == frame.Height)
            {
                UploadTexturePixels(context, cached.Texture, frame);
                cached.Version = version;
                return cached;
            }

            // Dimensions changed: dispose and recreate
            _textureCache.Remove(key);
            cached.Dispose();
        }

        // Evict oldest texture if cache exceeds capacity
        if (_textureCache.Count >= MaxCacheEntries)
        {
            var oldest = _textureCache.OrderBy(kv => kv.Value.LastUsedTicks).First();
            _textureCache.Remove(oldest.Key);
            oldest.Value.Dispose();
        }

        // Create new Texture2D on GPU
        var desc = new Texture2DDescription
        {
            Width = frame.Width,
            Height = frame.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None
        };

        var texture = new Texture2D(device, desc);
        UploadTexturePixels(context, texture, frame);

        var srv = new ShaderResourceView(device, texture);
        var entry = new CachedGpuTexture(texture, srv, frame.Width, frame.Height, version);
        _textureCache[key] = entry;
        return entry;
    }

    private static void UploadTexturePixels(DeviceContext context, Texture2D texture, RawVideoFrame frame)
    {
        unsafe
        {
            fixed (byte* pPixels = frame.Pixels)
            {
                var dataBox = new DataBox((IntPtr)pPixels, frame.Stride, 0);
                context.UpdateSubresource(dataBox, texture, 0);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _deviceManager.DeviceReset -= OnDeviceReset;

        lock (_renderLock)
        {
            foreach (var item in _textureCache.Values) item.Dispose();
            _textureCache.Clear();

            _shaders?.Dispose();
            _shaders = null;
        }
    }
}
