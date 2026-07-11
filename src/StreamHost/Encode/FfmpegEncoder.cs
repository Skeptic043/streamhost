using System.Diagnostics;

namespace StreamHost.Encode;

/// <summary>
/// Drives ffmpeg as a child process: raw BGRA frames in on stdin, low-latency
/// fragmented MP4 (H.264) out on stdout. One fragment per frame via -frag_duration.
/// </summary>
public sealed class FfmpegEncoder : IDisposable
{
    private readonly Process _process;
    private readonly Stream _stdin;

    public Stream Output => _process.StandardOutput.BaseStream;
    public string EncoderName { get; }

    /// <summary>Bundled ffmpeg.exe next to our exe wins; PATH is the fallback.</summary>
    public static string FfmpegPath { get; } =
        File.Exists(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"))
            ? Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")
            : "ffmpeg";

    public FfmpegEncoder(
        int inWidth, int inHeight, int fps, int bitrateKbps,
        int outWidth, int outHeight, string encoder, string? audioPipeName = null,
        int fragMs = 0)
    {
        EncoderName = encoder;
        int gop = Math.Max(fps / 2, 1);              // keyframe every 0.5 s => fast late-join/resync
        long fragUs = fragMs > 0 ? fragMs * 1000L    // batched fragments (fewer, larger MSE appends)
                                 : 1_000_000L / fps; // default: one fragment per frame

        string encoderOpts = EncoderOpts(encoder);

        string scale = (outWidth != inWidth || outHeight != inHeight)
            ? $"-vf scale={outWidth}:{outHeight}:flags=bilinear "
            : "";

        string audioIn = audioPipeName is not null
            ? $"-thread_queue_size 512 -f f32le -ar {Audio.ProcessAudioCapture.SampleRate} -ac {Audio.ProcessAudioCapture.Channels} -i \\\\.\\pipe\\{audioPipeName} "
            : "";
        string audioOut = audioPipeName is not null
            ? "-map 0:v -map 1:a -c:a aac -b:a 160k "
            : "-an ";

        // -max_interleave_delta: the muxer's default is to hold finished video
        // packets up to 10 SECONDS waiting for the other stream's timestamps to
        // catch up. A stalled audio feed therefore froze the entire fragment
        // output (indistinguishable from an encoder stall). Half a second keeps
        // interleaving tight in the healthy case and force-flushes video-only
        // fragments when audio starves — a degraded stream instead of no stream.
        string args =
            $"-hide_banner -loglevel warning " +
            $"-thread_queue_size 128 -f rawvideo -pixel_format bgra -video_size {inWidth}x{inHeight} -framerate {fps} -i pipe:0 " +
            audioIn +
            scale +
            $"{audioOut}-c:v {encoder} {encoderOpts} " +
            $"-b:v {bitrateKbps}k -maxrate {bitrateKbps * 5 / 4}k -bufsize {bitrateKbps / 2}k " +
            $"-g {gop} -bf 0 -pix_fmt yuv420p " +
            $"-f mp4 -movflags +empty_moov+default_base_moof -frag_duration {fragUs} " +
            $"-max_interleave_delta 500000 " +
            $"-flush_packets 1 pipe:1";

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = FfmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Console.WriteLine($"[ffmpeg] {e.Data}");
        };
        Console.WriteLine($"[encoder] ffmpeg {args}");
        _process.Start();
        Util.ChildJob.Adopt(_process); // dies with us, even on a force-quit
        _process.BeginErrorReadLine();
        _stdin = _process.StandardInput.BaseStream;
    }

