using System.Runtime.InteropServices;
using Spectari.Audio;
using Xunit;

namespace Spectari.Tests;

public sealed class CoreAudioInteropTests
{
    [Fact]
    public void NativeStructLayoutsMatchX64CoreAudioContracts()
    {
        Assert.Equal(20, Marshal.SizeOf<CoreAudioInterop.PropertyKey>());
        Assert.Equal(24, Marshal.SizeOf<CoreAudioInterop.PropVariant>());
        Assert.Equal(18, Marshal.SizeOf<CoreAudioInterop.WaveFormatEx>());
    }
}
