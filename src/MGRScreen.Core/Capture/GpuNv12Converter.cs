using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace MGRScreen.Core.Capture;

/// <summary>
/// Converte a textura BGRA da captura para NV12 na GPU, já no tamanho final, e só então traz os
/// bytes pra CPU. Antes o frame BGRA inteiro (8 MB em 1080p, 15 MB em 1440p) era lido da GPU e a
/// redução/conversão ficava com o ffmpeg na CPU; agora chegam 3 MB prontos pro encoder.
///
/// São duas passadas com um triângulo de tela cheia: uma escreve o plano Y (R8) e a outra o plano
/// UV intercalado em meia resolução (R8G8, que tem exatamente o layout de bytes do NV12).
/// A matriz é BT.601 faixa limitada — a mesma que o ffmpeg usava, pra não mudar as cores.
/// </summary>
internal sealed class GpuNv12Converter : IDisposable
{
    private const string Source = """
        Texture2D src : register(t0);
        SamplerState smp : register(s0);
        cbuffer Params : register(b0) { float2 uvScale; float2 tap; };

        struct VsOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

        VsOut VsMain(uint id : SV_VertexID)
        {
            VsOut o;
            float2 t = float2((id << 1) & 2, id & 2);
            o.pos = float4(t * float2(2, -2) + float2(-1, 1), 0, 1);
            o.uv = t * uvScale;
            return o;
        }

        // 4 amostras bilineares em volta do centro: ao reduzir (1440p -> 1080p, 4K -> 1080p) cobre
        // a area inteira do pixel de destino em vez de pular texels. Sem reducao, tap = 0.
        float3 Fetch(float2 uv)
        {
            return 0.25 * (src.Sample(smp, uv + float2(-tap.x, -tap.y)).rgb
                         + src.Sample(smp, uv + float2( tap.x, -tap.y)).rgb
                         + src.Sample(smp, uv + float2(-tap.x,  tap.y)).rgb
                         + src.Sample(smp, uv + float2( tap.x,  tap.y)).rgb);
        }

        float PsY(VsOut i) : SV_Target
        {
            return dot(Fetch(i.uv), float3(0.25679, 0.50413, 0.09791)) + 0.06275;
        }

        float2 PsUv(VsOut i) : SV_Target
        {
            float3 c = Fetch(i.uv);
            return float2(dot(c, float3(-0.14822, -0.29099, 0.43922)),
                          dot(c, float3(0.43922, -0.36779, -0.07143))) + 0.50196;
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public float UvScaleX, UvScaleY, TapX, TapY;
    }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _lumaShader;
    private readonly ID3D11PixelShader _chromaShader;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11RasterizerState _rasterizer;
    private readonly ID3D11Buffer _params;

    private ID3D11Texture2D? _lumaTarget;
    private ID3D11Texture2D? _chromaTarget;
    private ID3D11RenderTargetView? _lumaView;
    private ID3D11RenderTargetView? _chromaView;
    private ID3D11Texture2D? _lumaStaging;
    private ID3D11Texture2D? _chromaStaging;
    private ID3D11Texture2D? _sampleable;
    private int _width;
    private int _height;

    public GpuNv12Converter(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;
        _vertexShader = device.CreateVertexShader(HlslCompiler.Compile(Source, "VsMain", "vs_4_0"));
        _lumaShader = device.CreatePixelShader(HlslCompiler.Compile(Source, "PsY", "ps_4_0"));
        _chromaShader = device.CreatePixelShader(HlslCompiler.Compile(Source, "PsUv", "ps_4_0"));
        _sampler = device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));
        _rasterizer = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
        _params = device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer));
    }

    /// <summary>
    /// Converte <paramref name="source"/> (só a região de conteúdo, que pode ser menor que a
    /// textura durante um redimensionamento) para NV12 de <paramref name="width"/> x
    /// <paramref name="height"/> (pares) em <paramref name="destination"/>.
    /// </summary>
    public unsafe void Convert(ID3D11Texture2D source, int contentWidth, int contentHeight, int width, int height, byte[] destination)
    {
        EnsureTargets(width, height);

        var description = source.Description;
        var texture = source;
        if ((description.BindFlags & BindFlags.ShaderResource) == 0)
        {
            // A textura da captura não pode ser amostrada direto: copia (na GPU) pra uma que pode.
            var mine = _sampleable?.Description;
            if (mine is null || mine.Value.Width != description.Width || mine.Value.Height != description.Height)
            {
                _sampleable?.Dispose();
                _sampleable = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = description.Width,
                    Height = description.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = description.Format,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                });
            }

            _context.CopyResource(_sampleable!, source);
            texture = _sampleable!;
        }

        using var view = _device.CreateShaderResourceView(texture);

        var scaleX = contentWidth / (float)description.Width;
        var scaleY = contentHeight / (float)description.Height;
        var reducing = width < contentWidth || height < contentHeight;

        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vertexShader);
        _context.VSSetConstantBuffer(0, _params);
        _context.RSSetState(_rasterizer);
        _context.PSSetConstantBuffer(0, _params);
        _context.PSSetSampler(0, _sampler);
        _context.PSSetShaderResource(0, view);

        Draw(_lumaShader, _lumaView!, width, height, scaleX, scaleY, reducing);
        Draw(_chromaShader, _chromaView!, width / 2, height / 2, scaleX, scaleY, reducing);

        _context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        _context.PSSetShaderResource(0, null!);

        _context.CopyResource(_lumaStaging!, _lumaTarget!);
        _context.CopyResource(_chromaStaging!, _chromaTarget!);

        fixed (byte* dest = destination)
        {
            ReadPlane(_lumaStaging!, dest, width, height);
            ReadPlane(_chromaStaging!, dest + (width * height), width, height / 2);
        }
    }

    private void Draw(ID3D11PixelShader shader, ID3D11RenderTargetView target, int width, int height,
        float scaleX, float scaleY, bool reducing)
    {
        var parameters = new Params
        {
            UvScaleX = scaleX,
            UvScaleY = scaleY,
            TapX = reducing ? 0.25f * scaleX / width : 0,
            TapY = reducing ? 0.25f * scaleY / height : 0,
        };
        _context.UpdateSubresource(in parameters, _params);
        _context.PSSetShader(shader);
        _context.OMSetRenderTargets(target);
        _context.RSSetViewport(new Viewport(width, height));
        _context.Draw(3, 0);
    }

    private unsafe void ReadPlane(ID3D11Texture2D staging, byte* destination, int rowBytes, int rows)
    {
        var mapped = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var source = (byte*)mapped.DataPointer;
            if (mapped.RowPitch == (uint)rowBytes)
            {
                Buffer.MemoryCopy(source, destination, (long)rowBytes * rows, (long)rowBytes * rows);
            }
            else
            {
                for (var row = 0; row < rows; row++)
                {
                    Buffer.MemoryCopy(source + (row * mapped.RowPitch), destination + (row * rowBytes), rowBytes, rowBytes);
                }
            }
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
    }

    private void EnsureTargets(int width, int height)
    {
        if (width == _width && height == _height)
        {
            return;
        }

        DisposeTargets();

        ID3D11Texture2D Create(Format format, int w, int h, bool staging) => _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = staging ? ResourceUsage.Staging : ResourceUsage.Default,
            BindFlags = staging ? BindFlags.None : BindFlags.RenderTarget,
            CPUAccessFlags = staging ? CpuAccessFlags.Read : CpuAccessFlags.None,
        });

        _lumaTarget = Create(Format.R8_UNorm, width, height, staging: false);
        _chromaTarget = Create(Format.R8G8_UNorm, width / 2, height / 2, staging: false);
        _lumaStaging = Create(Format.R8_UNorm, width, height, staging: true);
        _chromaStaging = Create(Format.R8G8_UNorm, width / 2, height / 2, staging: true);
        _lumaView = _device.CreateRenderTargetView(_lumaTarget);
        _chromaView = _device.CreateRenderTargetView(_chromaTarget);
        _width = width;
        _height = height;
    }

    private void DisposeTargets()
    {
        _lumaView?.Dispose();
        _chromaView?.Dispose();
        _lumaTarget?.Dispose();
        _chromaTarget?.Dispose();
        _lumaStaging?.Dispose();
        _chromaStaging?.Dispose();
        _lumaView = _chromaView = null;
        _lumaTarget = _chromaTarget = _lumaStaging = _chromaStaging = null;
        _width = _height = 0;
    }

    public void Dispose()
    {
        DisposeTargets();
        _sampleable?.Dispose();
        _params.Dispose();
        _rasterizer.Dispose();
        _sampler.Dispose();
        _chromaShader.Dispose();
        _lumaShader.Dispose();
        _vertexShader.Dispose();
    }
}
