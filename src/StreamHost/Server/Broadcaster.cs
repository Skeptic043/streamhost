using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace StreamHost.Server;

/// <summary>
/// Fans the fMP4 stream out to every connected viewer.
/// Wire protocol per client: text hello JSON, then binary messages of
/// [8-byte LE double: send time, Unix ms][payload]; first binary = init segment,
/// the rest are fragments starting at a keyframe. Slow clients get their queue
/// dumped and resync at the next keyframe instead of corrupting the stream.
/// </summary>
public sealed class Broadcaster
{
    private sealed class Client
    {
        public required WebSocket Socket;
        public required Channel<byte[]> Queue;
        public bool NeedsResync = true;
    }

    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly TaskCompletionSource _initReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[]? _init;
    private string _codec = "avc1.64002A";

    public int Width { get; init; }
    public int Height { get; init; }
    public int Fps { get; init; }
    public string StreamName { get; init; } = Environment.MachineName;

    /// <summary>Hard viewer cap. Extra connections get a clean protocol-level
    /// close instead of piling onto an upload-bound host. 0 = unlimited.</summary>
    public const int DefaultMaxViewers = 24;
    public int MaxViewers { get; init; } = DefaultMaxViewers;

    // Reserved slots = registered clients + in-flight handshakes still waiting on
    // the init segment. The cap is enforced against THIS (atomically), not the
    // looser _clients.Count, so a burst of concurrent handshakes can't overshoot.
    private int _reserved;

    public int ViewerCount => _clients.Count;
    public long FragmentsSent;
    private long _initReadyTicks;
    private long _lastFragmentTicks;
    internal long InitReadyTicks => Interlocked.Read(ref _initReadyTicks);
    internal long LastFragmentTicks => Interlocked.Read(ref _lastFragmentTicks);

    // Updated by the pacing loop each stats window; read by /api/stats.
    public volatile int SourceFps;   // compositor frames/sec (how fast the source actually changes)
    public volatile int DupPercent;  // % of encoded frames that were repeats (meaningful during motion only)
    public volatile string State = "starting"; // starting → live → stopped/failed
    public bool HasAudio { get; set; }

    /// <summary>Blocks until ffmpeg's header (init segment) has arrived — the
    /// earliest moment fragment production can possibly start.</summary>
    public bool WaitForInit(TimeSpan timeout, CancellationToken ct)
    {
        try { return _initReady.Task.Wait((int)timeout.TotalMilliseconds, ct); }
        catch (OperationCanceledException) { return false; }
    }

    public void SetInit(byte[] init, string codec)
    {
        _init = init;
        _codec = codec;
        Interlocked.Exchange(ref _initReadyTicks, System.Diagnostics.Stopwatch.GetTimestamp());
        Console.WriteLine($"[mp4] init segment ready ({init.Length} bytes), codec {codec}");
        _initReady.TrySetResult();
    }

    public void Broadcast(byte[] fragment, bool keyframe)
    {
        Interlocked.Increment(ref FragmentsSent);
        Interlocked.Exchange(ref _lastFragmentTicks, System.Diagnostics.Stopwatch.GetTimestamp());
        byte[] message = Prefix(fragment);
        foreach (var (id, client) in _clients)
        {
            if (client.NeedsResync)
            {
                if (!keyframe) continue;
                client.NeedsResync = false;
            }
            if (!client.Queue.Writer.TryWrite(message))
            {
                // Overloaded viewer: dump backlog, wait for the next keyframe.
                while (client.Queue.Reader.TryRead(out _)) { }
                client.NeedsResync = true;
                Console.WriteLine($"[ws] viewer {id:N} fell behind; resyncing at next keyframe");
            }
        }
    }

