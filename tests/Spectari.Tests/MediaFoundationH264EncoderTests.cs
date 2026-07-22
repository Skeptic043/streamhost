using Spectari.Encode;
using Xunit;

namespace Spectari.Tests;

public sealed class MediaFoundationH264EncoderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankFriendlyNameUsesStableFallback(string? value)
    {
        Assert.Equal(
            MediaFoundationH264Encoder.DefaultFriendlyName,
            MediaFoundationH264Encoder.NormalizeFriendlyName(value));
    }

    [Fact]
    public void FriendlyNameIsTrimmed()
    {
        Assert.Equal(
            "NVIDIA H.264 Encoder MFT",
            MediaFoundationH264Encoder.NormalizeFriendlyName("  NVIDIA H.264 Encoder MFT  "));
    }

    [Theory]
    [InlineData(100L, null, false)]
    [InlineData(100L, 100L, false)]
    [InlineData(100L, 99L, true)]
    public void DecodeTimestampDetectsReordering(
        long sampleTime,
        long? decodeTimestamp,
        bool expected)
    {
        Assert.Equal(
            expected,
            MediaFoundationH264Encoder.SignalsReordering(
                sampleTime,
                decodeTimestamp));
    }

    [Fact]
    public void ShutdownRunsNativeActionsInRequiredOrder()
    {
        var actions = new List<string>();

        IReadOnlyList<EncodedAccessUnit> output = HardwareEncoderShutdownSequence.Execute(
            () => actions.Add("end-of-stream"),
            () =>
            {
                actions.Add("drain");
                return [new EncodedAccessUnit(new byte[] { 1 }, false)];
            },
            () => actions.Add("end-streaming"),
            () => actions.Add("shutdown-object"),
            () => actions.Add("release"));

        Assert.Equal(
            ["end-of-stream", "drain", "end-streaming", "shutdown-object", "release"],
            actions);
        Assert.Single(output);
    }

    [Fact]
    public void ShutdownStillReleasesInOrderWhenDrainFails()
    {
        var actions = new List<string>();

        Assert.Throws<InvalidOperationException>(() =>
            HardwareEncoderShutdownSequence.Execute(
                () => actions.Add("end-of-stream"),
                () =>
                {
                    actions.Add("drain");
                    throw new InvalidOperationException("failed");
                },
                () => actions.Add("end-streaming"),
                () => actions.Add("shutdown-object"),
                () => actions.Add("release")));

        Assert.Equal(
            ["end-of-stream", "drain", "end-streaming", "shutdown-object", "release"],
            actions);
    }
}
