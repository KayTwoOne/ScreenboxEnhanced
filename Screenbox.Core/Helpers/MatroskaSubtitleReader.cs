using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using Screenbox.Core.Models;

namespace Screenbox.Core.Helpers;

/// <summary>
/// Reads one subtitle track out of a Matroska (MKV) file: its header and every one of its blocks.
/// </summary>
/// <remarks>
/// <para>
/// This exists because LibVLC 3 has no way to resize ASS subtitles: the resizer has to rebuild
/// the track as a standalone ASS file, and for that it needs the track's CodecPrivate and frames.
/// </para>
/// <para>
/// The reader is pure: it takes a seekable <see cref="Stream"/>, touches nothing else, and never
/// throws for bad input. A file that is not Matroska, or whose requested track is missing or
/// encrypted, gives <see langword="null"/>; a file that is truncated or corrupt part-way gives
/// whatever was read before the damage, with <see cref="MatroskaTrackData.IsComplete"/> unset.
/// Only <see cref="OperationCanceledException"/> escapes.
/// </para>
/// <para>
/// Subtitle blocks are interleaved with video and audio through the whole file, so the reader
/// walks every cluster. Other tracks' payloads are skipped by seeking past them, never read: for
/// each block only the few header bytes that name its track are read.
/// </para>
/// </remarks>
public static class MatroskaSubtitleReader
{
    /// <summary>
    /// Reads the timestamp scale and every track header, stopping at the first Cluster once the
    /// Tracks element has been seen. Nothing from the clusters is read.
    /// </summary>
    /// <param name="stream">A readable, seekable stream positioned anywhere; it is read from the start.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The header, or <see langword="null"/> when the stream is not a usable Matroska file.</returns>
    public static MatroskaHeader? ReadHeader(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek) return null;

