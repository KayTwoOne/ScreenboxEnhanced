using System.Text;
using Screenbox.Core.Helpers;
using Screenbox.Core.Models;

namespace Screenbox.Core.Tests.Helpers;

public sealed class MatroskaSubtitleReaderTests
{
    private const string AssHeader = "[Script Info]\nScriptType: v4.00+\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n";

    private static string Text(MatroskaBlock block) => Encoding.UTF8.GetString(block.Payload);

    [Test]
    public async Task ReadHeader_FindsTracksWithCodecLanguageAndName()
    {
        using MemoryStream stream = new MatroskaTestFile()
            .AddTrack(1, "V_MPEGH/ISO/HEVC", type: 1)
            .AddTrack(4, "S_TEXT/ASS", codecPrivate: AssHeader, language: "eng", name: "Signs and Songs", isDefault: true)
            .AddTrack(6, "S_TEXT/UTF8", language: "jpn", languageBcp47: "ja", name: "Japanese", isDefault: false)
            .BuildStream();

        MatroskaHeader? header = MatroskaSubtitleReader.ReadHeader(stream);

        await Assert.That(header).IsNotNull();
        await Assert.That(header!.Tracks.Count).IsEqualTo(3);
        MatroskaTrack video = header.Tracks[0];
        await Assert.That(video.Number).IsEqualTo(1UL);
        await Assert.That(video.IsSubtitle).IsFalse();

        MatroskaTrack ass = header.Tracks[1];
        await Assert.That(ass.Number).IsEqualTo(4UL);
        await Assert.That(ass.IsSubtitle).IsTrue();
        await Assert.That(ass.CodecId).IsEqualTo("S_TEXT/ASS");
        await Assert.That(Encoding.UTF8.GetString(ass.CodecPrivate)).IsEqualTo(AssHeader);
        await Assert.That(ass.Language).IsEqualTo("eng");
        await Assert.That(ass.Name).IsEqualTo("Signs and Songs");
        await Assert.That(ass.IsDefault).IsTrue();

        MatroskaTrack srt = header.Tracks[2];
        await Assert.That(srt.Number).IsEqualTo(6UL);
        await Assert.That(srt.CodecId).IsEqualTo("S_TEXT/UTF8");
        await Assert.That(srt.Language).IsEqualTo("jpn");
        await Assert.That(srt.LanguageBcp47).IsEqualTo("ja");
        await Assert.That(srt.Name).IsEqualTo("Japanese");
        await Assert.That(srt.IsDefault).IsFalse();
    }

    [Test]
    public async Task ReadHeader_UsesMatroskaDefaultsForMissingElements()
    {
        using MemoryStream stream = new MatroskaTestFile()
            .AddTrack(3, "S_TEXT/UTF8")
            .BuildStream();

        MatroskaHeader? header = MatroskaSubtitleReader.ReadHeader(stream);

        await Assert.That(header).IsNotNull();
        await Assert.That(header!.TimestampScale).IsEqualTo(1_000_000L);
        MatroskaTrack track = header.Tracks[0];
        await Assert.That(track.Language).IsEqualTo("eng");
        await Assert.That(track.Name).IsEqualTo(string.Empty);
        await Assert.That(track.IsDefault).IsTrue();
        await Assert.That(track.CodecPrivate.Length).IsEqualTo(0);
    }

    [Test]
    public async Task ReadHeader_StopsAtTheFirstCluster()
    {
        // A thousand clusters: even walking just their element headers would read well over 4 KiB.
        MatroskaTestFile file = new MatroskaTestFile()
            .AddTrack(1, "V_MPEG4/ISO/AVC", type: 1)
            .AddTrack(2, "S_TEXT/UTF8");
        for (ulong i = 0; i < 1000; i++)
        {
            file.AddCluster(i * 1000, MatroskaTestFile.SimpleBlock(1, 0, new byte[512]), MatroskaTestFile.BlockGroup(2, 0, "hi", 100));
        }

        using CountingStream stream = new(file.BuildStream());

        MatroskaHeader? header = MatroskaSubtitleReader.ReadHeader(stream);

        await Assert.That(header).IsNotNull();
        await Assert.That(header!.Tracks.Count).IsEqualTo(2);
        await Assert.That(stream.BytesRead).IsLessThan(4096L);
    }

