using System.Buffers.Binary;
using System.Text;
using StreamHost.Server;

namespace StreamHost.Mp4;

/// <summary>
/// Consumes ffmpeg's fragmented-MP4 byte stream, splits it into the init segment
/// (ftyp+moov) and per-frame fragments (moof+mdat), detects keyframe fragments,
/// and extracts the RFC 6381 codec string from avcC — everything MSE needs.
/// </summary>
public static class Mp4Splitter
{
    public static async Task RunAsync(Stream source, Broadcaster sink, CancellationToken ct)
    {
        var initBoxes = new List<byte[]>();
        byte[]? pendingMoof = null;
        bool pendingKeyframe = false;
        uint videoTrackId = 0;
        var header = new byte[8];

        while (!ct.IsCancellationRequested)
        {
            if (!await ReadExactlyAsync(source, header, 8, ct)) break;
            uint size = BinaryPrimitives.ReadUInt32BigEndian(header);
            string type = Encoding.ASCII.GetString(header, 4, 4);
            if (size < 8 || size > 64 * 1024 * 1024)
                throw new InvalidDataException($"Implausible MP4 box '{type}' size {size}");

            var box = new byte[size];
            Array.Copy(header, box, 8);
            if (!await ReadExactlyAsync(source, box, (int)size - 8, ct, offset: 8)) break;

            switch (type)
            {
                case "ftyp":
                    initBoxes.Add(box);
                    break;
                case "moov":
                    initBoxes.Add(box);
                    (string codec, uint trackId) = ParseMoovInfo(box);
                    videoTrackId = trackId;
                    sink.SetInit(Concat(initBoxes), codec);
                    break;
                case "moof":
                    pendingMoof = box;
                    pendingKeyframe = MoofStartsWithSyncSample(box, videoTrackId);
                    break;
                case "mdat" when pendingMoof is not null:
                    var fragment = new byte[pendingMoof.Length + box.Length];
                    pendingMoof.CopyTo(fragment, 0);
                    box.CopyTo(fragment, pendingMoof.Length);
                    sink.Broadcast(fragment, pendingKeyframe);
                    pendingMoof = null;
                    break;
                default:
                    break; // styp/sidx/mfra etc. — irrelevant for live MSE
            }
        }
    }

