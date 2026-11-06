using System.IO.Compression;
using System.Text;

namespace Screenbox.Core.Tests.Helpers;

/// <summary>
/// A minimal EBML writer, just enough to build small synthetic Matroska files for the
/// subtitle reader's tests. Sizes are written in their shortest form unless a test asks for
/// the "unknown size" marker.
/// </summary>
internal static class Ebml
{
    public const uint EbmlHeaderId = 0x1A45DFA3;
    public const uint SegmentId = 0x18538067;
    public const uint InfoId = 0x1549A966;
    public const uint TimestampScaleId = 0x2AD7B1;
    public const uint TracksId = 0x1654AE6B;
    public const uint TrackEntryId = 0xAE;
    public const uint TrackNumberId = 0xD7;
    public const uint TrackTypeId = 0x83;
    public const uint CodecIdId = 0x86;
    public const uint CodecPrivateId = 0x63A2;
    public const uint LanguageId = 0x22B59C;
    public const uint LanguageBcp47Id = 0x22B59D;
    public const uint NameId = 0x536E;
    public const uint FlagDefaultId = 0x88;
    public const uint DefaultDurationId = 0x23E383;
    public const uint ContentEncodingsId = 0x6D80;
    public const uint ContentEncodingId = 0x6240;
    public const uint ContentEncodingOrderId = 0x5031;
    public const uint ContentEncodingScopeId = 0x5032;
    public const uint ContentEncodingTypeId = 0x5033;
    public const uint ContentCompressionId = 0x5034;
    public const uint ContentCompAlgoId = 0x4254;
    public const uint ContentCompSettingsId = 0x4255;
    public const uint ContentEncryptionId = 0x5035;
    public const uint ClusterId = 0x1F43B675;
    public const uint ClusterTimestampId = 0xE7;
    public const uint SimpleBlockId = 0xA3;
    public const uint BlockGroupId = 0xA0;
    public const uint BlockId = 0xA1;
    public const uint BlockDurationId = 0x9B;
    public const uint CuesId = 0x1C53BB6B;
    public const uint VoidId = 0xEC;