    [Test]
    public async Task ReadTrack_ResolvesTimingFromTimestampScaleClusterOffsetAndDuration()
    {
        // TimestampScale 100 000 ns: one unit is 0.1 ms.
        using MemoryStream stream = new MatroskaTestFile { TimestampScale = 100_000 }
            .AddTrack(2, "S_TEXT/UTF8")
            .AddCluster(10_000, MatroskaTestFile.BlockGroup(2, 500, "first", 20_000))
            .AddCluster(50_000, MatroskaTestFile.BlockGroup(2, -200, "second", 15))
            .BuildStream();

        MatroskaTrackData? data = MatroskaSubtitleReader.ReadTrack(stream, 2);

        await Assert.That(data).IsNotNull();
        await Assert.That(data!.IsComplete).IsTrue();
        await Assert.That(data.Header.TimestampScale).IsEqualTo(100_000L);
        await Assert.That(data.Blocks.Count).IsEqualTo(2);
        await Assert.That(data.Blocks[0].StartNs).IsEqualTo((10_000L + 500) * 100_000);
        await Assert.That(data.Blocks[0].DurationNs).IsEqualTo(20_000L * 100_000);
        await Assert.That(Text(data.Blocks[0])).IsEqualTo("first");
        await Assert.That(data.Blocks[1].StartNs).IsEqualTo((50_000L - 200) * 100_000);
        await Assert.That(data.Blocks[1].DurationNs).IsEqualTo(15L * 100_000);
        await Assert.That(Text(data.Blocks[1])).IsEqualTo("second");
    }

    [Test]
    public async Task ReadTrack_ReadsBlockDurationWrittenBeforeTheBlock()
    {
        // Children of a BlockGroup may come in any order.
        byte[] block = Ebml.Element(Ebml.BlockId, Ebml.Concat(Ebml.Vint(2), [0, 10, 0], Encoding.UTF8.GetBytes("x")));
        byte[] group = Ebml.Element(Ebml.BlockGroupId, Ebml.Uint(Ebml.BlockDurationId, 750), block);
        using MemoryStream stream = new MatroskaTestFile()
            .AddTrack(2, "S_TEXT/UTF8")
            .AddCluster(1000, group)
            .BuildStream();

        MatroskaTrackData? data = MatroskaSubtitleReader.ReadTrack(stream, 2);

        await Assert.That(data!.Blocks.Count).IsEqualTo(1);
        await Assert.That(data.Blocks[0].StartNs).IsEqualTo(1010L * 1_000_000);
        await Assert.That(data.Blocks[0].DurationNs).IsEqualTo(750L * 1_000_000);
    }

    [Test]
    public async Task ReadTrack_SimpleBlockFallsBackToDefaultDuration()
    {
        using MemoryStream stream = new MatroskaTestFile()
            .AddTrack(2, "S_TEXT/UTF8", defaultDurationNs: 3_000_000_000)
            .AddTrack(3, "S_TEXT/UTF8")
            .AddCluster(0, MatroskaTestFile.SimpleBlock(2, 40, "simple"), MatroskaTestFile.SimpleBlock(3, 40, "none"))
            .BuildStream();

        MatroskaTrackData? withDefault = MatroskaSubtitleReader.ReadTrack(stream, 2);
        stream.Position = 0;
        MatroskaTrackData? withoutDefault = MatroskaSubtitleReader.ReadTrack(stream, 3);

        await Assert.That(withDefault!.Blocks[0].StartNs).IsEqualTo(40L * 1_000_000);
        await Assert.That(withDefault.Blocks[0].DurationNs).IsEqualTo(3_000_000_000L);
        await Assert.That(withoutDefault!.Blocks[0].DurationNs).IsEqualTo(0L);
    }

