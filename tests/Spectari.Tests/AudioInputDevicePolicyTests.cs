using Spectari.Audio;
using Xunit;

namespace Spectari.Tests;

public sealed class AudioInputDevicePolicyTests
{
    [Fact]
    public void KeepsCaptureStyleInputsAndExcludesMicrophoneStyleInputs()
    {
        IReadOnlyList<AudioInputDeviceDisplayItem> items =
            AudioInputDevicePolicy.PrepareDisplayItems(
            [
                Device("mic", "Desk microphone", AudioEndpointFormFactor.Microphone),
                Device("headset", "Headset microphone", AudioEndpointFormFactor.Headset),
                Device("line", "Capture Card", AudioEndpointFormFactor.LineLevel),
                Device("digital", "HDMI Audio", AudioEndpointFormFactor.DigitalAudioDisplayDevice),
                Device("unknown", "USB Capture", AudioEndpointFormFactor.Unknown),
            ]);

        Assert.Equal(
            ["Capture Card", "HDMI Audio", "USB Capture"],
            items.Select(item => item.Device.FriendlyName));
    }

    [Fact]
    public void DeduplicatesStableIdsAndDisambiguatesDuplicateNames()
    {
        IReadOnlyList<AudioInputDeviceDisplayItem> items =
            AudioInputDevicePolicy.PrepareDisplayItems(
            [
                Device("device-b", "Capture Card", AudioEndpointFormFactor.Unknown),
                Device("device-a", "Capture Card", AudioEndpointFormFactor.LineLevel),
                Device("DEVICE-A", "Ignored duplicate", AudioEndpointFormFactor.LineLevel),
                Device("", "Missing id", AudioEndpointFormFactor.LineLevel),
            ]);

        Assert.Equal(["device-a", "device-b"], items.Select(item => item.Device.Id));
        Assert.Equal(
            [
                "Capture device audio - Capture Card",
                "Capture device audio - Capture Card (2)",
            ],
            items.Select(item => item.DisplayName));
    }

    private static AudioInputDeviceDescription Device(
        string id,
        string name,
        AudioEndpointFormFactor formFactor) => new(id, name, formFactor);
}
