using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Spectari.Audio;

/// <summary>
/// Captures the audio of one process tree (the game) via WASAPI process
/// loopback (Win10 2004+, the OBS "Application Audio Capture" technique).
/// Output: 48 kHz stereo float32 interleaved blocks via the Read callback.
///
/// Timeline discipline: real packets (even silent-flagged ones) carry the
/// clock - WASAPI loopback delivers them at the render rate, so as long as the
/// app keeps an audio stream open the byte count tracks real time on its own.
/// Only when the app stops rendering entirely (no packets at all for &gt;150 ms)
/// does a wall-clock fill, anchored at the moment packets stopped, advance the
/// timeline with silence. That keeps the mp4 muxer moving: it interleaves by
/// timestamp and would otherwise sit on finished video fragments waiting for
/// audio that never comes (the header-then-no-video stall). We do NOT try to
/// reconcile against WASAPI device positions - for a quiet process those
/// positions stall while the wall clock keeps ticking, so trusting them just
/// fought the idle fill and produced choppy audio.
///
/// The WASAPI drain loop never blocks on the consumer: samples go through a
/// bounded queue to a writer thread. On overflow (encoder briefly not draining)
/// the OLDEST queued block is dropped so playback resumes near the live edge,
/// and the dropped span's DURATION is owed back as silence - ffmpeg gets raw
/// float with no timestamps, so replaying a matching length of silence is the
/// only way to keep A/V in sync. Owed silence is repaid as a CONTIGUOUS gap
/// into free queue room the moment the stall clears (a hitch plays as a brief
/// silence then live audio), and is bounded to ~2 s so a long stall leaves a
/// small residual instead of dumping an unbounded run of silence. The clock
/// counts only DELIVERED frames (real packets plus silence actually enqueued);
/// a dropped packet advances nothing on its own, so the timeline can neither
/// run permanently short (desynced) nor, once a transient stall clears, stay
/// silent. A starved drain loop injecting silence into otherwise-fine audio was
/// the original choppiness; the highest-priority thread (MMCSS Pro Audio when
/// available) plus the non-blocking queue removes that failure mode.
/// Discord (a different process tree) is excluded by construction.
/// </summary>
public sealed class ProcessAudioCapture : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    private const int BytesPerFrame = Channels * 4;

    private static readonly byte[] SilenceBlock = new byte[SampleRate * BytesPerFrame / 100]; // 10 ms
    private const int MaxInitialLeadInSeconds = 5;
    internal const int LeadInBiasMilliseconds = 100;
    // Field-tunable ceiling on how much silence we will inject to resync after a
    // stall (~2 s). Beyond it the excess dropped duration is accepted as a bounded
    // residual rather than dumping a huge run of silence to catch up.
    private const long MaxCatchupBytes = SampleRate * BytesPerFrame * 2; // ~2 s

    private readonly IAudioClient _client;
    private readonly IAudioCaptureClient _capture;
    private readonly Thread _thread;
    private readonly Thread _writerThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<byte[], int> _onSamples;

    // Capture thread → writer thread. ~10 ms per item ≈ 2.5 s of headroom.
    private readonly BlockingCollection<(byte[] Buffer, int Length)> _queue = new(boundedCapacity: 256);
    // Both touched only on the capture thread (Emit / RepayOwedSilence / idle fill).
    private long _owedSilenceBytes; // dropped audio duration owed back as silence, bounded by MaxCatchupBytes
    // The stream clock: frames that reached AND stayed in the writer path (real +
    // silence). A block removed from the queue before the writer saw it is
    // uncounted here and owed back as silence, so a drop advances nothing net.
    private long _deliveredFrames;

    public ProcessAudioCapture(uint targetPid, long videoEpochTicks, Action<byte[], int> onSamples)
    {
        _onSamples = onSamples;
        _client = ActivateProcessLoopback(targetPid);

        // REL-2: everything past activation is transactional. If format setup,
        // Initialize, GetService, or Start throws, release the client before
        // rethrowing - otherwise a half-built capture leaks it and its threads
        // never start to be disposed.
        bool clientStarted = false;
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

            long captureStartTicks = Stopwatch.GetTimestamp();
            Marshal.ThrowExceptionForHR(_client.Start());
            clientStarted = true;

            // This direct prefix aligns ffmpeg's zero-based audio input with the
            // first video enqueue. It deliberately bypasses owed-silence catchup:
            // that bounded machinery is only for drops after capture has started.
            long leadInFrames = GetLeadInFrames(videoEpochTicks, captureStartTicks);
            int leadInBytes = checked((int)(leadInFrames * BytesPerFrame));
            if (leadInBytes > 0)
            {
                _queue.Add((new byte[leadInBytes], leadInBytes));
                _deliveredFrames += leadInFrames;
            }
            Console.WriteLine(FormatLeadInLog(leadInFrames));
        }
        catch
        {
            // REL-2: release both COM objects we may have acquired before the throw.
            // _capture is a readonly field - it reads null unless GetService ran, so
            // the null check keeps the pre-GetService failure path safe.
            if (clientStarted) { try { _client.Stop(); } catch { } }
            try { if (_capture is not null) Marshal.FinalReleaseComObject(_capture); } catch { }
            try { Marshal.FinalReleaseComObject(_client); } catch { }
            throw;
        }

        _writerThread = new Thread(WriteLoop) { IsBackground = true, Name = "audio-writer", Priority = ThreadPriority.AboveNormal };
        _writerThread.Start();
        // Highest: on a machine pegged by the game, a starved drain loop is
        // exactly what used to inject spurious silence into the stream.
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "audio-capture", Priority = ThreadPriority.Highest };
        _thread.Start();
        // The "capture started" line is logged from CaptureLoop after MMCSS setup so
        // it can report truthfully whether Pro Audio scheduling was actually granted.
    }

    internal static long GetLeadInFrames(long videoEpochTicks, long captureStartTicks)
    {
        long maximumTicks = Stopwatch.Frequency * MaxInitialLeadInSeconds;
        long biasTicks = Stopwatch.Frequency * LeadInBiasMilliseconds / 1000;
        long elapsedTicks = Math.Clamp(
            captureStartTicks - videoEpochTicks,
            0,
            maximumTicks);
        long biasedTicks = Math.Min(maximumTicks, elapsedTicks + biasTicks);
        return (biasedTicks * SampleRate + Stopwatch.Frequency / 2) /
            Stopwatch.Frequency;
    }

    internal static string FormatLeadInLog(long leadInFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(leadInFrames);
        long leadInMs = (leadInFrames * 1000 + SampleRate / 2) / SampleRate;
        return $"[audio] aligned to video timeline (+{leadInMs} ms lead-in silence, includes {LeadInBiasMilliseconds} ms safety bias)";
    }

    private void CaptureLoop()
    {
        // REL-3: MMCSS "Pro Audio" is the OS's realtime-audio scheduling class;
        // fully guarded (Part 4), so any failure silently keeps ThreadPriority.Highest.
        IntPtr mmcss = ConfigureMmcss();
        // REL-1 / honest log: one line so a live session shows audio came up, worded
        // by whether MMCSS actually handed us a Pro Audio handle (else we kept
        // ThreadPriority.Highest). Logged here - the ctor can't know the outcome.
        Console.WriteLine($"[audio] capture started (process loopback, 48 kHz stereo{(mmcss != IntPtr.Zero ? ", MMCSS Pro Audio" : ", thread priority Highest")})");
        try
        {
            bool overflowLogged = false, errorLogged = false;

            long lastPacketTicks = Stopwatch.GetTimestamp();
            long idleAfterTicks = Stopwatch.Frequency * 150 / 1000; // no packets at all for 150 ms = app stopped rendering
            bool idle = false;
            long idleAnchorFrames = 0, idleAnchorTicks = 0;

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
                            Console.Error.WriteLine($"[audio] capture read failed (0x{hr:X8}); stream continues with silence.");
                        }
                        break;
                    }

                    if (_capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _) < 0) break;
                    // HP-04: ReleaseBuffer must run even if alloc/Marshal.Copy/Emit
                    // throws, or the WASAPI buffer leaks and capture wedges.
                    try
                    {
                        got = true;

                        // Emit every real packet, silent-flagged or not - a silent packet
                        // is real render time and keeps the clock advancing without any
                        // idle fill. Copy only when it carries actual samples.
                        int bytes = (int)frames * BytesPerFrame;
                        var buf = new byte[bytes];
                        const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
                        if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0)
                            Marshal.Copy(data, buf, 0, bytes);
                        // Delivery accounting lives entirely in Emit / RepayOwedSilence /
                        // the idle fill now (I2): a DROPPED packet must not advance the clock.
                        Emit(buf, bytes, ref overflowLogged);
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
                        // Anchor at the REAL-TIME position (delivered + still-owed), not
                        // just delivered, so this fill also pays off any silence owed from
                        // a pre-idle overflow instead of leaving the timeline short.
                        idleAnchorFrames = _deliveredFrames + _owedSilenceBytes / BytesPerFrame;
                        idleAnchorTicks = lastPacketTicks;
                    }
                    long targetFrames = idleAnchorFrames + (now - idleAnchorTicks) * SampleRate / Stopwatch.Frequency;
                    if (targetFrames > _deliveredFrames)
                        EmitSilence((targetFrames - _deliveredFrames) * BytesPerFrame); // advances _deliveredFrames
                    if (_deliveredFrames >= targetFrames)
                        // Caught up to real time: the owed gap is now covered by this fill,
                        // so clear it rather than let RepayOwedSilence pay the same deficit
                        // again when fresh audio resumes (no double-pay, no desync).
                        _owedSilenceBytes = 0;
                }

                Thread.Sleep(4);
            }
        }
        catch (Exception ex)
        {
            // HP-04 / REL-1: an unhandled capture error ends this thread cleanly
            // and logs it. The mux-liveness cap keeps video going without audio.
            Console.Error.WriteLine($"[audio] capture thread stopped on error ({ex.Message}); video continues without audio.");
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

    /// <summary>Accrue owed silence, bounded by MaxCatchupBytes - beyond the ceiling
    /// the excess dropped duration is accepted as a bounded residual.</summary>
    private void OweSilence(long bytes) => _owedSilenceBytes = Math.Min(_owedSilenceBytes + bytes, MaxCatchupBytes);

    // Drain owed silence into the queue as a CONTIGUOUS run - as much as fits right
    // now, not one block per call - so a cleared hitch plays as a short clean gap
    // then live audio. Non-blocking: when the queue is full nothing is injected, so
    // fresh audio still wins during an active overflow. Each block enqueued advances
    // the clock (I2: silence counts only once it is actually delivered).
    private void RepayOwedSilence()
    {
        while (_owedSilenceBytes > 0)
        {
            int chunk = (int)Math.Min(_owedSilenceBytes, SilenceBlock.Length);
            if (!TryEnqueue((SilenceBlock, chunk))) break;
            _owedSilenceBytes -= chunk;
            _deliveredFrames += chunk / BytesPerFrame;
        }
    }

    /// <summary>Hand samples to the writer queue (review RB-01). Fresh ALWAYS lands
    /// first (drop-oldest when the queue is full), so a sustained slowdown keeps fresh
    /// flowing and can never become permanent silence; a dropped or removed block
    /// advances the clock by nothing net (the removed oldest is uncounted from
    /// _deliveredFrames) - its duration is owed as silence. Owed silence is then repaid into
    /// whatever room REMAINS: a no-op while the queue stays full (owed accrues, bounded),
    /// a contiguous gap once the stall clears (resync). So a transient stall becomes a
    /// brief silence gap then live audio, never permanent silence, never desync.</summary>
    private void Emit(byte[] buf, int len, ref bool overflowLogged)
    {
        // Fresh always reaches the queue (never permanent silence): enqueue it, or if the
        // queue is full drop the OLDEST for the live edge and owe its duration as silence.
        if (TryEnqueue((buf, len)))
        {
            _deliveredFrames += len / BytesPerFrame;
        }
        else
        {
            if (_queue.TryTake(out var stale))
            {
                // TryTake only removes a block the writer never consumed. It was
                // counted into _deliveredFrames at its own enqueue, so uncount it
                // here before owing its duration back as silence - otherwise the
                // clock would count it twice (once now, once when the silence repays).
                _deliveredFrames -= stale.Length / BytesPerFrame;
                OweSilence(stale.Length);
            }
            if (TryEnqueue((buf, len))) _deliveredFrames += len / BytesPerFrame;
            else OweSilence(len); // writer took the freed slot first
            if (!overflowLogged)
            {
                overflowLogged = true;
                Console.Error.WriteLine("[audio] encoder is not draining audio; dropping stale audio to stay at the live edge; backfilling silence to resync.");
            }
        }

        // Pay the owed gap into whatever room REMAINS after the fresh block: a no-op while
        // the queue stays full (a sustained slowdown keeps fresh flowing, owed accrues
        // bounded), a contiguous silence gap once the stall clears (resync). The gap lands
        // just after the current block instead of just before it; at 10 ms granularity that
        // is inaudible and the byte-clock still resyncs.
        RepayOwedSilence();
    }

    // Idle-fill silence: routes through the enqueue path and advances the clock per
    // block (I2). If the queue is full it owes the remainder rather than blocking.
    private void EmitSilence(long bytes)
    {
        while (bytes > 0 && !_cts.IsCancellationRequested)
        {
            int chunk = (int)Math.Min(bytes, SilenceBlock.Length);
            if (!TryEnqueue((SilenceBlock, chunk))) { OweSilence(bytes); return; }
            _deliveredFrames += chunk / BytesPerFrame;
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
                // Timed out: a late ActivateCompleted may still fire and touch Done, so
                // we do NOT dispose it here - leave it for GC (Set() is disposed-tolerant
                // as a further belt).
                throw new TimeoutException("ActivateAudioInterfaceAsync timed out");

            Marshal.ThrowExceptionForHR(op.GetActivateResult(out int activateHr, out object unk));
            Marshal.ThrowExceptionForHR(activateHr);
            // Activation completed: the callback has already fired and won't touch Done
            // again, so the wait-handle is now safe to dispose.
            handler.Done.Dispose();
            return (IAudioClient)unk;
        }
        finally
        {
            // Release the async-operation COM object (distinct from the returned client)
            // and the activation-params buffer regardless of outcome.
            if (op is not null) { try { Marshal.FinalReleaseComObject(op); } catch { } }
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEvent Done = new(false);
        // A late callback can arrive after an activation timeout; if Done was disposed
        // by then, the set must not throw back into COM.
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try { Done.Set(); } catch (ObjectDisposedException) { }
        }
    }

    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return; // session teardown guard may also call this
        _disposed = true;
        _cts.Cancel();
        // Stop the WASAPI client first so the capture thread's reads drain and it
        // exits promptly; only then complete the queue (producer is done - TryEnqueue
        // also tolerates a late completion as a belt) and join the writer.
        try { _client.Stop(); } catch { }
        // Gate the COM release on the capture thread ACTUALLY having stopped: releasing
        // _capture/_client while a live capture thread is still calling into them is a
        // use-after-free. If the thread is wedged, skip the release - a leaked COM object
        // on a stuck thread is the lesser evil.
        bool captureStopped = _thread.Join(2000);
        _queue.CompleteAdding();
        _writerThread.Join(2000); // writer uses only _onSamples, so its join doesn't gate the COM release
        if (captureStopped)
        {
            try { Marshal.FinalReleaseComObject(_capture); } catch { }
            try { Marshal.FinalReleaseComObject(_client); } catch { }
        }
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
