using Spectari.Encode;
using Xunit;

namespace Spectari.Tests;

public sealed class AnnexBAccessUnitAssemblerTests
{
    [Fact]
    public void LengthPrefixedNalsBecomeAnnexB()
    {
        var assembler = new AnnexBAccessUnitAssembler();

        EncodedAccessUnit unit = assembler.Assemble(
            [0, 0, 0, 3, 0x41, 0x11, 0x22],
            isKeyFrame: false);

        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x41, 0x11, 0x22 }, unit.Data.ToArray());
        Assert.False(unit.IsKeyFrame);
    }

    [Fact]
    public void SeparateParameterSetsArePrependedToEveryKeyframe()
    {
        var assembler = new AnnexBAccessUnitAssembler();
        assembler.SetSequenceHeader(
            [0, 0, 0, 2, 0x67, 0x01, 0, 0, 0, 2, 0x68, 0x02]);
        byte[] idr = [0, 0, 0, 1, 0x65, 0x33];

        EncodedAccessUnit first = assembler.Assemble(idr, isKeyFrame: true);
        EncodedAccessUnit second = assembler.Assemble(idr, isKeyFrame: true);

        byte[] expected =
        [
            0, 0, 0, 1, 0x67, 0x01,
            0, 0, 0, 1, 0x68, 0x02,
            0, 0, 0, 1, 0x65, 0x33,
        ];
        Assert.Equal(expected, first.Data.ToArray());
        Assert.Equal(expected, second.Data.ToArray());
    }

    [Fact]
    public void IdrNalMarksKeyframeWhenCleanPointMetadataIsMissing()
    {
        var assembler = new AnnexBAccessUnitAssembler();

        EncodedAccessUnit unit = assembler.Assemble(
            [0, 0, 1, 0x65, 0x10],
            isKeyFrame: false);

        Assert.True(unit.IsKeyFrame);
    }
}
