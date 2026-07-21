using System.Collections.Concurrent;
using System.Diagnostics;

namespace Spectari.Encode;

internal readonly record struct VideoInputWriterProgress(
    long FramesEnqueued,
    long WritesStarted,
    long WritesCompleted,
    long LastEnqueueTicks,
    long LastWriteStartedTicks,
    long LastWriteCompletedTicks,
    bool WriteInProgress,
    bool Failed);

internal interface IVideoInputWriter : IDisposable
{
    bool Failed { get; }
    VideoInputWriterProgress GetProgressSnapshot();
}

/// <summary>
/// Decouples the sampling grid from ffmpeg's stdin pipe: the sampler rents a
/// buffer, fills it, enqueues it; a dedicated thread does the blocking pipe
/// writes. Transient pipe stalls no longer skew sample timing. Sustained
/// encoder overload surfaces as back-pressure on RentBuffer (all buffers busy).
/// </summary>
public sealed class FrameWriter : IVideoInputWriter
{
    private readonly BlockingCollection<byte[]> _free = new();
    private readonly BlockingCollection<byte[]> _pending = new(boundedCapacity: 3);
    private readonly Thread _thread;
    private readonly FfmpegEncoder _encoder;
    private volatile bool _failed;
    private long _framesEnqueued;
    private long _writesStarted;
    private long _writesCompleted;
    private long _lastEnqueueTicks;
    private long _lastWriteStartedTicks;
    private long _lastWriteCompletedTicks;
    private int _writeInProgress;

    public bool Failed => _failed;

    public FrameWriter(FfmpegEncoder encoder, int frameBytes, int bufferCount = 4)
    {
        _encoder = encoder;
        for (int i = 0; i < bufferCount; i++) _free.Add(new byte[frameBytes]);
        _thread = new Thread(WriteLoop) { IsBackground = true, Name = "ffmpeg-writer" };
        _thread.Start();
    }

    /// <summary>Blocks when the encoder is behind - that back-pressure is intentional.</summary>
    public byte[] RentBuffer(CancellationToken ct) => _free.Take(ct);

    public void Enqueue(byte[] buffer)
    {
        _pending.Add(buffer);
        Interlocked.Increment(ref _framesEnqueued);
        Interlocked.Exchange(ref _lastEnqueueTicks, Stopwatch.GetTimestamp());
    }

    public void ReturnUnused(byte[] buffer) => _free.Add(buffer);

    internal VideoInputWriterProgress GetProgressSnapshot() => new(
        Interlocked.Read(ref _framesEnqueued),
        Interlocked.Read(ref _writesStarted),
        Interlocked.Read(ref _writesCompleted),
        Interlocked.Read(ref _lastEnqueueTicks),
        Interlocked.Read(ref _lastWriteStartedTicks),
        Interlocked.Read(ref _lastWriteCompletedTicks),
        Volatile.Read(ref _writeInProgress) != 0,
        _failed);

    VideoInputWriterProgress IVideoInputWriter.GetProgressSnapshot() =>
        GetProgressSnapshot();

    private void WriteLoop()
    {
        try
        {
            foreach (var buffer in _pending.GetConsumingEnumerable())
            {
                Interlocked.Increment(ref _writesStarted);
                Interlocked.Exchange(ref _lastWriteStartedTicks, Stopwatch.GetTimestamp());
                Volatile.Write(ref _writeInProgress, 1);
                try
                {
                    _encoder.WriteFrame(buffer, buffer.Length);
                    Interlocked.Increment(ref _writesCompleted);
                    Interlocked.Exchange(ref _lastWriteCompletedTicks, Stopwatch.GetTimestamp());
                }
                finally
                {
                    Volatile.Write(ref _writeInProgress, 0);
                }
                _free.Add(buffer);
            }
        }
        catch (Exception)
        {
            _failed = true; // ffmpeg died or pipe closed; Program notices via Failed/HasExited
        }
    }

    public void Dispose()
    {
        _pending.CompleteAdding();
        _thread.Join(2000);
    }
}

/// <summary>Writes complete H.264 access units to ffmpeg without blocking pacing.</summary>
internal sealed class AccessUnitWriter : IVideoInputWriter, IEncodedAccessUnitSink
{
    private readonly BlockingCollection<byte[]> _pending = new(boundedCapacity: 8);
    private readonly Thread _thread;
    private readonly FfmpegEncoder _encoder;
    private volatile bool _failed;
    private long _unitsEnqueued;
    private long _writesStarted;
    private long _writesCompleted;
    private long _lastEnqueueTicks;
    private long _lastWriteStartedTicks;
    private long _lastWriteCompletedTicks;
    private int _writeInProgress;

    internal AccessUnitWriter(FfmpegEncoder encoder)
    {
        _encoder = encoder;
        _thread = new Thread(WriteLoop) { IsBackground = true, Name = "ffmpeg-h264-writer" };
        _thread.Start();
    }

    public bool Failed => _failed;

    public void Write(IReadOnlyList<EncodedAccessUnit> accessUnits)
    {
        foreach (EncodedAccessUnit accessUnit in accessUnits)
        {
            _pending.Add(accessUnit.Data.ToArray());
            Interlocked.Increment(ref _unitsEnqueued);
            Interlocked.Exchange(ref _lastEnqueueTicks, Stopwatch.GetTimestamp());
        }
    }

    public VideoInputWriterProgress GetProgressSnapshot() => new(
        Interlocked.Read(ref _unitsEnqueued),
        Interlocked.Read(ref _writesStarted),
        Interlocked.Read(ref _writesCompleted),
        Interlocked.Read(ref _lastEnqueueTicks),
        Interlocked.Read(ref _lastWriteStartedTicks),
        Interlocked.Read(ref _lastWriteCompletedTicks),
        Volatile.Read(ref _writeInProgress) != 0,
        _failed);

    private void WriteLoop()
    {
        try
        {
            foreach (byte[] accessUnit in _pending.GetConsumingEnumerable())
            {
                Interlocked.Increment(ref _writesStarted);
                Interlocked.Exchange(ref _lastWriteStartedTicks, Stopwatch.GetTimestamp());
                Volatile.Write(ref _writeInProgress, 1);
                try
                {
                    _encoder.WritePacket(accessUnit);
                    Interlocked.Increment(ref _writesCompleted);
                    Interlocked.Exchange(ref _lastWriteCompletedTicks, Stopwatch.GetTimestamp());
                }
                finally
                {
                    Volatile.Write(ref _writeInProgress, 0);
                }
            }
        }
        catch (Exception ex)
        {
            _failed = true;
            Console.Error.WriteLine($"[ffmpeg-input] H.264 access-unit write failed: {ex.Message.Replace('\r', ' ').Replace('\n', ' ')}");
        }
    }

    public void Dispose()
    {
        _pending.CompleteAdding();
        _thread.Join(2000);
    }
}
