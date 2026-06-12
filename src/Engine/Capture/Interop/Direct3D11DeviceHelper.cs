using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace AmbientFx.Capture.Interop;

/// <summary>
/// Creates the Direct3D 11 device pair used by Windows.Graphics.Capture:
/// the native Vortice device/context and its WinRT <see cref="IDirect3DDevice"/> wrapper.
/// All members are thread-safe and stateless.
/// </summary>
internal static class Direct3D11DeviceHelper
{
    private static readonly FeatureLevel[] FeatureLevels =
    {
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
    };

    // d3d11.dll export, header windows.graphics.directx.direct3d11.interop.h:
    // HRESULT CreateDirect3D11DeviceFromDXGIDevice(IDXGIDevice* dxgiDevice, IInspectable** graphicsDevice)
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    /// <summary>
    /// Creates a hardware D3D11 device with BGRA support (required by Windows.Graphics.Capture),
    /// falling back to WARP if no hardware device is available. Throws <see cref="SharpGenException"/> on failure.
    /// </summary>
    public static (ID3D11Device Device, ID3D11DeviceContext Context) CreateDevice()
    {
        Result result = D3D11.D3D11CreateDevice(
            null,                            // default adapter
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport, // REQUIRED for WGC interop
            FeatureLevels,
            out ID3D11Device device,
            out ID3D11DeviceContext context);

        if (result.Failure)
        {
            result = D3D11.D3D11CreateDevice(
                null,
                DriverType.Warp,
                DeviceCreationFlags.BgraSupport,
                FeatureLevels,
                out device,
                out context);
        }

        result.CheckError();
        return (device, context);
    }

    /// <summary>
    /// Wraps the native device as a WinRT <see cref="IDirect3DDevice"/> via
    /// <c>CreateDirect3D11DeviceFromDXGIDevice</c>. The caller must Dispose the result.
    /// </summary>
    public static IDirect3DDevice CreateWinRTDevice(ID3D11Device device)
    {
        using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
        int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr abi);
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(abi); // AddRefs; does not take ownership
        }
        finally
        {
            Marshal.Release(abi);
        }
    }
}
