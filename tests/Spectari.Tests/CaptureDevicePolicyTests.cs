using Spectari.Capture;
using Xunit;

namespace Spectari.Tests;

public sealed class CaptureDevicePolicyTests
{
    [Fact]
    public void DisplayNamesAreDeterministicAndDisambiguateDuplicates()
    {
        CaptureDeviceDescription[] devices =
        [
            new("device-z", "USB Camera"),
            new("device-a", " USB Camera "),
            new("device-a", "Duplicate identity"),
        ];

        IReadOnlyList<CaptureDeviceDisplayItem> items =
            CaptureDevicePolicy.PrepareDisplayItems(devices);

        Assert.Collection(
            items,
            item =>
            {
                Assert.Equal("device-a", item.Device.SymbolicLink);
                Assert.Equal("USB Camera", item.DisplayName);
            },
            item =>
            {
                Assert.Equal("device-z", item.Device.SymbolicLink);
                Assert.Equal("USB Camera (2)", item.DisplayName);
            });
    }

}
