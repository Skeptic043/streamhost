using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace StreamHost.Capture;

/// <summary>
/// Interop shims between Win32/WinRT graphics capture and Vortice D3D11 wrappers.
/// </summary>
public static class D3DInterop
{
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(ref Guid iid);
    }

    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid ID3D11Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    /// <summary>Wraps a Vortice D3D11 device as the WinRT IDirect3DDevice the capture API needs.</summary>
    public static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr inspectable);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr hMonitor)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemIid;
        IntPtr itemPtr = interop.CreateForMonitor(hMonitor, ref iid);
        try
        {
            return GraphicsCaptureItem.FromAbi(itemPtr);
        }
        finally
        {
            Marshal.Release(itemPtr);
        }
    }

    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hWnd)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemIid;
        IntPtr itemPtr = interop.CreateForWindow(hWnd, ref iid);
        try
        {
            return GraphicsCaptureItem.FromAbi(itemPtr);
        }
        finally
        {
            Marshal.Release(itemPtr);
        }
    }

    /// <summary>Gets the D3D11 texture behind a capture frame's surface. Caller disposes.</summary>
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        IntPtr unknown = ((IWinRTObject)surface).NativeObject.ThisPtr;
        Guid accessIid = typeof(IDirect3DDxgiInterfaceAccess).GUID;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in accessIid, out IntPtr accessPtr));
        try
        {
            var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessPtr);
            var texIid = ID3D11Texture2DIid;
            IntPtr texPtr = access.GetInterface(ref texIid); // AddRef'd; wrapper takes ownership
            return MarshallingHelpers.FromPointer<ID3D11Texture2D>(texPtr)!;
        }
        finally
        {
            Marshal.Release(accessPtr);
        }
    }
}