    [Test]
    public async Task ReadTrack_ReturnsOnlyTheRequestedTrackAndSeeksOverOtherPayloads()
    {
        // Two 256 KiB video frames and two 64 KiB audio frames surround the subtitle blocks.
        // Reading them would cost over 600 KiB; skipping by seeking costs a few header bytes each.
        byte[] video = new byte[256 * 1024];
        byte[] audio = new byte[64 * 1024];
        using CountingStream stream = new(new MatroskaTestFile()
            .AddTrack(1, "V_MPEG4/ISO/AVC", type: 1)
            .AddTrack(2, "A_AAC", type: 2)
            .AddTrack(3, "S_TEXT/UTF8")
            .AddTrack(4, "S_TEXT/UTF8")
            .AddCluster(0,
                MatroskaTestFile.SimpleBlock(1, 0, video),
                MatroskaTestFile.SimpleBlock(2, 0, audio),
                MatroskaTestFile.BlockGroup(3, 10, "wanted one", 100),
                MatroskaTestFile.BlockGroup(4, 10, "other subtitle", 100))
            .AddCluster(1000,
                MatroskaTestFile.BlockGroup(1, 0, video, null),
                MatroskaTestFile.SimpleBlock(2, 0, audio),
                MatroskaTestFile.BlockGroup(3, 10, "wanted two", 100))
            .BuildStream());

        MatroskaTrackData? data = MatroskaSubtitleReader.ReadTrack(stream, 3);

        await Assert.That(data).IsNotNull();
        await Assert.That(data!.Blocks.Select(Text).ToArray()).IsEquivalentTo(new[] { "wanted one", "wanted two" });
        await Assert.That(stream.BytesRead).IsLessThan(8192L);
    }

    [Test]
    public async Task ReadTrack_HandlesUnknownSizeSegmentAndClusters()
    {
        // Each unknown-size cluster ends where the next Cluster (a sibling) or the Cues begin.
        using MemoryStream stream = new MatroskaTestFile { UnknownSizeSegment = true }
            .AddTrack(1, "V_MPEG4/ISO/AVC", type: 1)
            .AddTrack(2, "S_TEXT/UTF8")
            .AddUnknownSizeCluster(0,
                MatroskaTestFile.SimpleBlock(1, 0, new byte[300]),
                MatroskaTestFile.BlockGroup(2, 100, "in the first cluster", 500),
                Ebml.Binary(Ebml.VoidId, new byte[8]))
            .AddUnknownSizeCluster(2000,
                MatroskaTestFile.BlockGroup(2, 250, "in the second cluster", 500),
                MatroskaTestFile.SimpleBlock(1, 0, new byte[300]))
            .AddCluster(5000, MatroskaTestFile.BlockGroup(2, 0, "in a sized cluster", 500))
            .AddTrailing(Ebml.Element(Ebml.CuesId, Ebml.Binary(0xBB, new byte[4])))
            .BuildStream();

        MatroskaTrackData? data = MatroskaSubtitleReader.ReadTrack(stream, 2);

        await Assert.That(data).IsNotNull();
        await Assert.That(data!.IsComplete).IsTrue();
        await Assert.That(data.Blocks.Count).IsEqualTo(3);
        await Assert.That(Text(data.Blocks[0])).IsEqualTo("in the first cluster");
        await Assert.That(data.Blocks[0].StartNs).IsEqualTo(100L * 1_000_000);
        await Assert.That(Text(data.Blocks[1])).IsEqualTo("in the second cluster");
        await Assert.That(data.Blocks[1].StartNs).IsEqualTo(2250L * 1_000_000);
        await Assert.That(Text(data.Blocks[2])).IsEqualTo("in a sized cluster");
        await Assert.That(data.Blocks[2].StartNs).IsEqualTo(5000L * 1_000_000);
    }

    [Test]
    public async Task ReadTrack_InflatesZlibCompressedFrames()
    {
        byte[] compressed = Ebml.Zlib(Encoding.UTF8.GetBytes("0,0,Default,,0,0,0,,Compressed, with commas"));
        using MemoryStream stream = new MatroskaTestFile()
            .AddTrack(3, "S_TEXT/ASS", codecPrivate: AssHeader, contentEncodings: MatroskaTestFile.Compression(0))
            .AddCluster(0, MatroskaTestFile.BlockGroup(3, 0, compressed, 100))
            .BuildStream();

        MatroskaTrackData? data = MatroskaSubtitleReader.ReadTrack(stream, 3);

        await Assert.That(data).IsNotNull();
        await Assert.That(Text(data!.Blocks[0])).IsEqualTo("0,0,Default,,0,0,0,,Compressed, with commas");
        // Scope defaults to frames only, so the CodecPrivate is left as stored.
        await Assert.That(Encoding.UTF8.GetString(data.Track.CodecPrivate)).IsEqualTo(AssHeader);
    }

