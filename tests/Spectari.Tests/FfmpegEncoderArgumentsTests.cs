using Spectari.Encode;
using Xunit;

namespace Spectari.Tests;

public sealed class FfmpegEncoderArgumentsTests
{
    [Fact]
    public void H264WallclockOptionBelongsOnlyToTheVideoInput()
    {
        string arguments = FfmpegEncoder.BuildArguments(
            1920,
            1080,
            60,
            12000,
            1920,
            1080,
            "h264_nvenc",
            "audio_pipe",
            0,
            FfmpegVideoInput.H264AnnexB);
        string[] tokens = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int videoInput = Array.IndexOf(tokens, "pipe:0");
        int audioInput = Array.IndexOf(tokens, "\\\\.\\pipe\\audio_pipe");

        Assert.Equal(
            ["-use_wallclock_as_timestamps", "1", "-f", "h264", "-i", "pipe:0"],
            tokens[(videoInput - 5)..(videoInput + 1)]);
        Assert.Equal(
            ["-ar", "48000", "-ac", "2", "-i", "\\\\.\\pipe\\audio_pipe"],
            tokens[(audioInput - 5)..(audioInput + 1)]);
        Assert.Equal(1, tokens.Count(token => token == "-use_wallclock_as_timestamps"));
        Assert.DoesNotContain("setts=", arguments);
        Assert.DoesNotContain("-framerate", tokens[..videoInput]);
    }

    [Fact]
    public void RawVideoArgumentsRemainUnchanged()
    {
        string arguments = FfmpegEncoder.BuildArguments(
            1920,
            1080,
            60,
            12000,
            1280,
            720,
            "h264_nvenc",
            null,
            0,
            FfmpegVideoInput.RawBgra);

        Assert.Equal(
            "-hide_banner -loglevel warning -thread_queue_size 128 -f rawvideo -pixel_format bgra -video_size 1920x1080 -framerate 60 -i pipe:0 -vf scale=1280:720:flags=bilinear -an -c:v h264_nvenc -preset p4 -tune ull -rc cbr -multipass 0 -profile:v high -b:v 12000k -maxrate 15000k -bufsize 6000k -g 30 -bf 0 -pix_fmt yuv420p -f mp4 -movflags +empty_moov+default_base_moof -frag_duration 16666 -max_interleave_delta 500000 -flush_packets 1 pipe:1",
            arguments);
    }
}
