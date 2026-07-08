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

    public int ViewerCount => _clients.Count;
    public long FragmentsSent;

    // Updated by the pacing loop each stats window; read by /api/stats.
    public volatile int SourceFps;   // compositor frames/sec (how fast the source actually changes)
    public volatile int DupPercent;  // % of encoded frames that were repeats (meaningful during motion only)
    public volatile string State = "starting"; // starting → live → stopped/failed
    public bool HasAudio { get; set; }

    public void SetInit(byte[] init, string codec)
    {
        _init = init;
        _codec = codec;
        Console.WriteLine($"[mp4] init segment ready ({init.Length} bytes), codec {codec}");
        _initReady.TrySetResult();
    }

    public void Broadcast(byte[] fragment, bool keyframe)
    {
        Interlocked.Increment(ref FragmentsSent);
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
                Console.WriteLine($"[ws] viewer {id:N} fell behind — resyncing at next keyframe");
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
        await _initReady.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);

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
        await socket.SendAsync(Encoding.UTF8.GetBytes(hello), WebSocketMessageType.Text, true, ct);
        await socket.SendAsync(Prefix(_init!), WebSocketMessageType.Binary, true, ct);

        var client = new Client
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

        // Drain incoming (close frames, pings); we never expect data from viewers.
        var receiveLoop = Task.Run(async () =>
        {
            var buf = new byte[1024];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buf, ct);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch { }
        }, ct);

        try
        {
            await foreach (var message in client.Queue.Reader.ReadAllAsync(ct))
            {
                if (socket.State != WebSocketState.Open) break;
                await socket.SendAsync(message, WebSocketMessageType.Binary, true, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            _clients.TryRemove(id, out _);
            Console.WriteLine($"[ws] viewer disconnected ({_clients.Count} total)");
            try { socket.Abort(); } catch { }
            await Task.WhenAny(receiveLoop, Task.Delay(1000, CancellationToken.None));
        }
    }
}
