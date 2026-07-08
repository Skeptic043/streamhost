using System.Collections.Concurrent;

namespace StreamHost.Encode;

/// <summary>
/// Decouples the sampling grid from ffmpeg's stdin pipe: the sampler rents a
/// buffer, fills it, enqueues it; a dedicated thread does the blocking pipe
/// writes. Transient pipe stalls no longer skew sample timing. Sustained
/// encoder overload surfaces as back-pressure on RentBuffer (all buffers busy).
/// </summary>
public sealed class FrameWriter : IDisposable
{
    private readonly BlockingCollection<byte[]> _free = new();
    private readonly BlockingCollection<byte[]> _pending = new(boundedCapacity: 3);
    private readonly Thread _thread;
    private readonly FfmpegEncoder _encoder;
    private volatile bool _failed;

    public bool Failed => _failed;

    public FrameWriter(FfmpegEncoder encoder, int frameBytes, int bufferCount = 4)
    {
        _encoder = encoder;
        for (int i = 0; i < bufferCount; i++) _free.Add(new byte[frameBytes]);
        _thread = new Thread(WriteLoop) { IsBackground = true, Name = "ffmpeg-writer" };
        _thread.Start();
    }

    /// <summary>Blocks when the encoder is behind — that back-pressure is intentional.</summary>
    public byte[] RentBuffer(CancellationToken ct) => _free.Take(ct);

    public void Enqueue(byte[] buffer) => _pending.Add(buffer);

    public void ReturnUnused(byte[] buffer) => _free.Add(buffer);

    private void WriteLoop()
    {
        try
        {
            foreach (var buffer in _pending.GetConsumingEnumerable())
            {
                _encoder.WriteFrame(buffer, buffer.Length);
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
