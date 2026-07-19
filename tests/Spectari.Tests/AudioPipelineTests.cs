using System.Diagnostics;
using Spectari.Audio;
using Xunit;

namespace Spectari.Tests;

public sealed class AudioPipelineTests
{
    [Fact]
    public void ZeroAudioPidProducesNoPipeline()
    {
        Assert.Null(AudioPipeline.Create(0, 8093));
    }

    [Fact]
    public void PipeNameIncludesTheConfiguredPort()
    {
        Assert.Equal("spectari_audio_8093", AudioPipeline.FormatPipeName(8093));
    }

    [Fact]
    public void LeadInFramesMatchOneSecondBetweenEpochAndFallback()
    {
        const long videoEpochTicks = 1234;
        long fallbackStartTicks = videoEpochTicks + Stopwatch.Frequency;

        long frames = ProcessAudioCapture.GetLeadInFrames(videoEpochTicks, fallbackStartTicks);

        Assert.Equal(ProcessAudioCapture.SampleRate, frames);
    }
}
