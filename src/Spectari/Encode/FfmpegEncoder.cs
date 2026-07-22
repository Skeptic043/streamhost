using System.Diagnostics;

namespace Spectari.Encode;

internal enum FfmpegVideoInput
{
    RawBgra,
    H264AnnexB,
}

/// <summary>
/// Drives ffmpeg as a child process: video in on stdin, low-latency
/// fragmented MP4 (H.264) out on stdout. One fragment per frame via -frag_duration.
/// </summary>
public sealed class FfmpegEncoder : IDisposable
{
    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly object _lifecycleGate = new();
    private bool _disposed;
    private int _terminationConfirmed;

    public Stream Output => _process.StandardOutput.BaseStream;
    public string EncoderName { get; }
    internal bool CopiesH264Video { get; }

    /// <summary>Bundled ffmpeg.exe next to our exe wins; PATH is the fallback.</summary>
    public static string FfmpegPath { get; } =
        File.Exists(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"))
            ? Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")
            : "ffmpeg";

    public FfmpegEncoder(
        int inWidth, int inHeight, int fps, int bitrateKbps,
        int outWidth, int outHeight, string encoder, string? audioPipeName = null,
        int fragMs = 0)
        : this(
            inWidth, inHeight, fps, bitrateKbps, outWidth, outHeight,
            encoder, audioPipeName, fragMs, FfmpegVideoInput.RawBgra)
    {
    }

    internal FfmpegEncoder(
        int inWidth, int inHeight, int fps, int bitrateKbps,
        int outWidth, int outHeight, string encoder, string? audioPipeName,
        int fragMs,
        FfmpegVideoInput videoInput)
    {
        LogBuildInfoOnce(); // first stream start records the exact bundled ffmpeg to the log
        CopiesH264Video = videoInput == FfmpegVideoInput.H264AnnexB;
        EncoderName = CopiesH264Video ? "Media Foundation H.264" : encoder;
        string args = BuildArguments(
            inWidth,
            inHeight,
            fps,
            bitrateKbps,
            outWidth,
            outHeight,
            encoder,
            audioPipeName,
            fragMs,
            videoInput);

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

    internal static string BuildArguments(
        int inWidth,
        int inHeight,
        int fps,
        int bitrateKbps,
        int outWidth,
        int outHeight,
        string encoder,
        string? audioPipeName,
        int fragMs,
        FfmpegVideoInput videoInput)
    {
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
        // fragments when audio starves - a degraded stream instead of no stream.
        return videoInput == FfmpegVideoInput.H264AnnexB
            ? $"-hide_banner -loglevel warning " +
              $"-thread_queue_size 128 -use_wallclock_as_timestamps 1 -f h264 -i pipe:0 " +
              audioIn +
              $"{audioOut}-c:v copy " +
              $"-f mp4 -movflags +empty_moov+default_base_moof -frag_duration {fragUs} " +
              $"-max_interleave_delta 500000 " +
              $"-flush_packets 1 pipe:1"
            : $"-hide_banner -loglevel warning " +
              $"-thread_queue_size 128 -f rawvideo -pixel_format bgra -video_size {inWidth}x{inHeight} -framerate {fps} -i pipe:0 " +
              audioIn +
              scale +
              $"{audioOut}-c:v {encoder} {encoderOpts} " +
              $"-b:v {bitrateKbps}k -maxrate {bitrateKbps * 5 / 4}k -bufsize {bitrateKbps / 2}k " +
              $"-g {gop} -bf 0 -pix_fmt yuv420p " +
              $"-f mp4 -movflags +empty_moov+default_base_moof -frag_duration {fragUs} " +
              $"-max_interleave_delta 500000 " +
              $"-flush_packets 1 pipe:1";
    }

    /// <summary>Per-encoder options, shared by the real encode AND the startup probe
    /// so a probe pass is meaningful for the config that actually runs (the v0.9-v0.10
    /// AMD miss: the probe passed on default options while the live encode stalled on
    /// its low-latency ones). AMF deliberately has NO -usage lowlatency: that
    /// submission path wedges on some driver/hardware combos (RX 9070 XT field case -
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

    public bool HasExited
    {
        get
        {
            lock (_lifecycleGate)
            {
                if (_disposed) return true;
                return _process.HasExited;
            }
        }
    }

    public int ExitCode
    {
        get
        {
            lock (_lifecycleGate)
            {
                if (_disposed) return Volatile.Read(ref _terminationConfirmed) != 0 ? 0 : -1;
                try { return _process.HasExited ? _process.ExitCode : 0; }
                catch { return -1; }
            }
        }
    }

    internal bool TerminationConfirmed => Volatile.Read(ref _terminationConfirmed) != 0;

    /// <summary>Blocking write of one raw frame; back-pressure comes from the pipe.</summary>
    public void WriteFrame(byte[] frame, int length)
    {
        _stdin.Write(frame, 0, length);
    }

    public void WritePacket(ReadOnlySpan<byte> packet)
    {
        _stdin.Write(packet);
    }

    /// <summary>Single source of truth for the positive-probe cache file
    /// (%AppData%/Spectari/encoder.cache), shared by PickEncoder and
    /// InvalidateProbeCache so the path is never spelled out in two places.</summary>
    private static string CachePath => Spectari.Util.AppPaths.EncoderCacheFile;

    internal static string? ReadCachedProbeToken()
    {
        try
        {
            return File.Exists(CachePath) ? File.ReadAllText(CachePath).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Drops the cached "passed" verdict so the next auto-mode launch
    /// re-probes the GPU encoder instead of trusting a token a live stall just
    /// disproved. Best-effort: a missing or unwritable cache just means the
    /// probe runs again anyway.</summary>
    public static void InvalidateProbeCache()
    {
        try
        {
            if (File.Exists(CachePath))
            {
                File.Delete(CachePath);
                Console.WriteLine("[encoder] cleared the cached probe verdict; the next Auto launch re-probes the GPU encoder.");
            }
        }
        catch { }
    }

    /// <summary>Picks the hardware encoder for this GPU vendor, then PROVES it can
    /// actually encode (drivers lie; being compiled into ffmpeg proves nothing).
    /// Falls back to CPU x264 so the stream starts on any machine.</summary>
    public static string PickEncoder(
        uint gpuVendorId, string adapterLuid, string driverVersion, string? requested)
    {
        if (!string.IsNullOrEmpty(requested) && requested != "auto") return requested;

        string preferred = PreferredEncoder(gpuVendorId);

        if (preferred == "libx264") return preferred;

        // Cache a PASSED probe per GPU so startup skips the 1-2s self-test.
        // Failures are deliberately not cached - a driver hiccup shouldn't
        // condemn the machine to CPU encoding forever.
        string? token = ExpectedProbeToken(gpuVendorId, adapterLuid, driverVersion);
        if (token is not null &&
            string.Equals(ReadCachedProbeToken(), token, StringComparison.Ordinal))
            return preferred;

        if (!ProbeEncoder(preferred))
        {
            Console.WriteLine($"[encoder] {preferred} failed its self-test; falling back to CPU (libx264)");
            return "libx264";
        }

        try
        {
            if (token is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                File.WriteAllText(CachePath, token);
            }
        }
        catch { }
        return preferred;
    }

    internal static bool HasHardwareEncoder(uint gpuVendorId) =>
        PreferredEncoder(gpuVendorId) != "libx264";

    /// <summary>The hardware encoder this GPU vendor prefers, or "libx264" for
    /// unknown/other vendors. Single source of truth for both PickEncoder and the
    /// probe-cache token so the two can never drift.</summary>
    private static string PreferredEncoder(uint gpuVendorId) => gpuVendorId switch
    {
        0x10DE => "h264_nvenc",
        0x1002 => "h264_amf",
        0x8086 => "h264_qsv",
        _ => "libx264",
    };

    /// <summary>The probe-cache token PickEncoder would expect for this GPU right
    /// now, in the exact form it writes to encoder.cache. A support bundle prints
    /// this next to the cached file so a report shows whether the cached "passed"
    /// verdict still matches the current adapter.
    /// Token version: bump when the probe changes. The identity digest changes
    /// automatically with its inputs, so stale "passed" verdicts (e.g. an AMD
    /// card that cleared the old trivial 320x240 test but stalls on
    /// real content) get re-evaluated. v4 binds a pass to the capture adapter LUID,
    /// installed UMD driver, and the exact ffmpeg binary/build. Null means some
    /// identity could not be read, so caching fails closed and the probe runs.</summary>
    public static string? ExpectedProbeToken(
        uint gpuVendorId, string adapterLuid, string driverVersion)
    {
        string encoder = PreferredEncoder(gpuVendorId);
        if (encoder == "libx264" || !IdentityKnown(adapterLuid) || !IdentityKnown(driverVersion))
            return null;

        return ExpectedProbeToken(gpuVendorId, adapterLuid, driverVersion, FfmpegBuildInfo());
    }

    /// <summary>Builds the same cache token from ffmpeg identity already read by
    /// the caller. Used by support-bundle generation so its UI continuation never
    /// launches ffmpeg or hashes the binary a second time.</summary>
    public static string? ExpectedProbeToken(
        uint gpuVendorId, string adapterLuid, string driverVersion,
        (string version, string buildconf, string sha256) ffmpeg)
    {
        string encoder = PreferredEncoder(gpuVendorId);
        if (encoder == "libx264" || !IdentityKnown(adapterLuid) || !IdentityKnown(driverVersion))
            return null;

        var (version, buildconf, sha256) = ffmpeg;
        if (!IdentityKnown(version) || !IdentityKnown(buildconf) ||
            sha256.Length != 16 || !sha256.All(Uri.IsHexDigit))
            return null;

        string identity = string.Join('\n',
            "probe=v4",
            $"vendor={gpuVendorId:X}",
            $"encoder={encoder}",
            $"adapter={adapterLuid}",
            $"driver={driverVersion}",
            $"ffmpeg-version={version}",
            $"ffmpeg-build={buildconf}",
            $"ffmpeg-sha256={sha256}");
        string digest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
        return $"v4:{gpuVendorId:X}:{encoder}:{digest}";
    }

    private static bool IdentityKnown(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value != "?" &&
        !value.Equals("unknown", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("timed out", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("no output", StringComparison.OrdinalIgnoreCase) &&
        !value.StartsWith("not runnable", StringComparison.OrdinalIgnoreCase) &&
        !value.StartsWith("unreadable", StringComparison.OrdinalIgnoreCase) &&
        !value.StartsWith("(resolved via PATH", StringComparison.OrdinalIgnoreCase);

    private static readonly object _buildInfoLock = new();
    private static (string version, string buildconf, string sha256)? _buildInfo;

    /// <summary>Version line, full build configuration, and a short binary hash of
    /// the ffmpeg at <see cref="FfmpegPath"/>. Reusable by the support bundle (and
    /// later probe diagnostics). Best-effort: every field degrades to a short note
    /// rather than throwing. The runner adopts + tree-kills on timeout, so a hung
    /// "-version" can't leak. A fully-known identity is memoized for the process;
    /// degraded results are returned but retried by the next caller.</summary>
    public static (string version, string buildconf, string sha256) FfmpegBuildInfo()
    {
        lock (_buildInfoLock)
        {
            if (_buildInfo is { } cached)
                return cached;

            string version = "unknown", buildconf = "unknown";
            try
            {
                var r = Util.ProcessRunner.Run(FfmpegPath, "-version", 3000);
                if (r.TimedOut) { version = buildconf = "timed out"; }
                else
                {
                    foreach (string line in r.StdOut.Split('\n'))
                    {
                        string t = line.Trim();
                        if (t.Length == 0) continue;
                        if (version == "unknown") version = t; // first non-empty line is the version
                        if (t.StartsWith("configuration:", StringComparison.OrdinalIgnoreCase))
                            buildconf = t["configuration:".Length..].Trim();
                    }
                    if (version == "unknown") version = "no output";
                }
            }
            catch (Exception ex) { version = buildconf = $"not runnable ({ex.Message})"; }

            string sha256 = "?";
            try
            {
                if (File.Exists(FfmpegPath))
                {
                    using var fs = File.OpenRead(FfmpegPath);
                    sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs))[..16].ToLowerInvariant();
                }
                else sha256 = "(resolved via PATH, not hashed)";
            }
            catch (Exception ex) { sha256 = $"unreadable ({ex.Message})"; }

            var result = (version, buildconf, sha256);
            if (IdentityKnown(version) && IdentityKnown(buildconf) && IdentityKnown(sha256) &&
                sha256.Length == 16 && sha256.All(Uri.IsHexDigit))
                _buildInfo = result;
            return result;
        }
    }

    /// <summary>Starts a best-effort background read so the GUI's first stream start
    /// normally finds the memoized identity ready. Queueing and the work itself are
    /// fail-silent.</summary>
    public static void WarmBuildInfo()
    {
        try
        {
            _ = Task.Run(() =>
            {
                try { _ = FfmpegBuildInfo(); }
                catch { }
            });
        }
        catch { }
    }

    private static readonly object _buildInfoLogLock = new();
    private static bool _buildInfoLogged;

    /// <summary>Logs the exact bundled ffmpeg (version, short hash) to the Console
    /// once per process, so the on-disk log and the support bundle capture which
    /// binary actually ran. The full build configuration is deliberately not logged
    /// (it is hundreds of characters); the support bundle and build-info.txt read it
    /// from FfmpegBuildInfo directly. Called at the top of the ctor; the once-guard
    /// means fallback restarts (a fresh encoder per attempt) don't spam the log.
    /// Best-effort: FfmpegBuildInfo already degrades to notes rather than throwing,
    /// and this whole method is wrapped so it can never throw out of the ctor.</summary>
    public static void LogBuildInfoOnce()
    {
        lock (_buildInfoLogLock)
        {
            if (_buildInfoLogged) return;
            _buildInfoLogged = true;
        }
        try
        {
            var (version, _, sha256) = FfmpegBuildInfo();
            Console.WriteLine($"[encoder] ffmpeg {version}");
            Console.WriteLine($"[encoder] ffmpeg sha256: {sha256}");
        }
        catch { }
    }

    /// <summary>Encodes a second of realistic 1080p60 motion with the SAME encoder
    /// options as a live stream. A hardware encoder that can't sustain this config
    /// (some AMD AMF driver combos write the header then stall) fails or hangs here,
    /// so we catch it before it dead-ends a live stream. Probing a different config
    /// than we run proved worthless in the field - the v0.9 probe passed on defaults
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
            Console.Error.WriteLine($"[encoder] could not run ffmpeg ({ex.Message}); is ffmpeg.exe next to Spectari.exe?");
            return false;
        }
    }

    /// <summary>Breaks a blocked stdin write after the output watchdog has proved
    /// the live pipeline is stalled and confirms the child actually exited.</summary>
    internal bool AbortForStall()
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return TerminationConfirmed;
            return TryKillAndConfirmExit(2000);
        }
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            try { _stdin.Close(); } catch { }
            _ = TryStopForDisposal();
            _process.Dispose();
            _disposed = true;
        }
    }

    private bool TryStopForDisposal()
    {
        try
        {
            if (!_process.WaitForExit(2000))
            {
                _process.Kill(entireProcessTree: true);
                if (!_process.WaitForExit(250)) return false;
            }
            Volatile.Write(ref _terminationConfirmed, 1);
            return true;
        }
        catch
        {
            return ConfirmAlreadyExited();
        }
    }

    private bool TryKillAndConfirmExit(int timeoutMs)
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                if (!_process.WaitForExit(timeoutMs)) return false;
            }
            Volatile.Write(ref _terminationConfirmed, 1);
            return true;
        }
        catch
        {
            return ConfirmAlreadyExited();
        }
    }

    private bool ConfirmAlreadyExited()
    {
        try
        {
            if (!_process.HasExited) return false;
            Volatile.Write(ref _terminationConfirmed, 1);
            return true;
        }
        catch { return false; }
    }
}