    [Test]
    public async Task ReadTrack_InflatesCodecPrivateWhenTheEncodingScopeCoversIt()
    {
        byte[] privateData = Ebml.Zlib(Encoding.UTF8.GetBytes(AssHeader));
        using MemoryStream stream = new MatroskaTestFile()
            .AddRawTrack(
                Ebml.Uint(Ebml.TrackNumberId, 3),
                Ebml.Uint(Ebml.TrackTypeId, 0x11),
                Ebml.String(Ebml.CodecIdId, "S_TEXT/ASS"),
                Ebml.Binary(Ebml.CodecPrivateId, privateData),
                Ebml.Element(Ebml.ContentEncodingsId, MatroskaTestFile.Compression(0, scope: 3)))
            .AddCluster(0, MatroskaTestFile.BlockGroup(3, 0, Ebml.Zlib(Encoding.UTF8.GetBytes("1,0,Default,,0,0,0,,hi")), 100))
            .BuildStream();

        MatroskaTrackData? data = MatroskaSubtitleReader.ReadTrack(stream, 3);

        await Assert.That(data).IsNotNull();
        await Assert.That(Encoding.UTF8.GetString(data!.Track.CodecPrivate)).IsEqualTo(AssHeader);
        await Assert.That(Text(data.Blocks[0])).IsEqualTo("1,0,Default,,0,0,0,,hi");
    }

    [Test]
    public async Task ReadTrack_PrependsHeaderStrippedBytes()
    {
        byte[] stripped = Encoding.UTF8.GetBytes("Hello, ");
        using MemoryStream stream = new MatroskaTestFile()
            .AddTrack(2, "S_TEXT/UTF8", contentEncodings: MatroskaTestFile.Compression(3, stripped))
            .AddCluster(0, MatroskaTestFile.BlockGroup(2, 0, "world", 100), MatroskaTestFile.BlockGroup(2, 200, "there", 100))
            .BuildStream();

        MatroskaTrackData? data = MatroskaSubtitleReader.ReadTrack(stream, 2);

        await Assert.That(data).IsNotNull();
        await Assert.That(data!.Blocks.Select(Text).ToArray()).IsEquivalentTo(new[] { "Hello, world", "Hello, there" });
    }

    [Test]
    public async Task ReadTrack_EncryptedTrackIsUnsupported()
    {
        using MemoryStream stream = new MatroskaTestFile()
            .AddTrack(2, "S_TEXT/UTF8", contentEncodings: MatroskaTestFile.Encryption())
            .AddCluster(0, MatroskaTestFile.BlockGroup(2, 0, "ciphertext", 100))
            .BuildStream();

        MatroskaHeader? header = MatroskaSubtitleReader.ReadHeader(stream);
        stream.Position = 0;
        MatroskaTrackData? data = MatroskaSubtitleReader.ReadTrack(stream, 2);

        await Assert.That(header!.Tracks[0].HasSupportedEncodings).IsFalse();
        await Assert.That(data).IsNull();
    }

    [Test]
    public async Task ReadTrack_SkipsLacedBlocks()
    {
        // Flags 0x82: keyframe + Xiph lacing. Laced subtitle frames are not supported, so the
        // block is skipped while its unlaced neighbours are still read.
        using MemoryStream stream = new MatroskaTestFile()
            .AddTrack(2, "S_TEXT/UTF8")
            .AddCluster(0,
                MatroskaTestFile.SimpleBlock(2, 0, "plain one"),
                MatroskaTestFile.SimpleBlock(2, 10, [0x01, 0x03, (byte)'a', (byte)'b', (byte)'c', (byte)'d'], flags: 0x82),
                MatroskaTestFile.SimpleBlock(2, 20, "plain two"))
            .BuildStream();

        MatroskaTrackData? data = MatroskaSubtitleReader.ReadTrack(stream, 2);

        await Assert.That(data!.Blocks.Select(Text).ToArray()).IsEquivalentTo(new[] { "plain one", "plain two" });
    }

    [Test]
    public async Task ReadTrack_ReturnsNullForATrackThatIsNotInTheFile()
    {
        using MemoryStream stream = new MatroskaTestFile()
            .AddTrack(2, "S_TEXT/UTF8")
            .AddCluster(0, MatroskaTestFile.BlockGroup(2, 0, "x", 100))
            .BuildStream();

        await Assert.That(MatroskaSubtitleReader.ReadTrack(stream, 9)).IsNull();
    }