    private static async Task<bool> ReadExactlyAsync(Stream s, byte[] buf, int count, CancellationToken ct, int offset = 0)
    {
        int read = 0;
        while (read < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(offset + read, count - read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    private static byte[] Concat(List<byte[]> parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        int pos = 0;
        foreach (var p in parts) { p.CopyTo(result, pos); pos += p.Length; }
        return result;
    }

    // ---- box walking ----------------------------------------------------

    private static (int offset, int size)? FindBox(byte[] buf, int start, int end, string type)
    {
        int pos = start;
        while (pos + 8 <= end)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(pos));
            if (size < 8 || pos + size > end) return null;
            if (buf[pos + 4] == type[0] && buf[pos + 5] == type[1] &&
                buf[pos + 6] == type[2] && buf[pos + 7] == type[3])
                return (pos, (int)size);
            pos += (int)size;
        }
        return null;
    }

    /// <summary>True when the fragment's first VIDEO sample is a sync sample (IDR).
    /// With audio muxed in, a moof carries one traf per track — we must check the
    /// video track's traf (audio samples are all "sync" and would fake keyframes).</summary>
    private static bool MoofStartsWithSyncSample(byte[] moof, uint videoTrackId)
    {
        const uint SampleIsNonSync = 0x00010000;
        int searchPos = 8;

        while (true)
        {
            var traf = FindBox(moof, searchPos, moof.Length, "traf");
            if (traf is null) return false;
            searchPos = traf.Value.offset + traf.Value.size;
            int trafStart = traf.Value.offset + 8, trafEnd = traf.Value.offset + traf.Value.size;

            var tfhd = FindBox(moof, trafStart, trafEnd, "tfhd");
            if (tfhd is null) continue;
            uint trackId = BinaryPrimitives.ReadUInt32BigEndian(moof.AsSpan(tfhd.Value.offset + 12));
            if (videoTrackId != 0 && trackId != videoTrackId) continue;

            // trun first-sample-flags wins; tfhd default-sample-flags is the fallback
            var trun = FindBox(moof, trafStart, trafEnd, "trun");
            if (trun is not null)
            {
                int p = trun.Value.offset;
                uint flags = BinaryPrimitives.ReadUInt32BigEndian(moof.AsSpan(p + 8)) & 0x00FFFFFF;
                int fieldPos = p + 16;                    // header(8) + version/flags(4) + sample_count(4)
                if ((flags & 0x000001) != 0) fieldPos += 4; // data_offset
                if ((flags & 0x000004) != 0)
                {
                    uint firstSampleFlags = BinaryPrimitives.ReadUInt32BigEndian(moof.AsSpan(fieldPos));
                    return (firstSampleFlags & SampleIsNonSync) == 0;
                }
            }

            int tp = tfhd.Value.offset;
            uint tfhdFlags = BinaryPrimitives.ReadUInt32BigEndian(moof.AsSpan(tp + 8)) & 0x00FFFFFF;
            int tfieldPos = tp + 16;                      // header(8) + version/flags(4) + track_ID(4)
            if ((tfhdFlags & 0x000001) != 0) tfieldPos += 8; // base_data_offset
            if ((tfhdFlags & 0x000002) != 0) tfieldPos += 4; // sample_description_index
            if ((tfhdFlags & 0x000008) != 0) tfieldPos += 4; // default_sample_duration
            if ((tfhdFlags & 0x000010) != 0) tfieldPos += 4; // default_sample_size
            if ((tfhdFlags & 0x000020) != 0)
            {
                uint defaultFlags = BinaryPrimitives.ReadUInt32BigEndian(moof.AsSpan(tfieldPos));
                return (defaultFlags & SampleIsNonSync) == 0;
            }
            return false;
        }
    }

    /// <summary>Walks every trak in moov: builds the MSE codec string (video first,
    /// e.g. "avc1.64002A,mp4a.40.2") and finds the video track's track_ID.</summary>
    private static (string codec, uint videoTrackId) ParseMoovInfo(byte[] moov)
    {
        var codecs = new List<string>();
        uint videoTrackId = 0;
        int trakSearch = 8;

        while (true)
        {
            var trak = FindBox(moov, trakSearch, moov.Length, "trak");
            if (trak is null) break;
            int trakStart = trak.Value.offset, trakEnd = trak.Value.offset + trak.Value.size;
            trakSearch = trakEnd;

            uint trackId = 0;
            var tkhd = FindBox(moov, trakStart + 8, trakEnd, "tkhd");
            if (tkhd is not null)
            {
                int p = tkhd.Value.offset;
                byte version = moov[p + 8];
                int idOffset = p + (version == 1 ? 28 : 20); // header+verflags + creation/mod times
                trackId = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(idOffset));
            }

            var mdia = FindBox(moov, trakStart + 8, trakEnd, "mdia");
            if (mdia is null) continue;
            var minf = FindBox(moov, mdia.Value.offset + 8, mdia.Value.offset + mdia.Value.size, "minf");
            if (minf is null) continue;
            var stbl = FindBox(moov, minf.Value.offset + 8, minf.Value.offset + minf.Value.size, "stbl");
            if (stbl is null) continue;
            var stsd = FindBox(moov, stbl.Value.offset + 8, stbl.Value.offset + stbl.Value.size, "stsd");
            if (stsd is null) continue;

            // stsd: header(8) + version/flags(4) + entry_count(4), then sample entries
            int entry = stsd.Value.offset + 16;
            int stsdEnd = stsd.Value.offset + stsd.Value.size;
            while (entry + 8 <= stsdEnd)
            {
                uint entrySize = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(entry));
                string entryType = Encoding.ASCII.GetString(moov, entry + 4, 4);
                if (entryType is "avc1" or "avc3")
                {
                    // VisualSampleEntry fixed part = 78 bytes after the 8-byte box header
                    var avcC = FindBox(moov, entry + 86, entry + (int)entrySize, "avcC");
                    string codec = "avc1.64002A";
                    if (avcC is not null)
                    {
                        int p = avcC.Value.offset + 8; // AVCDecoderConfigurationRecord
                        codec = $"avc1.{moov[p + 1]:X2}{moov[p + 2]:X2}{moov[p + 3]:X2}";
                    }
                    codecs.Insert(0, codec);
                    videoTrackId = trackId;
                }
                else if (entryType == "mp4a")
                {
                    codecs.Add("mp4a.40.2"); // AAC-LC, matches our ffmpeg settings
                }
                if (entrySize < 8) break;
                entry += (int)entrySize;
            }
        }

        return (codecs.Count > 0 ? string.Join(",", codecs) : "avc1.64002A", videoTrackId);
    }
}
