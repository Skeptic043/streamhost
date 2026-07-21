using System.Runtime.InteropServices;

namespace Spectari.Audio;

internal static unsafe class CoreAudioInterop
{
    internal const uint ClassContextAll = 23;
    internal const int RenderDataFlow = 0;
    internal const int CaptureDataFlow = 1;
    internal const int ConsoleRole = 0;
    internal const uint ActiveDeviceState = 0x00000001;

    internal static readonly Guid AudioClientId =
        new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    internal static readonly Guid AudioCaptureClientId =
        new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    internal static readonly PropertyKey DeviceFriendlyName = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        14);
    internal static readonly PropertyKey EndpointFormFactor = new(
        new Guid("1DA5D803-D492-4EDD-8C23-E0C0FFE94C7E"),
        0);

    internal static int CreateDeviceEnumerator(out nint enumerator)
    {
        Guid classId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
        Guid interfaceId = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
        return CoCreateInstance(
            ref classId,
            0,
            ClassContextAll,
            ref interfaceId,
            out enumerator);
    }

    internal static int EnumAudioEndpoints(
        nint enumerator,
        int dataFlow,
        uint stateMask,
        out nint devices)
    {
        nint result = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, int, uint, nint*, int>)Method(enumerator, 3))(
            enumerator,
            dataFlow,
            stateMask,
            &result);
        devices = result;
        return hr;
    }

    internal static int GetDefaultAudioEndpoint(
        nint enumerator,
        int dataFlow,
        int role,
        out nint device)
    {
        nint result = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)Method(enumerator, 4))(
            enumerator,
            dataFlow,
            role,
            &result);
        device = result;
        return hr;
    }

    internal static int GetDevice(nint enumerator, string id, out nint device)
    {
        nint result = 0;
        fixed (char* idPointer = id)
        {
            int hr = ((delegate* unmanaged[Stdcall]<nint, char*, nint*, int>)Method(enumerator, 5))(
                enumerator,
                idPointer,
                &result);
            device = result;
            return hr;
        }
    }

    internal static int GetCollectionCount(nint devices, out uint count)
    {
        uint result = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Method(devices, 3))(
            devices,
            &result);
        count = result;
        return hr;
    }

    internal static int GetCollectionItem(nint devices, uint index, out nint device)
    {
        nint result = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Method(devices, 4))(
            devices,
            index,
            &result);
        device = result;
        return hr;
    }

    internal static int Activate(nint device, in Guid interfaceId, out nint instance)
    {
        Guid localInterfaceId = interfaceId;
        nint result = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)Method(device, 3))(
            device,
            &localInterfaceId,
            ClassContextAll,
            0,
            &result);
        instance = result;
        return hr;
    }

    internal static int OpenPropertyStore(nint device, out nint propertyStore)
    {
        const uint STGM_READ = 0;
        nint result = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Method(device, 4))(
            device,
            STGM_READ,
            &result);
        propertyStore = result;
        return hr;
    }

    internal static int GetDeviceId(nint device, out string id)
    {
        nint value = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Method(device, 5))(
            device,
            &value);
        try
        {
            id = hr >= 0 && value != 0 ? Marshal.PtrToStringUni(value) ?? "" : "";
            return hr;
        }
        finally
        {
            if (value != 0) Marshal.FreeCoTaskMem(value);
        }
    }

    internal static int GetPropertyValue(
        nint propertyStore,
        in PropertyKey key,
        out PropVariant value)
    {
        PropertyKey localKey = key;
        PropVariant result = default;
        int hr = ((delegate* unmanaged[Stdcall]<nint, PropertyKey*, PropVariant*, int>)Method(propertyStore, 5))(
            propertyStore,
            &localKey,
            &result);
        value = result;
        return hr;
    }

    internal static int InitializeAudioClient(
        nint client,
        uint streamFlags,
        long bufferDuration,
        ref WaveFormatEx format)
    {
        fixed (WaveFormatEx* formatPointer = &format)
        {
            return ((delegate* unmanaged[Stdcall]<nint, int, uint, long, long, WaveFormatEx*, nint, int>)Method(client, 3))(
                client,
                0,
                streamFlags,
                bufferDuration,
                0,
                formatPointer,
                0);
        }
    }

    internal static int StartAudioClient(nint client) =>
        ((delegate* unmanaged[Stdcall]<nint, int>)Method(client, 10))(client);

    internal static int StopAudioClient(nint client) =>
        ((delegate* unmanaged[Stdcall]<nint, int>)Method(client, 11))(client);

    internal static int GetAudioClientService(
        nint client,
        in Guid interfaceId,
        out nint service)
    {
        Guid localInterfaceId = interfaceId;
        nint result = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Method(client, 14))(
            client,
            &localInterfaceId,
            &result);
        service = result;
        return hr;
    }

    internal static int GetNextPacketSize(nint capture, out uint frames)
    {
        uint result = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Method(capture, 5))(
            capture,
            &result);
        frames = result;
        return hr;
    }

    internal static int GetCaptureBuffer(
        nint capture,
        out nint data,
        out uint frames,
        out uint flags)
    {
        nint dataResult = 0;
        uint frameResult = 0;
        uint flagResult = 0;
        ulong devicePosition = 0;
        ulong qpcPosition = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint*, uint*, uint*, ulong*, ulong*, int>)Method(capture, 3))(
            capture,
            &dataResult,
            &frameResult,
            &flagResult,
            &devicePosition,
            &qpcPosition);
        data = dataResult;
        frames = frameResult;
        flags = flagResult;
        return hr;
    }

    internal static int ReleaseCaptureBuffer(nint capture, uint frames) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, int>)Method(capture, 4))(
            capture,
            frames);

    internal static void ClearPropVariant(ref PropVariant value) =>
        _ = PropVariantClear(ref value);

    internal static void Release(ref nint instance)
    {
        nint value = instance;
        instance = 0;
        if (value != 0)
            _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Method(value, 2))(value);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey(Guid formatId, uint propertyId)
    {
        internal Guid FormatId = formatId;
        internal uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropVariant
    {
        [FieldOffset(0)] internal ushort VarType;
        [FieldOffset(8)] internal nint PointerValue;
        [FieldOffset(8)] internal uint UIntValue;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormatEx
    {
        internal ushort FormatTag;
        internal ushort Channels;
        internal uint SamplesPerSec;
        internal uint AvgBytesPerSec;
        internal ushort BlockAlign;
        internal ushort BitsPerSample;
        internal ushort ExtraSize;
    }

    private static nint Method(nint instance, int slot) => (*(nint**)instance)[slot];

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(
        ref Guid classId,
        nint outer,
        uint classContext,
        ref Guid interfaceId,
        out nint instance);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref PropVariant value);
}