    [Test]
    public async Task ReadTrack_TruncatedClusterReturnsThePartialResult()
    {
        byte[] file = new MatroskaTestFile()
            .AddTrack(2, "S_TEXT/UTF8")
            .AddCluster(0, MatroskaTestFile.BlockGroup(2, 0, "survives", 100))
            .AddCluster(1000, MatroskaTestFile.BlockGroup(2, 0, "cut off part-way through", 100))
            .Build();
        using MemoryStream stream = new(file, 0, file.Length - 10, writable: false);

        MatroskaTrackData? data = MatroskaSubtitleReader.ReadTrack(stream, 2);

        await Assert.That(data).IsNotNull();
        await Assert.That(data!.IsComplete).IsFalse();
        await Assert.That(data.Blocks.Select(Text).ToArray()).IsEquivalentTo(new[] { "survives" });
    }

    [Test]
    public async Task ReadTrack_TruncatedOrCorruptInputNeverThrows()
    {
        byte[] file = new MatroskaTestFile()
            .AddTrack(2, "S_TEXT/ASS", codecPrivate: AssHeader, name: "English")
            .AddCluster(0, MatroskaTestFile.BlockGroup(2, 0, "0,0,Default,,0,0,0,,line", 100))
            .Build();

        // Cut the file at every length, and also corrupt every byte in turn: the reader must come
        // back with null or a result, never an exception.
        for (int length = 0; length < file.Length; length++)
        {
            using MemoryStream truncated = new(file, 0, length, writable: false);
            _ = MatroskaSubtitleReader.ReadTrack(truncated, 2);
            truncated.Position = 0;
            _ = MatroskaSubtitleReader.ReadHeader(truncated);
        }

        for (int i = 0; i < file.Length; i++)
        {
            byte[] corrupt = (byte[])file.Clone();
            corrupt[i] ^= 0xFF;
            using MemoryStream stream = new(corrupt, writable: false);
            _ = MatroskaSubtitleReader.ReadTrack(stream, 2);
        }

        using MemoryStream empty = new();
        using MemoryStream notMatroska = new(Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nnot a matroska file\n"));
        await Assert.That(MatroskaSubtitleReader.ReadTrack(empty, 2)).IsNull();
        await Assert.That(MatroskaSubtitleReader.ReadTrack(notMatroska, 2)).IsNull();

        // Well-formed EBML carrying impossible values: a nine-byte unsigned integer in a track
        // header, and a cluster timestamp so large that adding the block offset overflows.
        using MemoryStream oversizedInteger = new MatroskaTestFile()
            .AddRawTrack(Ebml.Binary(Ebml.TrackNumberId, new byte[9]), Ebml.Uint(Ebml.TrackTypeId, 0x11))
            .BuildStream();
        byte[] hugeTimestamp = Ebml.Binary(Ebml.ClusterTimestampId, [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        using MemoryStream overflowingTime = new(new MatroskaTestFile()
            .AddTrack(2, "S_TEXT/UTF8")
            .AddCluster(0, MatroskaTestFile.BlockGroup(2, 0, "before", 100))
            .AddTrailing(Ebml.Element(Ebml.ClusterId, hugeTimestamp, MatroskaTestFile.BlockGroup(2, 10, "overflows", 100)))
            .Build());

        await Assert.That(MatroskaSubtitleReader.ReadTrack(oversizedInteger, 2)).IsNull();
        MatroskaTrackData? partial = MatroskaSubtitleReader.ReadTrack(overflowingTime, 2);
        await Assert.That(partial).IsNotNull();
        await Assert.That(partial!.IsComplete).IsFalse();
        await Assert.That(partial.Blocks.Select(Text).ToArray()).IsEquivalentTo(new[] { "before" });
    }

    [Test]
    public async Task ReadTrack_HonoursCancellation()
    {
        using MemoryStream stream = new MatroskaTestFile()
            .AddTrack(2, "S_TEXT/UTF8")
            .AddCluster(0, MatroskaTestFile.BlockGroup(2, 0, "x", 100))
            .BuildStream();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.That(() => MatroskaSubtitleReader.ReadTrack(stream, 2, cts.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    [Arguments("S_TEXT/ASS", true)]
    [Arguments("S_TEXT/SSA", true)]
    [Arguments("S_ASS", true)]
    [Arguments("S_SSA", true)]
    [Arguments("S_TEXT/UTF8", true)]
    [Arguments("S_HDMV/PGS", false)]
    [Arguments("S_VOBSUB", false)]
    [Arguments("S_TEXT/WEBVTT", false)]
    [Arguments("", false)]
    public async Task IsSupportedCodec_AcceptsOnlyAssSsaAndSrt(string codecId, bool expected)
    {
        await Assert.That(MatroskaSubtitleReader.IsSupportedCodec(codecId)).IsEqualTo(expected);
    }
}
