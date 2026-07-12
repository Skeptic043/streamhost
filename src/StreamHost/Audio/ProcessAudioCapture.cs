using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StreamHost.Audio;

/// <summary>
/// Captures the audio of one process tree (the game) via WASAPI process
/// loopback (Win10 2004+, the OBS "Application Audio Capture" technique).
/// Output: 48 kHz stereo float32 interleaved blocks via the Read callback.
///
/// Timeline discipline: real packets (even silent-flagged ones) carry the
/// clock — WASAPI loopback delivers them at the render rate, so as long as the
/// app keeps an audio stream open the byte count tracks real time on its own.
/// Only when the app stops rendering entirely (no packets at all for &gt;150 ms)
/// does a wall-clock fill, anchored at the moment packets stopped, advance the
/// timeline with silence. That keeps the mp4 muxer moving: it interleaves by
/// timestamp and would otherwise sit on finished video fragments waiting for
/// audio that never comes (the header-then-no-video stall). We do NOT try to
/// reconcile against WASAPI device positions — for a quiet process those
/// positions stall while the wall clock keeps ticking, so trusting them just
/// fought the idle fill and produced choppy audio.
///
/// The WASAPI drain loop never blocks on the consumer: samples go through a
/// bounded queue to a writer thread. On overflow (encoder not draining) the
/// newest packet is always queued first; if the queue is full the OLDEST
/// queued block is dropped so playback resumes near the live edge, and the
/// dropped span is owed back as a BOUNDED amount of silence (freshness wins
/// over preserving every byte). Prioritizing silence-debt ahead of fresh
/// audio was what let a transient stall become permanent silence; queuing
/// fresh first and capping the debt removes that. A starved drain loop
/// injecting silence into otherwise-fine audio was the original choppiness;
/// the highest-priority thread (MMCSS Pro Audio when available) plus the
/// non-blocking queue removes that failure mode.
/// Discord (a different process tree) is excluded by construction.
/// </summary>
public sealed class ProcessAudioCapture : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    private const int BytesPerFrame = Channels * 4;

    private static readonly byte[] SilenceBlock = new byte[SampleRate * BytesPerFrame / 100]; // 10 ms
    private const long MaxOverflowBytes = SampleRate * BytesPerFrame / 4; // 250 ms — bound on owed silence; freshness wins over preserving every byte

    private readonly IAudioClient _client;
    private readonly IAudioCaptureClient _capture;
    private readonly Thread _thread;
    private readonly Thread _writerThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<byte[], int> _onSamples;

    // Capture thread → writer thread. ~10 ms per item ≈ 2.5 s of headroom.
    private readonly BlockingCollection<(byte[] Buffer, int Length)> _queue = new(boundedCapacity: 256);
    private long _overflowBytes; // audio dropped at the queue, owed back as silence

    public ProcessAudioCapture(uint targetPid, Action<byte[], int> onSamples)
    {
        _onSamples = onSamples;
        _client = ActivateProcessLoopback(targetPid);

        // REL-2: everything past activation is transactional. If format setup,
        // Initialize, GetService, or Start throws, release the client before
        // rethrowing — otherwise a half-built capture leaks it and its threads
        // never start to be disposed.
        try
        {
            var format = new WAVEFORMATEX
            {
                wFormatTag = 3, // WAVE_FORMAT_IEEE_FLOAT
                nChannels = Channels,
                nSamplesPerSec = SampleRate,
                wBitsPerSample = 32,
                nBlockAlign = BytesPerFrame,
                nAvgBytesPerSec = SampleRate * BytesPerFrame,
                cbSize = 0,
            };

            const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
            // 500 ms: must comfortably exceed the idle-fill threshold plus worst-case
            // scheduling starvation, or WASAPI overwrites data before we drain it.
            const long bufferDuration100ns = 5_000_000;
            int hr = _client.Initialize(0 /*shared*/, AUDCLNT_STREAMFLAGS_LOOPBACK,
                bufferDuration100ns, 0, ref format, IntPtr.Zero);
            Marshal.ThrowExceptionForHR(hr);

            var captureIid = typeof(IAudioCaptureClient).GUID;
            Marshal.ThrowExceptionForHR(_client.GetService(ref captureIid, out IntPtr capturePtr));
            _capture = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(capturePtr);
            Marshal.Release(capturePtr);

            Marshal.ThrowExceptionForHR(_client.Start());
        }
        catch { try { Marshal.FinalReleaseComObject(_client); } catch { } throw; }

        _writerThread = new Thread(WriteLoop) { IsBackground = true, Name = "audio-writer", Priority = ThreadPriority.AboveNormal };
        _writerThread.Start();
        // Highest: on a machine pegged by the game, a starved drain loop is
        // exactly what used to inject spurious silence into the stream.
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "audio-capture", Priority = ThreadPriority.Highest };
        _thread.Start();
        // REL-1: one line so a live session shows audio actually came up. MMCSS
        // is best-effort (Part 4) — it may have fallen back to thread priority.
        Console.WriteLine("[audio] capture started (process loopback, 48 kHz stereo, MMCSS Pro Audio)");
    }

    private void CaptureLoop()
    {
        // REL-3: MMCSS "Pro Audio" is the OS's realtime-audio scheduling class;
        // fully guarded (Part 4), so any failure silently keeps ThreadPriority.Highest.
        IntPtr mmcss = ConfigureMmcss();
        try
        {
            long written = 0;              // frames emitted (real + idle silence) — the stream clock
            bool overflowLogged = false, errorLogged = false;

            long lastPacketTicks = Stopwatch.GetTimestamp();
            long idleAfterTicks = Stopwatch.Frequency * 150 / 1000; // no packets at all for 150 ms = app stopped rendering
            bool idle = false;
            long idleAnchorWritten = 0, idleAnchorTicks = 0;

            while (!_cts.IsCancellationRequested)
            {
                bool got = false;
                while (true)
                {
                    int hr = _capture.GetNextPacketSize(out uint packetFrames);
                    if (hr < 0 || packetFrames == 0)
                    {
                        if (hr < 0 && !errorLogged)
                        {
                            errorLogged = true;
                            Console.Error.WriteLine($"[audio] capture read failed (0x{hr:X8}) — stream continues with silence.");
                        }
                        break;
                    }

                    if (_capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _) < 0) break;
                    // HP-04: ReleaseBuffer must run even if alloc/Marshal.Copy/Emit
                    // throws, or the WASAPI buffer leaks and capture wedges.
                    try
                    {
                        got = true;

                        // Emit every real packet, silent-flagged or not — a silent packet
                        // is real render time and keeps the clock advancing without any
                        // idle fill. Copy only when it carries actual samples.
                        int bytes = (int)frames * BytesPerFrame;
                        var buf = new byte[bytes];
                        const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
                        if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0)
                            Marshal.Copy(data, buf, 0, bytes);
                        Emit(buf, bytes, ref overflowLogged);
                        written += frames;
                    }
                    finally { _capture.ReleaseBuffer(frames); }
                }

                long now = Stopwatch.GetTimestamp();
                if (got)
                {
                    lastPacketTicks = now;
                    idle = false;
                }
                else if (now - lastPacketTicks > idleAfterTicks)
                {
                    // The app isn't rendering audio at all: fill silence from the
                    // moment packets stopped so the muxer's audio timeline keeps up
                    // with video. Anchored at lastPacketTicks (not stream start) so
                    // the fill measures only this gap and can't drift.
                    if (!idle)
                    {
                        idle = true;
                        idleAnchorWritten = written;
                        idleAnchorTicks = lastPacketTicks;
                    }
                    long targetFrames = idleAnchorWritten + (now - idleAnchorTicks) * SampleRate / Stopwatch.Frequency;
                    if (targetFrames > written)
                    {
                        EmitSilence((targetFrames - written) * BytesPerFrame);
                        written = targetFrames;
                    }
                }

                Thread.Sleep(4);
            }
        }
        catch (Exception ex)
        {
            // HP-04 / REL-1: an unhandled capture error ends this thread cleanly
            // and logs it. The mux-liveness cap keeps video going without audio.
            Console.Error.WriteLine($"[audio] capture thread stopped on error ({ex.Message}) — video continues without audio.");
        }
        finally { RevertMmcss(mmcss); }
    }

    /// <summary>Completion-tolerant add: a late CompleteAdding during shutdown
    /// must not throw on the producer thread. Every producer add goes through here.</summary>
    private bool TryEnqueue((byte[] Buffer, int Length) item)
    {
        try { return _queue.TryAdd(item); }
        catch (InvalidOperationException) { return false; } // collection completed during shutdown
    }

    /// <summary>Accrue owed silence, bounded — freshness wins over preserving every byte.</summary>
    private void OweSilence(long bytes) => _overflowBytes = Math.Min(_overflowBytes + bytes, MaxOverflowBytes);

    // Repay at most ONE silence block, and only when the queue accepts it, so
    // silence-debt catch-up can never displace imminent fresh audio.
    private void RepayDebtBounded()
    {
        if (_overflowBytes <= 0) return;
        int chunk = (int)Math.Min(_overflowBytes, SilenceBlock.Length);
        if (TryEnqueue((SilenceBlock, chunk))) _overflowBytes -= chunk;
    }

    /// <summary>Hand samples to the writer queue; a stalled consumer costs audio
    /// content (replaced by silence later), never timeline bytes.</summary>
    private void Emit(byte[] buf, int len, ref bool overflowLogged)
    {
        // Freshness wins (review RB-01): queue the newest audio before any silence
        // repayment. Repaying debt ahead of fresh samples was what let a transient
        // encoder stall turn into permanent silence.
        if (TryEnqueue((buf, len))) { RepayDebtBounded(); return; }

        // Queue full => the encoder is behind. Discard the OLDEST queued block so we
        // resume near the live edge (a live stream must not replay a stale backlog and
        // then desync), admit the fresh block in its place, and owe the discarded span
        // back as bounded silence to hold the A/V byte-clock.
        if (_queue.TryTake(out var stale))
        {
            OweSilence(stale.Length);
            if (!TryEnqueue((buf, len))) OweSilence(len); // lost the freed slot to the writer
        }
        else if (!TryEnqueue((buf, len)))
        {
            OweSilence(len);
        }
        if (!overflowLogged)
        {
            overflowLogged = true;
            Console.Error.WriteLine("[audio] encoder is not draining audio — dropping stale audio to stay at the live edge.");
        }
    }

    private void EmitSilence(long bytes)
    {
        while (bytes > 0 && !_cts.IsCancellationRequested)
        {
            int chunk = (int)Math.Min(bytes, SilenceBlock.Length);
            if (!TryEnqueue((SilenceBlock, chunk))) { OweSilence(bytes); return; }
            bytes -= chunk;
        }
    }

    private void WriteLoop()
    {
        try
        {
            foreach (var (buf, len) in _queue.GetConsumingEnumerable())
            {
                _onSamples(buf, len);
                if (_cts.IsCancellationRequested) return;
            }
        }
        catch { /* consumer gone during shutdown */ }
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
        ActivationHandler? handler = null;
        IActivateAudioInterfaceAsyncOperation? op = null;
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);
            var propvariant = new PROPVARIANT
            {
                vt = 65, // VT_BLOB
                blobSize = (uint)size,
                blobData = paramsPtr,
            };

            handler = new ActivationHandler();
            var audioClientIid = typeof(IAudioClient).GUID;
            int hr = ActivateAudioInterfaceAsync(
                "VAD\\Process_Loopback", ref audioClientIid, ref propvariant, handler, out op);
            Marshal.ThrowExceptionForHR(hr);

            if (!handler.Done.WaitOne(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("ActivateAudioInterfaceAsync timed out");

            Marshal.ThrowExceptionForHR(op.GetActivateResult(out int activateHr, out object unk));
            Marshal.ThrowExceptionForHR(activateHr);
            return (IAudioClient)unk;
        }
        finally
        {
            // Release the wait-handle and the async-operation COM object too — the
            // op is a distinct COM object, so this can't affect the returned client.
            handler?.Done.Dispose();
            if (op is not null) { try { Marshal.FinalReleaseComObject(op); } catch { } }
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEvent Done = new(false);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op) => Done.Set();
    }

    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return; // session teardown guard may also call this
        _disposed = true;
        _cts.Cancel();
        // Stop the WASAPI client first so the capture thread's reads drain and it
        // exits promptly; only then complete the queue (producer is done — TryEnqueue
        // also tolerates a late completion as a belt) and join the writer.
        try { _client.Stop(); } catch { }
        _thread.Join(2000);
        _queue.CompleteAdding();
        _writerThread.Join(2000); // a writer blocked in the pipe is freed when the session disposes the pipe
        try { Marshal.FinalReleaseComObject(_capture); } catch { }
        try { Marshal.FinalReleaseComObject(_client); } catch { }
        _cts.Dispose();
    }

    // ---- MMCSS (realtime scheduling, best-effort) -----------------------

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);
    [DllImport("avrt.dll", SetLastError = true)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);

    private static IntPtr ConfigureMmcss()
    {
        try { uint idx = 0; return AvSetMmThreadCharacteristicsW("Pro Audio", ref idx); }
        catch { return IntPtr.Zero; }
    }
    private static void RevertMmcss(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        try { AvRevertMmThreadCharacteristics(handle); } catch { }
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
