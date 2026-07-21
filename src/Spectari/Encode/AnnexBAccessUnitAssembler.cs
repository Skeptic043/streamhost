namespace Spectari.Encode;

/// <summary>Normalizes Media Foundation H.264 output into complete Annex B units.</summary>
internal sealed class AnnexBAccessUnitAssembler
{
    private static ReadOnlySpan<byte> StartCode => [0, 0, 0, 1];
    private static ReadOnlySpan<byte> ShortStartCode => [0, 0, 1];
    private byte[] _parameterSets = [];

    internal void SetSequenceHeader(ReadOnlySpan<byte> sequenceHeader)
    {
        _parameterSets = Normalize(sequenceHeader);
    }

    internal EncodedAccessUnit Assemble(ReadOnlySpan<byte> encodedSample, bool isKeyFrame)
    {
        byte[] sample = Normalize(encodedSample);
        bool keyFrame = isKeyFrame || ContainsNalType(sample, 5);
        if (!keyFrame || _parameterSets.Length == 0)
            return new EncodedAccessUnit(sample, keyFrame);

        byte[] combined = GC.AllocateUninitializedArray<byte>(_parameterSets.Length + sample.Length);
        _parameterSets.CopyTo(combined, 0);
        sample.CopyTo(combined, _parameterSets.Length);
        return new EncodedAccessUnit(combined, true);
    }

    private static byte[] Normalize(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return [];
        if (HasStartCode(data)) return data.ToArray();

        var result = new List<byte>(data.Length + 16);
        int offset = 0;
        while (offset + 4 <= data.Length)
        {
            int length = (data[offset] << 24) | (data[offset + 1] << 16) |
                (data[offset + 2] << 8) | data[offset + 3];
            offset += 4;
            if (length <= 0 || offset + length > data.Length)
                return WithStartCode(data);
            result.AddRange(StartCode.ToArray());
            result.AddRange(data.Slice(offset, length).ToArray());
            offset += length;
        }

        return offset == data.Length ? result.ToArray() : WithStartCode(data);
    }

    private static byte[] WithStartCode(ReadOnlySpan<byte> data)
    {
        byte[] result = GC.AllocateUninitializedArray<byte>(StartCode.Length + data.Length);
        StartCode.CopyTo(result);
        data.CopyTo(result.AsSpan(StartCode.Length));
        return result;
    }

    private static bool HasStartCode(ReadOnlySpan<byte> data) =>
        data.StartsWith(StartCode) || data.StartsWith(ShortStartCode);

    private static bool ContainsNalType(ReadOnlySpan<byte> data, int requestedType)
    {
        for (int index = 0; index + 3 < data.Length; index++)
        {
            int header = data[index] == 0 && data[index + 1] == 0 && data[index + 2] == 1
                ? index + 3
                : index + 4 < data.Length && data[index] == 0 && data[index + 1] == 0 &&
                  data[index + 2] == 0 && data[index + 3] == 1
                    ? index + 4
                    : -1;
            if (header >= 0 && (data[header] & 0x1f) == requestedType) return true;
        }
        return false;
    }
}
