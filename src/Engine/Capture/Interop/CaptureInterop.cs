using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace AmbientFx.Capture.Interop;

/// <summary>
/// COM activation-factory interface from <c>windows.graphics.capture.interop.h</c> used to create a
/// <see cref="GraphicsCaptureItem"/> for an HMONITOR without the system picker UI.
/// </summary>
[ComImport]
[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    // Vtable order matters: CreateForWindow is slot 1, CreateForMonitor is slot 2. Do not reorder.
    IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
    IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
}

/// <summary>
/// COM interface from <c>windows.graphics.directx.direct3d11.interop.h</c> implemented by every
/// WinRT <c>IDirect3DSurface</c>/<c>IDirect3DDevice</c>; exposes the underlying DXGI/D3D11 object.
/// </summary>
[ComImport]
[Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirect3DDxgiInterfaceAccess
{
    IntPtr GetInterface([In] ref Guid iid);
}

/// <summary>
/// Windows.Graphics.Capture COM/WinRT interop helpers. All members are thread-safe and stateless.
/// </summary>
internal static class CaptureInterop
{
    /// <summary>IID of the WinRT <c>IGraphicsCaptureItem</c> interface (the riid passed to CreateForMonitor).</summary>
    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    /// <summary>
    /// Creates a <see cref="GraphicsCaptureItem"/> for the given HMONITOR via
    /// <see cref="IGraphicsCaptureItemInterop.CreateForMonitor"/> (no capability/picker required, works back to Win10 1903).
    /// </summary>
    public static GraphicsCaptureItem CreateItemForMonitor(nint hmonitor)
    {
        IGraphicsCaptureItemInterop interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        Guid iid = GraphicsCaptureItemGuid;
        IntPtr itemAbi = interop.CreateForMonitor(hmonitor, ref iid); // returns a +1 ref-counted ABI pointer
        try
        {
            // FromAbi AddRefs and does NOT take ownership of the pointer, so we release it below.
            return GraphicsCaptureItem.FromAbi(itemAbi);
        }
        finally
        {
            Marshal.Release(itemAbi);
        }
    }

    /// <summary>
    /// Extracts the <see cref="ID3D11Texture2D"/> beneath a captured frame's <see cref="IDirect3DSurface"/>.
    /// The returned texture owns one COM reference; the caller must Dispose it.
    /// </summary>
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        IDirect3DDxgiInterfaceAccess access = surface.As<IDirect3DDxgiInterfaceAccess>();
        Guid iid = typeof(ID3D11Texture2D).GUID;
        IntPtr texturePtr = access.GetInterface(ref iid); // returns +1 ref
        return new ID3D11Texture2D(texturePtr);           // Vortice ctor takes ownership (no AddRef) — balanced
    }
}
