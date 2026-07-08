using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StreamHost.Audio;

/// <summary>
/// Captures the audio of one process tree (the game) via WASAPI process
/// loopback (Win10 2004+, the OBS "Application Audio Capture" technique).
/// Output: 48 kHz stereo float32 interleaved blocks via the Read callback,
/// silence-filled against the wall clock so the timeline never stalls —
/// crucial for A/V sync when muxed with the CFR video stream.
/// Discord (a different process tree) is excluded by construction.
/// </summary>
public sealed class ProcessAudioCapture : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    private readonly IAudioClient _client;
    private readonly IAudioCaptureClient _capture;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<byte[], int> _onSamples;
    private long _samplesWritten;

    public ProcessAudioCapture(uint targetPid, Action<byte[], int> onSamples)
    {
        _onSamples = onSamples;
        _client = ActivateProcessLoopback(targetPid);

        var format = new WAVEFORMATEX
        {
            wFormatTag = 3, // WAVE_FORMAT_IEEE_FLOAT
            nChannels = Channels,
            nSamplesPerSec = SampleRate,
            wBitsPerSample = 32,
            nBlockAlign = Channels * 4,
            nAvgBytesPerSec = SampleRate * Channels * 4,
            cbSize = 0,
        };

        const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        const long bufferDuration100ns = 2_000_000; // 200 ms
        int hr = _client.Initialize(0 /*shared*/, AUDCLNT_STREAMFLAGS_LOOPBACK,
            bufferDuration100ns, 0, ref format, IntPtr.Zero);
        Marshal.ThrowExceptionForHR(hr);

        var captureIid = typeof(IAudioCaptureClient).GUID;
        Marshal.ThrowExceptionForHR(_client.GetService(ref captureIid, out IntPtr capturePtr));
        _capture = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(capturePtr);
        Marshal.Release(capturePtr);

        Marshal.ThrowExceptionForHR(_client.Start());
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "audio-capture" };
        _thread.Start();
    }

    private void CaptureLoop()
    {
        // Timeline discipline: totalSamples must track the wall clock. Real packets
        // advance it; when the game goes quiet (loopback delivers nothing) we inject
        // silence. If real audio then overshoots wall time slightly we just let it —
        // the ±20 ms band keeps mux interleaving happy without audible artifacts.
        var block = new byte[SampleRate * Channels * 4 / 100]; // 10 ms
        long startTicks = Stopwatch.GetTimestamp();
        int silenceThresholdSamples = SampleRate * 2 / 100;    // 20 ms behind → fill

        while (!_cts.IsCancellationRequested)
        {
            // Drain everything the capture client has
            while (true)
            {
                if (_capture.GetNextPacketSize(out uint packetFrames) < 0 || packetFrames == 0) break;

                if (_capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _) < 0) break;
                int bytes = (int)frames * Channels * 4;
                var buf = new byte[bytes];
                const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
                if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0)
                    Marshal.Copy(data, buf, 0, bytes);
                _capture.ReleaseBuffer(frames);
                _onSamples(buf, bytes);
                _samplesWritten += frames;
            }

            // Wall-clock deficit → silence fill (game quiet or not started yet)
            long elapsed = Stopwatch.GetTimestamp() - startTicks;
            long expectedSamples = elapsed * SampleRate / Stopwatch.Frequency;
            while (expectedSamples - _samplesWritten > silenceThresholdSamples && !_cts.IsCancellationRequested)
            {
                Array.Clear(block);
                _onSamples(block, block.Length);
                _samplesWritten += block.Length / (Channels * 4);
            }

            Thread.Sleep(5);
        }
    }

    // ---- activation plumbing --------------------------------------------

    private static IAudioClient ActivateProcessLoopback(uint pid)
    {
        var activationParams = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            ProcessLoopbackParams = new AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
            {
                TargetProcessId = pid,
                ProcessLoopbackMode = 0, // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
            },
        };

        int size = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
        IntPtr paramsPtr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);
            var propvariant = new PROPVARIANT
            {
                vt = 65, // VT_BLOB
                blobSize = (uint)size,
                blobData = paramsPtr,
            };

            var handler = new ActivationHandler();
            var audioClientIid = typeof(IAudioClient).GUID;
            int hr = ActivateAudioInterfaceAsync(
                "VAD\\Process_Loopback", ref audioClientIid, ref propvariant, handler, out IActivateAudioInterfaceAsyncOperation op);
            Marshal.ThrowExceptionForHR(hr);

            if (!handler.Done.WaitOne(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("ActivateAudioInterfaceAsync timed out");

            Marshal.ThrowExceptionForHR(op.GetActivateResult(out int activateHr, out object unk));
            Marshal.ThrowExceptionForHR(activateHr);
            return (IAudioClient)unk;
        }
        finally
        {
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEvent Done = new(false);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op) => Done.Set();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread.Join(1000);
        try { _client.Stop(); } catch { }
        _cts.Dispose();
    }

    // ---- interop definitions --------------------------------------------

    [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        ref PROPVARIANT activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
    {
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public int ActivationType;
        public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public uint blobSize;
        public IntPtr blobData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig]
        int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WAVEFORMATEX format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, ref WAVEFORMATEX format, IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid iid, out IntPtr service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
