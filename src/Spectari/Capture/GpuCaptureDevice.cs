using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Spectari.Capture;

/// <summary>One D3D11 capture device shared across window reattachment generations.</summary>
internal sealed class GpuCaptureDevice : IDisposable
{
    private int _disposed;

    private GpuCaptureDevice(ID3D11Device device, ID3D11DeviceContext context)
    {
        Device = device;
        Context = context;
        using var multithread = context.QueryInterfaceOrNull<ID3D11Multithread>();
        multithread?.SetMultithreadProtected(true);
    }

    internal ID3D11Device Device { get; }
    internal ID3D11DeviceContext Context { get; }

    internal static GpuCaptureDevice Create(string? preferredAdapterLuid = null)
    {
        if (!string.IsNullOrWhiteSpace(preferredAdapterLuid))
        {
            try { return CreateOnAdapter(preferredAdapterLuid); }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[gpu-capture] could not create capture device on encoder adapter {preferredAdapterLuid}: {SingleLine(ex.Message)}; using the default capture adapter.");
            }
        }

        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            null!,
            out ID3D11Device? device,
            out _,
            out ID3D11DeviceContext? context).CheckError();
        return new GpuCaptureDevice(device!, context!);
    }

    internal (ID3D11Device Device, ID3D11DeviceContext Context) Acquire()
    {
        Marshal.AddRef(Device.NativePointer);
        try { Marshal.AddRef(Context.NativePointer); }
        catch
        {
            Marshal.Release(Device.NativePointer);
            throw;
        }
        return (new ID3D11Device(Device.NativePointer), new ID3D11DeviceContext(Context.NativePointer));
    }

    private static GpuCaptureDevice CreateOnAdapter(string adapterLuid)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint index = 0;
             factory.EnumAdapters1(index, out IDXGIAdapter1? adapter).Success;
             index++)
        {
            if (adapter is null) continue;
            using (adapter)
            {
                AdapterDescription1 description = adapter.Description1;
                string luid = $"{description.Luid.HighPart}:{description.Luid.LowPart}";
                if (!luid.Equals(adapterLuid, StringComparison.OrdinalIgnoreCase)) continue;

                D3D11.D3D11CreateDevice(
                    adapter,
                    DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport,
                    null!,
                    out ID3D11Device? device,
                    out _,
                    out ID3D11DeviceContext? context).CheckError();
                return new GpuCaptureDevice(device!, context!);
            }
        }
        throw new InvalidOperationException($"Adapter {adapterLuid} is unavailable.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Context.Dispose();
        Device.Dispose();
    }

    private static string SingleLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ');
}
