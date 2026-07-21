using System;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using VNotch.Services;

namespace VNotch.Controllers;

public sealed class DxgiCaptureSource : IDisposable
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _stagingTexture;
    private int _width;
    private int _height;
    private bool _disposed;
    
    public DxgiCaptureSource()
    {
        InitDxgi();
    }
    
    private void InitDxgi()
    {
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.None,
            new[] { FeatureLevel.Level_11_0 },
            out _device,
            out _context).CheckError();
            
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        adapter.EnumOutputs(0, out IDXGIOutput output).CheckError();
        using var output1 = output.QueryInterface<IDXGIOutput1>();
        
        _duplication = output1.DuplicateOutput(dxgiDevice);
        
        var desc = output.Description;
        _width = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
        _height = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;
        
        var texDesc = new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            Format = Format.B8G8R8A8_UNorm,
            ArraySize = 1,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MipLevels = 1,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging
        };
        _stagingTexture = _device.CreateTexture2D(texDesc);
    }

    public bool CaptureInto(int x, int y, int w, int h, IntPtr destBits)
    {
        if (_disposed || _duplication == null) return false;

        try
        {
            var res = _duplication.AcquireNextFrame(0, out var frameInfo, out var desktopResource);
            if (res.Failure) return false;
            
            using (desktopResource)
            {
                using var tex = desktopResource.QueryInterface<ID3D11Texture2D>();
                _context!.CopyResource(_stagingTexture!, tex);
            }
            _duplication.ReleaseFrame();

            var mapped = _context.Map(_stagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                unsafe
                {
                    byte* src = (byte*)mapped.DataPointer;
                    byte* dst = (byte*)destBits;
                    int srcStride = (int)mapped.RowPitch;
                    int dstStride = w * 4;

                    for (int row = 0; row < h; row++)
                    {
                        int srcY = Math.Clamp(y + row, 0, _height - 1);
                        int srcX = Math.Clamp(x, 0, _width - w);
                        Buffer.MemoryCopy(src + srcY * srcStride + srcX * 4, dst + row * dstStride, dstStride, dstStride);
                    }
                }
            }
            finally
            {
                _context.Unmap(_stagingTexture!, 0);
            }
            
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("LIQUIDGLASS", $"DXGI Capture failed: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        _duplication?.Dispose();
        _stagingTexture?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