        Parser parser = new(stream, targetTrack: null, cancellationToken);
        parser.Run();
        return parser.TracksSeen ? parser.BuildHeader() : null;
    }

    /// <summary>
    /// Reads one track's header and all of its blocks.
    /// </summary>
    /// <param name="stream">A readable, seekable stream positioned anywhere; it is read from the start.</param>
    /// <param name="trackNumber">The Matroska <c>TrackNumber</c> to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The track, or <see langword="null"/> when the stream is not a usable Matroska file, has no
    /// such track, or the track is encoded in a way that cannot be undone (encryption, for example).
    /// </returns>
    public static MatroskaTrackData? ReadTrack(Stream stream, ulong trackNumber, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek) return null;

        Parser parser = new(stream, trackNumber, cancellationToken);
        parser.Run();
        if (!parser.TracksSeen || parser.Target is null || !parser.IsTargetReadable) return null;

        return new MatroskaTrackData
        {
            Header = parser.BuildHeader(),
            Track = parser.Target,
            Blocks = parser.BuildBlocks(),
            IsComplete = parser.IsComplete,
        };
    }

    /// <summary>
    /// Returns whether a Matroska CodecID is one of the text formats the subtitle resizer supports:
    /// ASS and SSA under either of their two ids, and SRT (<c>S_TEXT/UTF8</c>).
    /// </summary>
    public static bool IsSupportedCodec(string codecId) => codecId switch
    {
        "S_TEXT/ASS" or "S_TEXT/SSA" or "S_ASS" or "S_SSA" or "S_TEXT/UTF8" => true,
        _ => false,
    };

    /// <summary>
    /// One pass over the file. Keeps what it has read in fields, so that when the file turns out
    /// to be truncated or corrupt the caller still gets everything that came before the damage.
    /// </summary>
    private sealed class Parser
    {
        private const uint EbmlHeaderId = 0x1A45DFA3;
        private const uint SegmentId = 0x18538067;
        private const uint SeekHeadId = 0x114D9B74;
        private const uint InfoId = 0x1549A966;
        private const uint TimestampScaleId = 0x2AD7B1;
        private const uint TracksId = 0x1654AE6B;
        private const uint TrackEntryId = 0xAE;
        private const uint TrackNumberId = 0xD7;
        private const uint TrackTypeId = 0x83;
        private const uint CodecIdId = 0x86;
        private const uint CodecPrivateId = 0x63A2;
        private const uint LanguageId = 0x22B59C;
        private const uint LanguageBcp47Id = 0x22B59D;
        private const uint NameId = 0x536E;
        private const uint FlagDefaultId = 0x88;
        private const uint DefaultDurationId = 0x23E383;
        private const uint ContentEncodingsId = 0x6D80;
        private const uint ContentEncodingId = 0x6240;
        private const uint ContentEncodingOrderId = 0x5031;
        private const uint ContentEncodingScopeId = 0x5032;
        private const uint ContentEncodingTypeId = 0x5033;
        private const uint ContentCompressionId = 0x5034;
        private const uint ContentCompAlgoId = 0x4254;
        private const uint ContentCompSettingsId = 0x4255;
        private const uint ClusterId = 0x1F43B675;
        private const uint ClusterTimestampId = 0xE7;
        private const uint SimpleBlockId = 0xA3;
        private const uint BlockGroupId = 0xA0;
        private const uint BlockId = 0xA1;
        private const uint BlockDurationId = 0x9B;
        private const uint CuesId = 0x1C53BB6B;
        private const uint AttachmentsId = 0x1941A469;
        private const uint ChaptersId = 0x1043A770;
        private const uint TagsId = 0x1254C367;

        // Limits for a single element read into memory. A subtitle frame or header is a few KB;
        // anything larger than this is corrupt, and must not be allowed to allocate gigabytes.
        private const long MaxBinarySize = 16 * 1024 * 1024;
        private const long MaxStringSize = 64 * 1024;

        private readonly Stream _stream;
        private readonly long _length;
        private readonly ulong? _targetTrack;
        private readonly CancellationToken _cancellationToken;
        private readonly List<MatroskaTrack> _tracks = new();
        private readonly HashSet<ulong> _undecodableTracks = new();
        private readonly List<(long StartUnits, long? DurationUnits, byte[] Payload)> _blocks = new();
        private long _timestampScale = MatroskaHeader.DefaultTimestampScale;
        private MatroskaContentEncoding[] _targetFrameDecoders = Array.Empty<MatroskaContentEncoding>();

        public Parser(Stream stream, ulong? targetTrack, CancellationToken cancellationToken)
        {
            _stream = stream;
            _length = stream.Length;
            _targetTrack = targetTrack;
            _cancellationToken = cancellationToken;
        }

        public bool TracksSeen { get; private set; }

        public MatroskaTrack? Target { get; private set; }

        public bool IsTargetReadable { get; private set; }

        public bool IsComplete { get; private set; }

        /// <summary>
        /// Whether blocks are being collected: the requested track is known, present and decodable.
        /// </summary>
        private bool IsCollecting => _targetTrack is not null && TracksSeen && Target is not null && IsTargetReadable;

        private long Position
        {
            get => _stream.Position;
            set => _stream.Position = value;
        }

        public MatroskaHeader BuildHeader() => new()
        {
            TimestampScale = _timestampScale,
            Tracks = _tracks.ToArray(),
        };

        public IReadOnlyList<MatroskaBlock> BuildBlocks()
        {
            long defaultDuration = Target?.DefaultDurationNs ?? 0;
            MatroskaBlock[] blocks = new MatroskaBlock[_blocks.Count];
            for (int i = 0; i < blocks.Length; i++)
            {
                (long startUnits, long? durationUnits, byte[] payload) = _blocks[i];
                long duration = durationUnits is { } units ? SaturatingMultiply(units, _timestampScale) : defaultDuration;
                blocks[i] = new MatroskaBlock(SaturatingMultiply(startUnits, _timestampScale), duration, payload);
            }

            return blocks;
        }

        /// <summary>
        /// Walks the file. Every failure other than cancellation ends the walk quietly, leaving
        /// <see cref="IsComplete"/> unset and whatever was read so far in place.
        /// </summary>
        public void Run()
        {
            try
            {
                Position = 0;
                if (!TryReadElementHeader(_length, out uint id, out long size, out long dataStart) ||
                    id != EbmlHeaderId || size < 0 || dataStart + size > _length)
                {
                    return;
                }

                Position = dataStart + size;
                while (Position < _length)
                {
                    if (!TryReadElementHeader(_length, out id, out size, out dataStart)) return;
                    if (id == SegmentId)
                    {
                        // An unknown-size Segment runs to the end of the file.
                        long end = size < 0 ? _length : dataStart + size;
                        IsComplete = ParseSegment(Math.Min(end, _length)) && end <= _length;
                        return;
                    }

                    if (size < 0) return;
                    Position = dataStart + size;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Truncated or corrupt input: keep what has been read. Nothing may escape to playback.
                IsComplete = false;
            }
        }

        /// <summary>Returns true when the segment was walked to its end without damage.</summary>
        private bool ParseSegment(long end)
        {
            while (Position < end)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (!TryReadElementHeader(end, out uint id, out long size, out long dataStart)) return false;

                switch (id)
                {
                    case ClusterId when TracksSeen && !IsCollecting:
                        // Nothing in the clusters is wanted: either only the header was asked
                        // for, or the requested track does not exist or cannot be decoded.
                        return true;

                    case ClusterId when size >= 0 && !TracksSeen:
                        // A cluster before the Tracks element (unusual, but legal) cannot be
                        // decoded yet, so it is skipped whole.
                        if (dataStart + size > end) return false;
                        Position = dataStart + size;
                        continue;

                    case ClusterId:
                        if (!ParseCluster(dataStart, size, end)) return false;
                        continue;

                    case EbmlHeaderId:
                    case SegmentId:
                        // A second, chained segment. Only the first is read.
                        return true;
                }

                if (size < 0 || dataStart + size > end) return false;

                switch (id)
                {
                    case InfoId:
                        ParseInfo(dataStart + size);
                        break;
                    case TracksId when !TracksSeen:
                        ParseTracks(dataStart + size);
                        break;
                }

                Position = dataStart + size;
            }

            return true;
        }

        private void ParseInfo(long end)
        {
            while (Position < end)
            {
                if (!TryReadChild(end, out uint id, out long size, out long dataStart)) return;
                if (id == TimestampScaleId)
                {
                    ulong scale = ReadUInt(size);
                    if (scale is > 0 and <= long.MaxValue) _timestampScale = (long)scale;
                }

                Position = dataStart + size;
            }
        }

        private void ParseTracks(long end)
        {
            while (Position < end)
            {
                if (!TryReadChild(end, out uint id, out long size, out long dataStart)) break;
                if (id == TrackEntryId)
                {
                    ParseTrackEntry(dataStart + size);
                }

                Position = dataStart + size;
            }

            // Only now is it known whether the requested track exists and can be decoded.
            TracksSeen = true;
            if (_targetTrack is { } number)
            {
                Target = _tracks.Find(track => track.Number == number);
                IsTargetReadable = Target is not null && Target.HasSupportedEncodings && !_undecodableTracks.Contains(number);
                if (Target is not null)
                {
                    _targetFrameDecoders = GetDecoders(Target, codecPrivate: false);
                }
            }
        }

        private void ParseTrackEntry(long end)
        {
            ulong number = 0, type = 0, defaultDuration = 0;
            string codecId = string.Empty, language = "eng", languageBcp47 = string.Empty, name = string.Empty;
            byte[] codecPrivate = Array.Empty<byte>();
            bool isDefault = true;
            List<MatroskaContentEncoding> encodings = new();

            while (Position < end)
            {
                if (!TryReadChild(end, out uint id, out long size, out long dataStart)) break;
                switch (id)
                {
                    case TrackNumberId: number = ReadUInt(size); break;
                    case TrackTypeId: type = ReadUInt(size); break;
                    case CodecIdId: codecId = ReadString(size); break;
                    case CodecPrivateId: codecPrivate = ReadBinary(size); break;
                    case LanguageId: language = ReadString(size); break;
                    case LanguageBcp47Id: languageBcp47 = ReadString(size); break;
                    case NameId: name = ReadString(size); break;
                    case FlagDefaultId: isDefault = ReadUInt(size) != 0; break;
                    case DefaultDurationId: defaultDuration = ReadUInt(size); break;
                    case ContentEncodingsId: ParseContentEncodings(dataStart + size, encodings); break;
                }

                Position = dataStart + size;
            }

            if (number == 0) return;

            MatroskaTrack track = new()
            {
                Number = number,
                Type = type,
                CodecId = codecId,
                CodecPrivate = codecPrivate,
                Language = language,
                LanguageBcp47 = languageBcp47,
                Name = name,
                IsDefault = isDefault,
                DefaultDurationNs = defaultDuration <= long.MaxValue ? (long)defaultDuration : 0,
                ContentEncodings = encodings.ToArray(),
            };

            // Undo any encoding scoped to the CodecPrivate now, so the header is ready to use.
            MatroskaContentEncoding[] privateDecoders = GetDecoders(track, codecPrivate: true);
            if (privateDecoders.Length > 0 && track.HasSupportedEncodings)
            {
                byte[]? decoded = Decode(codecPrivate, privateDecoders);
                if (decoded is null)
                {
                    _undecodableTracks.Add(number);
                }
                else
                {
                    track = new MatroskaTrack
                    {
                        Number = track.Number,
                        Type = track.Type,
                        CodecId = track.CodecId,
                        CodecPrivate = decoded,
                        Language = track.Language,
                        LanguageBcp47 = track.LanguageBcp47,
                        Name = track.Name,
                        IsDefault = track.IsDefault,
                        DefaultDurationNs = track.DefaultDurationNs,
                        ContentEncodings = track.ContentEncodings,
                    };
                }
            }

            _tracks.Add(track);
        }

        private void ParseContentEncodings(long end, List<MatroskaContentEncoding> encodings)
        {
            while (Position < end)
            {
                if (!TryReadChild(end, out uint id, out long size, out long dataStart)) return;
                if (id == ContentEncodingId)
                {
                    encodings.Add(ParseContentEncoding(dataStart + size));
                }

                Position = dataStart + size;
            }
        }

        private MatroskaContentEncoding ParseContentEncoding(long end)
        {
            // Defaults from the Matroska specification.
            ulong order = 0, scope = 1, type = MatroskaContentEncoding.TypeCompression, algorithm = MatroskaContentEncoding.AlgorithmZlib;
            byte[] settings = Array.Empty<byte>();

            while (Position < end)
            {
                if (!TryReadChild(end, out uint id, out long size, out long dataStart)) break;
                switch (id)
                {
                    case ContentEncodingOrderId: order = ReadUInt(size); break;
                    case ContentEncodingScopeId: scope = ReadUInt(size); break;
                    case ContentEncodingTypeId: type = ReadUInt(size); break;
                    case ContentCompressionId:
                        long compressionEnd = dataStart + size;
                        while (Position < compressionEnd)
                        {
                            if (!TryReadChild(compressionEnd, out uint childId, out long childSize, out long childStart)) break;
                            if (childId == ContentCompAlgoId) algorithm = ReadUInt(childSize);
                            else if (childId == ContentCompSettingsId) settings = ReadBinary(childSize);
                            Position = childStart + childSize;
                        }

                        break;
                }

                Position = dataStart + size;
            }

            return new MatroskaContentEncoding(order, scope, type, algorithm, settings);
        }

        /// <summary>
        /// Walks one cluster. A cluster of unknown size has no end marker: its children continue
        /// until an element that can only be a sibling or a parent appears, and that element is
        /// left for the segment loop to read. Returns false when the file is damaged.
        /// </summary>
        private bool ParseCluster(long dataStart, long size, long parentEnd)
        {
            bool isUnknownSize = size < 0;
            long end = isUnknownSize ? parentEnd : dataStart + size;

            // A cluster cut off by the end of the file still has whole blocks in it worth reading.
            bool isTruncated = end > parentEnd;
            if (isTruncated) end = parentEnd;

            ulong clusterTimestamp = 0;
            while (Position < end)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                long elementStart = Position;
                if (!TryReadElementHeader(end, out uint id, out long childSize, out long childStart)) return false;

                if (isUnknownSize && EndsUnknownSizeCluster(id))
                {
                    Position = elementStart;
                    return true;
                }

                if (childSize < 0 || childStart + childSize > end) return false;
                long childEnd = childStart + childSize;

                switch (id)
                {
                    case ClusterTimestampId:
                        clusterTimestamp = ReadUInt(childSize);
                        break;
                    case SimpleBlockId:
                        if (TryReadTargetBlock(childEnd, clusterTimestamp, out long start, out byte[]? payload))
                        {
                            _blocks.Add((start, null, payload));
                        }

                        break;
                    case BlockGroupId:
                        ParseBlockGroup(childEnd, clusterTimestamp);
                        break;
                }

                Position = childEnd;
            }

            return !isTruncated;
        }

        private void ParseBlockGroup(long end, ulong clusterTimestamp)
        {
            // BlockDuration may be written before or after the Block, so collect both first.
            long? duration = null;
            (long Start, byte[] Payload)? block = null;
            while (Position < end)
            {
                if (!TryReadChild(end, out uint id, out long size, out long dataStart)) return;
                switch (id)
                {
                    case BlockId:
                        if (TryReadTargetBlock(dataStart + size, clusterTimestamp, out long start, out byte[]? payload))
                        {
                            block = (start, payload);
                        }

                        break;
                    case BlockDurationId:
                        ulong value = ReadUInt(size);
                        duration = value <= long.MaxValue ? (long)value : null;
                        break;
                }

                Position = dataStart + size;
            }

            if (block is { } found)
            {
                _blocks.Add((found.Start, duration, found.Payload));
            }
        }

        /// <summary>
        /// Reads a Block or SimpleBlock body if it belongs to the target track. For any other track
        /// only the track number is read; the caller then seeks past the rest.
        /// </summary>
        private bool TryReadTargetBlock(long end, ulong clusterTimestamp, out long startUnits, [NotNullWhen(true)] out byte[]? payload)
        {
            startUnits = 0;
            payload = null;
            if (!IsCollecting || !TryReadVint(out long trackNumber, out _) || (ulong)trackNumber != _targetTrack) return false;

            Span<byte> header = stackalloc byte[3];
            _stream.ReadExactly(header);
            short offset = (short)((header[0] << 8) | header[1]);
            byte flags = header[2];

            // Lacing packs several frames into one block. Subtitles are never laced in practice,
            // so a laced block is skipped rather than split.
            if ((flags & 0x06) != 0) return false;

            long length = end - Position;
            if (length < 0 || length > MaxBinarySize) return false;
            byte[] data = new byte[length];
            _stream.ReadExactly(data);

            byte[]? decoded = Decode(data, _targetFrameDecoders);
            if (decoded is null) return false;

            startUnits = checked((long)clusterTimestamp + offset);
            payload = decoded;
            return true;
        }

        /// <summary>Returns the track's supported encodings for the given scope, in decoding order.</summary>
        private static MatroskaContentEncoding[] GetDecoders(MatroskaTrack track, bool codecPrivate)
        {
            List<MatroskaContentEncoding> decoders = new();
            foreach (MatroskaContentEncoding encoding in track.ContentEncodings)
            {
                if (codecPrivate ? encoding.AppliesToCodecPrivate : encoding.AppliesToFrames)
                {
                    decoders.Add(encoding);
                }
            }

            // The specification says decoding starts with the highest ContentEncodingOrder.
            decoders.Sort((a, b) => b.Order.CompareTo(a.Order));
            return decoders.ToArray();
        }

        /// <summary>Undoes the given encodings, or returns null when the data cannot be decoded.</summary>
        private static byte[]? Decode(byte[] data, MatroskaContentEncoding[] decoders)
        {
            foreach (MatroskaContentEncoding encoding in decoders)
            {
                switch (encoding)
                {
                    case { Type: MatroskaContentEncoding.TypeCompression, CompressionAlgorithm: MatroskaContentEncoding.AlgorithmZlib }:
                        byte[]? inflated = Inflate(data);
                        if (inflated is null) return null;
                        data = inflated;
                        break;
                    case { Type: MatroskaContentEncoding.TypeCompression, CompressionAlgorithm: MatroskaContentEncoding.AlgorithmHeaderStripping }:
                        byte[] joined = new byte[encoding.CompressionSettings.Length + data.Length];
                        encoding.CompressionSettings.CopyTo(joined, 0);
                        data.CopyTo(joined, encoding.CompressionSettings.Length);
                        data = joined;
                        break;
                    default:
                        return null;
                }
            }

            return data;
        }

        private static byte[]? Inflate(byte[] data)
        {
            try
            {
                using MemoryStream input = new(data, writable: false);
                using ZLibStream zlib = new(input, CompressionMode.Decompress);
                using MemoryStream output = new();
                byte[] buffer = new byte[16 * 1024];
                int read;
                while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    if (output.Length > MaxBinarySize) return null;
                }

                return output.ToArray();
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        private static bool EndsUnknownSizeCluster(uint id) => id is
            ClusterId or CuesId or TagsId or ChaptersId or AttachmentsId or SeekHeadId or InfoId or TracksId or
            EbmlHeaderId or SegmentId;

        /// <summary>Reads a child header of a known-size master element and checks it fits inside it.</summary>
        private bool TryReadChild(long end, out uint id, out long size, out long dataStart)
        {
            return TryReadElementHeader(end, out id, out size, out dataStart) && size >= 0 && dataStart + size <= end;
        }

        /// <summary>
        /// Reads an element ID and size. <paramref name="size"/> is -1 for the "unknown size"
        /// marker. Returns false at the end of the data or when the header is not valid EBML.
        /// </summary>
        private bool TryReadElementHeader(long end, out uint id, out long size, out long dataStart)
        {
            id = 0;
            size = 0;
            dataStart = Position;
            if (end - Position < 2) return false;

            int first = _stream.ReadByte();
            if (first <= 0) return false;
            int idLength = LeadingZeros((byte)first) + 1;
            if (idLength > 4) return false;
            uint value = (uint)first;
            for (int i = 1; i < idLength; i++)
            {
                int next = _stream.ReadByte();
                if (next < 0) return false;
                value = (value << 8) | (uint)next;
            }

            id = value;
            if (!TryReadVint(out size, out bool isUnknown)) return false;
            if (isUnknown) size = -1;
            dataStart = Position;
            return true;
        }

        /// <summary>Reads a variable-length integer (a size or a block's track number).</summary>
        private bool TryReadVint(out long value, out bool isUnknown)
        {
            value = 0;
            isUnknown = false;
            int first = _stream.ReadByte();
            if (first <= 0) return false;
            int length = LeadingZeros((byte)first) + 1;
            long result = first & (0xFF >> length);
            bool allOnes = result == 0xFF >> length;
            for (int i = 1; i < length; i++)
            {
                int next = _stream.ReadByte();
                if (next < 0) return false;
                result = (result << 8) | (uint)next;
                allOnes &= next == 0xFF;
            }

            isUnknown = allOnes;
            value = result;
            return true;
        }

        private ulong ReadUInt(long size)
        {
            if (size > 8) throw new InvalidDataException("Unsigned integer element is too long.");
            ulong result = 0;
            for (long i = 0; i < size; i++)
            {
                int next = _stream.ReadByte();
                if (next < 0) throw new EndOfStreamException();
                result = (result << 8) | (uint)next;
            }

            return result;
        }

        private string ReadString(long size)
        {
            if (size > MaxStringSize) return string.Empty;
            byte[] data = new byte[size];
            _stream.ReadExactly(data);
            return Encoding.UTF8.GetString(data).TrimEnd('\0');
        }

        private byte[] ReadBinary(long size)
        {
            if (size > MaxBinarySize) throw new InvalidDataException("Binary element is too large.");
            byte[] data = new byte[size];
            _stream.ReadExactly(data);
            return data;
        }

        private static int LeadingZeros(byte value)
        {
            int count = 0;
            for (int mask = 0x80; mask != 0 && (value & mask) == 0; mask >>= 1)
            {
                count++;
            }

            return count;
        }

        private static long SaturatingMultiply(long units, long scale)
        {
            try
            {
                return checked(units * scale);
            }
            catch (OverflowException)
            {
                return units < 0 ? long.MinValue : long.MaxValue;
            }
        }
    }
}
