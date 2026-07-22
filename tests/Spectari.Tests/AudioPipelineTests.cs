using System.Diagnostics;
using Spectari.Audio;
using Xunit;

namespace Spectari.Tests;

public sealed class AudioPipelineTests
{
    [Fact]
    public void ZeroAudioPidProducesNoPipeline()
    {
        Assert.Null(AudioPipeline.Create(0, false, 8093));
    }

    [Fact]
    public void DesktopModeProducesPipelineWithoutProcessId()
    {
        using AudioPipeline pipeline = Assert.IsType<AudioPipeline>(
            AudioPipeline.Create(0, true, 8093));

        Assert.Equal("spectari_audio_8093", pipeline.PipeName);
    }

    [Fact]
    public void CaptureInputModeProducesPipelineWithoutProcessId()
    {
        using AudioPipeline pipeline = Assert.IsType<AudioPipeline>(
            AudioPipeline.Create(0, false, "capture-input-id", 8093));

        Assert.Equal("spectari_audio_8093", pipeline.PipeName);
    }

    [Fact]
    public void MultipleAudioModesAreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new AudioPipeline(42, false, "capture-input-id", 8093));
        Assert.Throws<ArgumentException>(() =>
            new AudioPipeline(0, true, "capture-input-id", 8093));
    }

    [Fact]
    public void PipeNameIncludesTheConfiguredPort()
    {
        Assert.Equal("spectari_audio_8093", AudioPipeline.FormatPipeName(8093));
    }

    [Fact]
    public void LeadInFramesIncludeOneHundredMillisecondSafetyBias()
    {
        const long videoEpochTicks = 1234;
        long fallbackStartTicks = videoEpochTicks + Stopwatch.Frequency;

        long frames = ProcessAudioCapture.GetLeadInFrames(videoEpochTicks, fallbackStartTicks);

        Assert.Equal(ProcessAudioCapture.SampleRate * 11 / 10, frames);
        Assert.Equal(
            "[audio] aligned to video timeline (+1100 ms lead-in silence, includes 100 ms safety bias)",
            ProcessAudioCapture.FormatLeadInLog(frames));
    }
}