    public static byte[] Id(uint id)
    {
        if (id >= 0x1000000) return [(byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id];
        if (id >= 0x10000) return [(byte)(id >> 16), (byte)(id >> 8), (byte)id];
        if (id >= 0x100) return [(byte)(id >> 8), (byte)id];
        return [(byte)id];
    }

    /// <summary>Encodes a size (or a block's track number) as a minimal-length VINT.</summary>
    public static byte[] Vint(long value)
    {
        for (int length = 1; length <= 8; length++)
        {
            // All-ones is reserved for "unknown", so the largest usable value is one less.
            long max = (1L << (7 * length)) - 2;
            if (value > max) continue;
            byte[] bytes = new byte[length];
            for (int i = length - 1; i >= 0; i--)
            {
                bytes[i] = (byte)value;
                value >>= 8;
            }

            bytes[0] |= (byte)(0x80 >> (length - 1));
            return bytes;
        }

        throw new ArgumentOutOfRangeException(nameof(value));
    }

    public static byte[] Element(uint id, params byte[][] children)
    {
        byte[] data = Concat(children);
        return Concat(Id(id), Vint(data.Length), data);
    }

    /// <summary>A master element whose size field is the one-byte "unknown size" marker.</summary>
    public static byte[] UnknownSizeElement(uint id, params byte[][] children) =>
        Concat(Id(id), [0xFF], Concat(children));

    public static byte[] Uint(uint id, ulong value)
    {
        int length = 1;
        while (length < 8 && value >> (8 * length) != 0) length++;
        byte[] data = new byte[length];
        for (int i = length - 1; i >= 0; i--)
        {
            data[i] = (byte)value;
            value >>= 8;
        }

        return Element(id, data);
    }

    public static byte[] String(uint id, string value) => Element(id, Encoding.UTF8.GetBytes(value));

    public static byte[] Binary(uint id, byte[] value) => Element(id, value);

    public static byte[] Concat(params byte[][] parts)
    {
        using MemoryStream stream = new();
        foreach (byte[] part in parts)
        {
            stream.Write(part);
        }

        return stream.ToArray();
    }

    public static byte[] Zlib(byte[] data)
    {
        using MemoryStream output = new();
        using (ZLibStream zlib = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }
}

/// <summary>
/// Builds a small synthetic Matroska file: an EBML header, then a Segment with Info, Tracks and
/// Clusters, in the order mkvmerge writes them.
/// </summary>
internal sealed class MatroskaTestFile
{
    private readonly List<byte[]> _tracks = [];
    private readonly List<byte[]> _clusters = [];
    private readonly List<byte[]> _trailing = [];

    public long? TimestampScale { get; set; }

    public bool UnknownSizeSegment { get; set; }

    public MatroskaTestFile AddTrack(
        ulong number,
        string codecId,
        ulong type = 0x11,
        string? codecPrivate = null,
        string? language = null,
        string? name = null,
        bool? isDefault = null,
        string? languageBcp47 = null,
        long? defaultDurationNs = null,
        params byte[][] contentEncodings)
    {
        List<byte[]> children =
        [
            Ebml.Uint(Ebml.TrackNumberId, number),
            Ebml.Uint(Ebml.TrackTypeId, type),
            Ebml.String(Ebml.CodecIdId, codecId),
        ];
        if (codecPrivate is not null) children.Add(Ebml.String(Ebml.CodecPrivateId, codecPrivate));
        if (language is not null) children.Add(Ebml.String(Ebml.LanguageId, language));
        if (languageBcp47 is not null) children.Add(Ebml.String(Ebml.LanguageBcp47Id, languageBcp47));
        if (name is not null) children.Add(Ebml.String(Ebml.NameId, name));
        if (isDefault is not null) children.Add(Ebml.Uint(Ebml.FlagDefaultId, isDefault.Value ? 1UL : 0UL));
        if (defaultDurationNs is not null) children.Add(Ebml.Uint(Ebml.DefaultDurationId, (ulong)defaultDurationNs.Value));
        if (contentEncodings.Length > 0) children.Add(Ebml.Element(Ebml.ContentEncodingsId, contentEncodings));
        _tracks.Add(Ebml.Element(Ebml.TrackEntryId, children.ToArray()));
        return this;
    }

    /// <summary>Adds a track entry built by the caller, for tests that need unusual children.</summary>
    public MatroskaTestFile AddRawTrack(params byte[][] children)
    {
        _tracks.Add(Ebml.Element(Ebml.TrackEntryId, children));
        return this;
    }

    public MatroskaTestFile AddCluster(ulong timestamp, params byte[][] blocks)
    {
        _clusters.Add(Ebml.Element(Ebml.ClusterId, Ebml.Concat(Ebml.Uint(Ebml.ClusterTimestampId, timestamp)), Ebml.Concat(blocks)));
        return this;
    }

    public MatroskaTestFile AddUnknownSizeCluster(ulong timestamp, params byte[][] blocks)
    {
        _clusters.Add(Ebml.UnknownSizeElement(Ebml.ClusterId, Ebml.Concat(Ebml.Uint(Ebml.ClusterTimestampId, timestamp)), Ebml.Concat(blocks)));
        return this;
    }

    /// <summary>Appends a top-level element (for example Cues) after the clusters.</summary>
    public MatroskaTestFile AddTrailing(byte[] element)
    {
        _trailing.Add(element);
        return this;
    }

    public static byte[] SimpleBlock(ulong track, short offset, string payload, byte flags = 0x80) =>
        SimpleBlock(track, offset, Encoding.UTF8.GetBytes(payload), flags);

    public static byte[] SimpleBlock(ulong track, short offset, byte[] payload, byte flags = 0x80) =>
        Ebml.Element(Ebml.SimpleBlockId, BlockBody(track, offset, flags, payload));

    public static byte[] BlockGroup(ulong track, short offset, string payload, ulong? duration) =>
        BlockGroup(track, offset, Encoding.UTF8.GetBytes(payload), duration);

    public static byte[] BlockGroup(ulong track, short offset, byte[] payload, ulong? duration, byte flags = 0x00)
    {
        byte[] block = Ebml.Element(Ebml.BlockId, BlockBody(track, offset, flags, payload));
        return duration is null
            ? Ebml.Element(Ebml.BlockGroupId, block)
            : Ebml.Element(Ebml.BlockGroupId, block, Ebml.Uint(Ebml.BlockDurationId, duration.Value));
    }

    public static byte[] Compression(ulong algorithm, byte[]? settings = null, ulong? scope = null, ulong order = 0)
    {
        List<byte[]> compression = [Ebml.Uint(Ebml.ContentCompAlgoId, algorithm)];
        if (settings is not null) compression.Add(Ebml.Binary(Ebml.ContentCompSettingsId, settings));
        List<byte[]> children =
        [
            Ebml.Uint(Ebml.ContentEncodingOrderId, order),
            Ebml.Uint(Ebml.ContentEncodingTypeId, 0),
            Ebml.Element(Ebml.ContentCompressionId, compression.ToArray()),
        ];
        if (scope is not null) children.Add(Ebml.Uint(Ebml.ContentEncodingScopeId, scope.Value));
        return Ebml.Element(Ebml.ContentEncodingId, children.ToArray());
    }

    public static byte[] Encryption() =>
        Ebml.Element(Ebml.ContentEncodingId,
            Ebml.Uint(Ebml.ContentEncodingTypeId, 1),
            Ebml.Element(Ebml.ContentEncryptionId, Ebml.Uint(0x47E1, 5)));

    public byte[] Build()
    {
        byte[] header = Ebml.Element(Ebml.EbmlHeaderId,
            Ebml.Uint(0x4286, 1),
            Ebml.Uint(0x42F7, 1),
            Ebml.Uint(0x42F2, 4),
            Ebml.Uint(0x42F3, 8),
            Ebml.String(0x4282, "matroska"),
            Ebml.Uint(0x4287, 4),
            Ebml.Uint(0x4285, 2));

        List<byte[]> info = [];
        if (TimestampScale is not null) info.Add(Ebml.Uint(Ebml.TimestampScaleId, (ulong)TimestampScale.Value));
        info.Add(Ebml.String(0x4D80, "Screenbox tests"));

        byte[] segmentBody = Ebml.Concat(
            Ebml.Element(Ebml.InfoId, info.ToArray()),
            Ebml.Element(Ebml.TracksId, _tracks.ToArray()),
            Ebml.Concat(_clusters.ToArray()),
            Ebml.Concat(_trailing.ToArray()));

        byte[] segment = UnknownSizeSegment
            ? Ebml.UnknownSizeElement(Ebml.SegmentId, segmentBody)
            : Ebml.Element(Ebml.SegmentId, segmentBody);
        return Ebml.Concat(header, segment);
    }

    public MemoryStream BuildStream() => new(Build(), writable: false);

    private static byte[] BlockBody(ulong track, short offset, byte flags, byte[] payload) =>
        Ebml.Concat(Ebml.Vint((long)track), [(byte)(offset >> 8), (byte)offset, flags], payload);
}