    /// <summary>Per-encoder options, shared by the real encode AND the startup probe
    /// so a probe pass is meaningful for the config that actually runs (the v0.9-v0.10
    /// AMD miss: the probe passed on default options while the live encode stalled on
    /// its low-latency ones). AMF deliberately has NO -usage lowlatency: that
    /// submission path wedges on some driver/hardware combos (RX 9070 XT field case —
    /// header written, then zero output) while the default transcoding usage is the
    /// path every driver release actually gets tested on. With -bf 0 and a 0.5 s GOP
    /// the latency difference is tens of milliseconds; robustness wins.</summary>
    public static string EncoderOpts(string encoder) => encoder switch
    {
        "h264_nvenc" => "-preset p4 -tune ull -rc cbr -multipass 0 -profile:v high",
        "h264_amf"   => "-quality speed -rc cbr -profile:v high",
        "h264_qsv"   => "-preset veryfast -profile:v high",
        _            => "-preset veryfast -tune zerolatency -profile:v high",
    };

    public bool HasExited => _process.HasExited;
    public int ExitCode { get { try { return _process.HasExited ? _process.ExitCode : 0; } catch { return -1; } } }

    /// <summary>Blocking write of one raw frame; back-pressure comes from the pipe.</summary>
    public void WriteFrame(byte[] frame, int length)
    {
        _stdin.Write(frame, 0, length);
    }

    /// <summary>Picks the hardware encoder for this GPU vendor, then PROVES it can
    /// actually encode (drivers lie; being compiled into ffmpeg proves nothing).
    /// Falls back to CPU x264 so the stream starts on any machine.</summary>
    public static string PickEncoder(uint gpuVendorId, string? requested)
    {
        if (!string.IsNullOrEmpty(requested) && requested != "auto") return requested;

        string preferred = gpuVendorId switch
        {
            0x10DE => "h264_nvenc",
            0x1002 => "h264_amf",
            0x8086 => "h264_qsv",
            _ => "libx264",
        };

        if (preferred == "libx264") return preferred;

        // Cache a PASSED probe per GPU so startup skips the 1-2s self-test.
        // Failures are deliberately not cached — a driver hiccup shouldn't
        // condemn the machine to CPU encoding forever.
        string cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamHost", "encoder.cache");
        // Cache token version: bump when the probe changes so stale "passed"
        // verdicts (e.g. an AMD card that cleared the old trivial 320x240 test
        // but stalls on real content) get re-evaluated.
        // v3: probe now uses the real encoder options (EncoderOpts) and AMF
        // dropped -usage lowlatency — every machine re-probes the new config.
        string token = $"v3:{gpuVendorId:X}:{preferred}";
        try
        {
            if (File.Exists(cachePath) && File.ReadAllText(cachePath).Trim() == token)
                return preferred;
        }
        catch { }

        if (!ProbeEncoder(preferred))
        {
            Console.WriteLine($"[encoder] {preferred} failed its self-test — falling back to CPU (libx264)");
            return "libx264";
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllText(cachePath, token);
        }
        catch { }
        return preferred;
    }

    /// <summary>Encodes a second of realistic 1080p60 motion with the SAME encoder
    /// options as a live stream. A hardware encoder that can't sustain this config
    /// (some AMD AMF driver combos write the header then stall) fails or hangs here,
    /// so we catch it before it dead-ends a live stream. Probing a different config
    /// than we run proved worthless in the field — the v0.9 probe passed on defaults
    /// while the real low-latency options stalled.</summary>
    private static bool ProbeEncoder(string encoder)
    {
        try
        {
            var psi = new ProcessStartInfo(FfmpegPath,
                $"-hide_banner -loglevel error -f lavfi -i testsrc=size=1920x1080:rate=60 -t 1 " +
                $"-c:v {encoder} {EncoderOpts(encoder)} -b:v 8000k -maxrate 10000k -bufsize 4000k " +
                $"-g 30 -bf 0 -pix_fmt yuv420p -f null -")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            Util.ChildJob.Adopt(p);
            // A stall shows up as a hang: WaitForExit times out, we kill it, it fails.
            if (!p.WaitForExit(12000)) { try { p.Kill(entireProcessTree: true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[encoder] could not run ffmpeg ({ex.Message}) — is ffmpeg.exe next to StreamHost.exe?");
            return false;
        }
    }

    public void Dispose()
    {
        try { _stdin.Close(); } catch { }
        try
        {
            if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true);
        }
        catch { }
        _process.Dispose();
    }
}