    private static byte[] Prefix(byte[] payload)
    {
        var message = new byte[8 + payload.Length];
        BitConverter.TryWriteBytes(message, (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        payload.CopyTo(message, 8);
        return message;
    }

    public async Task HandleClientAsync(WebSocket socket, CancellationToken ct)
    {
        var id = Guid.NewGuid();

        // Reserve a slot ATOMICALLY before any work — counting this in-flight
        // handshake even while it waits on the init segment. Interlocked makes
        // concurrent handshakes serialize, so a burst can't overshoot MaxViewers
        // the way the old non-atomic _clients.Count check could. Over the cap:
        // give the slot straight back and turn the viewer away with a clean
        // protocol-level close (never registered). Released in the finally on
        // every exit path. MaxViewers == 0 stays unlimited (no counting).
        bool reserved = false;
        if (MaxViewers > 0)
        {
            if (Interlocked.Increment(ref _reserved) > MaxViewers)
            {
                Interlocked.Decrement(ref _reserved);
                using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                closeCts.CancelAfter(TimeSpan.FromSeconds(2));
                try { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "stream is full (viewer limit reached)", closeCts.Token); } catch { }
                try { socket.Abort(); } catch { }
                Console.WriteLine($"[ws] viewer rejected; at capacity ({MaxViewers})");
                return;
            }
            reserved = true;
        }

        // One linked source drives every await below (init wait, hello/init
        // sends, receive loop, send loop). The receive loop cancels it on peer
        // close, which is what wakes the send loop even on a static stream where
        // no fragments flow to fail a send.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = linkedCts.Token;

        Client? client = null;
        Task? receiveLoop = null;
        try
        {
            await _initReady.Task.WaitAsync(TimeSpan.FromSeconds(15), token);

            var hello = JsonSerializer.Serialize(new
            {
                type = "hello",
                codec = _codec,
                width = Width,
                height = Height,
                fps = Fps,
                name = StreamName,
                audio = HasAudio,
            });
            await socket.SendAsync(Encoding.UTF8.GetBytes(hello), WebSocketMessageType.Text, true, token);
            await socket.SendAsync(Prefix(_init!), WebSocketMessageType.Binary, true, token);

            client = new Client
            {
                Socket = socket,
                // FullMode must be Wait: it makes the non-blocking TryWrite return
                // FALSE when full, which is our overload signal. (DropWrite would
                // return true and silently discard — corrupting the stream instead
                // of triggering the keyframe resync.)
                Queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(180)
                {
                    SingleReader = true,
                    FullMode = BoundedChannelFullMode.Wait,
                }),
            };
            _clients[id] = client;
            Console.WriteLine($"[ws] viewer connected ({_clients.Count} total)");

            // Drain incoming (close frames, pings); we never expect data from
            // viewers. When this loop ends — peer closed or errored — its finally
            // cancels the linked token so the send loop unblocks immediately, not
            // only when the next fragment fails to send.
            receiveLoop = Task.Run(async () =>
            {
                var buf = new byte[1024];
                try
                {
                    while (socket.State == WebSocketState.Open)
                    {
                        var result = await socket.ReceiveAsync(buf, token);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                    }
                }
                catch { }
                finally { try { linkedCts.Cancel(); } catch { } }
            }, token);

            await foreach (var message in client.Queue.Reader.ReadAllAsync(token))
            {
                if (socket.State != WebSocketState.Open) break;
                await socket.SendAsync(message, WebSocketMessageType.Binary, true, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            // Idempotent: also wakes the receive loop if the send loop is the one
            // that ended (e.g. a socket error), and is guarded in case the CTS is
            // already cancelled/disposed.
            try { linkedCts.Cancel(); } catch { }
            if (client is not null)
            {
                _clients.TryRemove(id, out _);
                client.Queue.Writer.TryComplete(); // nothing else can queue onto a dead client
                Console.WriteLine($"[ws] viewer disconnected ({_clients.Count} total)");
            }
            try { socket.Abort(); } catch { }
            if (receiveLoop is not null)
                await Task.WhenAny(receiveLoop, Task.Delay(1000, CancellationToken.None));
            if (reserved) Interlocked.Decrement(ref _reserved); // exactly one release per reservation
        }
    }
}
