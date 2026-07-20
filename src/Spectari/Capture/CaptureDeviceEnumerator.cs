using System.Runtime.InteropServices;

namespace Spectari.Capture;

internal sealed record CaptureDeviceDescription(string SymbolicLink, string FriendlyName);

internal sealed record CaptureDeviceDisplayItem(CaptureDeviceDescription Device, string DisplayName);

internal static class CaptureDevicePolicy
{
    internal static IReadOnlyList<CaptureDeviceDisplayItem> PrepareDisplayItems(
        IEnumerable<CaptureDeviceDescription> devices)
    {
        List<CaptureDeviceDescription> ordered = devices
            .Where(device => !string.IsNullOrWhiteSpace(device.SymbolicLink))
            .GroupBy(device => device.SymbolicLink, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(device => DisplayBaseName(device), StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.SymbolicLink, StringComparer.Ordinal)
            .ToList();

        var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var items = new List<CaptureDeviceDisplayItem>(ordered.Count);
        foreach (CaptureDeviceDescription device in ordered)
        {
            string baseName = DisplayBaseName(device);
            nameCounts.TryGetValue(baseName, out int previousCount);
            int count = previousCount + 1;
            nameCounts[baseName] = count;
            string displayName = count == 1 ? baseName : $"{baseName} ({count})";
            items.Add(new CaptureDeviceDisplayItem(device, displayName));
        }
        return items;
    }

    private static string DisplayBaseName(CaptureDeviceDescription device) =>
        string.IsNullOrWhiteSpace(device.FriendlyName) ? "Capture device" : device.FriendlyName.Trim();
}

internal static class CaptureDeviceEnumerator
{
    internal static List<CaptureDeviceDescription> GetDevices()
    {
        try
        {
            return EnumerateDevices();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[capture-device] enumeration failed: {ex.Message.Replace('\r', ' ').Replace('\n', ' ')}");
            return [];
        }
    }

    private static List<CaptureDeviceDescription> EnumerateDevices()
    {
        nint attributes = 0;
        nint activateArray = 0;
        uint count = 0;
        bool mediaFoundationStarted = false;
        try
        {
            MediaFoundationInterop.ThrowIfFailed(
                MediaFoundationInterop.MFStartup(MediaFoundationInterop.MfVersion, 0),
                "Media Foundation startup");
            mediaFoundationStarted = true;
            MediaFoundationInterop.ThrowIfFailed(
                MediaFoundationInterop.MFCreateAttributes(out attributes, 1),
                "Capture device enumeration setup");
            MediaFoundationInterop.SetGuid(
                attributes,
                MediaFoundationInterop.MfDevSourceAttributeSourceType,
                MediaFoundationInterop.MfDevSourceAttributeSourceTypeVidcapGuid);
            MediaFoundationInterop.ThrowIfFailed(
                MediaFoundationInterop.MFEnumDeviceSources(attributes, out activateArray, out count),
                "Capture device enumeration");

            var devices = new List<CaptureDeviceDescription>(checked((int)count));
            for (int index = 0; index < count; index++)
            {
                nint activate = Marshal.ReadIntPtr(activateArray, checked(index * nint.Size));
                string symbolicLink = MediaFoundationInterop.GetString(
                    activate,
                    MediaFoundationInterop.MfDevSourceAttributeSourceTypeVidcapSymbolicLink);
                string friendlyName = MediaFoundationInterop.GetString(
                    activate,
                    MediaFoundationInterop.MfDevSourceAttributeFriendlyName);
                if (!string.IsNullOrWhiteSpace(symbolicLink))
                    devices.Add(new CaptureDeviceDescription(symbolicLink, friendlyName));
            }
            return devices;
        }
        finally
        {
            if (activateArray != 0)
            {
                for (int index = 0; index < count; index++)
                {
                    nint activate = Marshal.ReadIntPtr(activateArray, checked(index * nint.Size));
                    MediaFoundationInterop.Release(ref activate);
                }
                Marshal.FreeCoTaskMem(activateArray);
            }
            MediaFoundationInterop.Release(ref attributes);
            if (mediaFoundationStarted) _ = MediaFoundationInterop.MFShutdown();
        }
    }
}
