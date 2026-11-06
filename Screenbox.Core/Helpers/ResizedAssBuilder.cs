using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Screenbox.Core.Models;

namespace Screenbox.Core.Helpers;

/// <summary>
/// Builds a standalone ASS file at a chosen size from a subtitle track read out of a Matroska file.
/// </summary>
/// <remarks>
/// <para>
/// LibVLC 3 cannot resize ASS subtitles itself, so the resizer plays this file in place of the
/// embedded track. <see cref="Parse"/> does the slow part once, and <see cref="Render"/> can then
/// be called for any size without touching the events again.
/// </para>
/// <para>
/// For ASS and SSA tracks only the Fontsize, Outline and Shadow of dialogue styles change. A style
/// counts as dialogue when fewer than half of its events carry a position override: positioned
/// styles are signs and karaoke, which are laid out against the picture and must keep their size.
/// Inline overrides such as <c>\fs</c> are left alone for the same reason.
/// </para>
/// <para>SRT tracks are converted to ASS with a single Default style.</para>
/// </remarks>
public static class ResizedAssBuilder
{
    private const string AssEventFormat = "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text";
    private const string SsaEventFormat = "Format: Marked, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text";
    private const string EventsSection = "[Events]";
    private const string DefaultStyleName = "Default";

    // Matroska stores each event as ReadOrder,Layer,Style,Name,MarginL,MarginR,MarginV,Effect,Text.
    // Text is the last field and may itself contain commas.
    private const int BlockFieldCount = 9;

    /// <summary>
    /// The header of a converted SRT track. A canvas of 1920x1080 keeps sizes independent of the
    /// video's resolution, as VLC's own SRT rendering is, and ScaledBorderAndShadow scales the
    /// outline with the picture too. The size and outline were chosen to look close to VLC's native
    /// SRT rendering, so that moving the slider off 100% is not a visible jump in style.
    /// </summary>
    private static readonly string[] SrtHeaderLines =
    [
        "[Script Info]",
        "ScriptType: v4.00+",
        "PlayResX: 1920",
        "PlayResY: 1080",
        "ScaledBorderAndShadow: yes",
        "WrapStyle: 0",
        "",
        "[V4+ Styles]",
        "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding",
        "Style: Default,Arial,75,&H00FFFFFF,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,2.5,4,2,60,60,58,1",
        "",
    ];

    /// <summary>
    /// Parses a track into a document that can be rendered at any size.
    /// </summary>
    /// <param name="data">The track, as read by <see cref="MatroskaSubtitleReader"/>.</param>
    /// <returns>The document, or <see langword="null"/> when the track's codec is not a supported text format.</returns>
    public static ResizableSubtitle? Parse(MatroskaTrackData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return data.Track.CodecId switch
        {
            "S_TEXT/ASS" or "S_ASS" => ParseAss(data, AssEventFormat),
            "S_TEXT/SSA" or "S_SSA" => ParseAss(data, SsaEventFormat),
            "S_TEXT/UTF8" => ParseSrt(data),
            _ => null,
        };
    }

    /// <summary>
    /// Renders a document as a complete ASS file.
    /// </summary>
    /// <param name="subtitle">The parsed track. It is not changed, so it can be rendered again.</param>
    /// <param name="scalePercent">The size, where 100 is the original size.</param>
    /// <returns>The text of the file, with <c>\n</c> line endings.</returns>
    public static string Render(ResizableSubtitle subtitle, int scalePercent)
    {
        ArgumentNullException.ThrowIfNull(subtitle);

        StringBuilder builder = new(subtitle.EventLines.Length + (subtitle.HeaderLines.Count * 64));
        bool isInStyles = false;
        StyleColumns? columns = null;
        foreach (string line in subtitle.HeaderLines)
        {
            if (IsSectionHeader(line))
            {
                isInStyles = line.Contains("Styles", StringComparison.OrdinalIgnoreCase);
                columns = null;
            }
            else if (isInStyles && line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                columns = StyleColumns.Parse(line);
            }
            else if (isInStyles && columns is not null && line.StartsWith("Style:", StringComparison.OrdinalIgnoreCase))
            {
                builder.Append(ScaleStyleLine(line, columns, subtitle.DialogueStyles, scalePercent)).Append('\n');
                continue;
            }

            builder.Append(line).Append('\n');
        }

        builder.Append(subtitle.EventLines);
        return builder.ToString();
    }

    /// <summary>
    /// Formats a time as <c>h:mm:ss.cc</c>. Truncates to centiseconds, as mkvextract does, so that
    /// the rebuilt file has the same timing as extracting the track with MKVToolNix.
    /// </summary>
    /// <param name="milliseconds">The time. A negative time is written as zero.</param>
    public static string FormatTimestamp(long milliseconds)
    {
        long time = Math.Max(0, milliseconds);
        return string.Create(CultureInfo.InvariantCulture,
            $"{time / 3_600_000}:{time / 60_000 % 60:00}:{time / 1000 % 60:00}.{time % 1000 / 10:00}");
    }

    /// <summary>
    /// Converts the text of an SRT event to ASS text: <c>i</c>, <c>b</c>, <c>u</c> and
    /// <c>font color</c> tags become override blocks, newlines become <c>\N</c>, and any other tag
    /// is removed. Text in braces passes through, because SRT files use <c>{\an8}</c> as ASS does.
    /// </summary>
    public static string ConvertSrtText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string trimmed = text.Trim('\r', '\n');
        StringBuilder result = new(trimmed.Length + 16);

        // Tracks which open font tags set a colour, because only those need a colour reset.
        Stack<bool> fonts = new();
        int position = 0;
        while (position < trimmed.Length)
        {
            char current = trimmed[position];
            switch (current)
            {
                case '\r':
                    if (position + 1 < trimmed.Length && trimmed[position + 1] == '\n') position++;
                    result.Append(@"\N");
                    position++;
                    break;
                case '\n':
                    result.Append(@"\N");
                    position++;
                    break;
                case '<' when TryReadTag(trimmed, position, out string tag, out int next):
                    result.Append(ConvertSrtTag(tag, fonts));
                    position = next;
                    break;
                default:
                    result.Append(current);
                    position++;
                    break;
            }
        }

        return result.ToString();
    }

    private static ResizableSubtitle ParseAss(MatroskaTrackData data, string eventFormat)
    {
        List<(long ReadOrder, string Style, string Text, string Line)> events = new(data.Blocks.Count);
        foreach (MatroskaBlock block in data.Blocks)
        {
            string payload = Encoding.UTF8.GetString(block.Payload);
            string[] fields = payload.Split(',', BlockFieldCount);
            if (fields.Length < BlockFieldCount) continue;

            // A file without a usable ReadOrder keeps its block order.
            long readOrder = long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
                ? parsed
                : events.Count;

            // VLC's subtitle demuxer drops a Dialogue line whose first field is empty.
            string layer = fields[1].Length == 0 ? "0" : fields[1];
            string line = BuildDialogueLine(block, layer, fields[2], fields[3], fields[4], fields[5], fields[6], fields[7], fields[8]);
            events.Add((readOrder, fields[2], fields[8], line));
        }

        // OrderBy is a stable sort, so events sharing a ReadOrder stay in block order.
        StringBuilder lines = new();
        foreach (var item in events.OrderBy(item => item.ReadOrder))
        {
            lines.Append(item.Line).Append('\n');
        }

        string header = Encoding.UTF8.GetString(data.Track.CodecPrivate);
        return new ResizableSubtitle(
            ArrangeHeader(header, eventFormat),
            FindDialogueStyles(events.Select(item => (item.Style, item.Text))),
            lines.ToString());
    }

    private static ResizableSubtitle ParseSrt(MatroskaTrackData data)
    {
        StringBuilder lines = new();
        List<(string Style, string Text)> events = new(data.Blocks.Count);
        foreach (MatroskaBlock block in data.Blocks)
        {
            string text = ConvertSrtText(Encoding.UTF8.GetString(block.Payload));
            if (text.Length == 0) continue;
            lines.Append(BuildDialogueLine(block, "0", DefaultStyleName, string.Empty, "0", "0", "0", string.Empty, text)).Append('\n');
            events.Add((DefaultStyleName, text));
        }

        List<string> header = new(SrtHeaderLines) { EventsSection, AssEventFormat };

        // SRT files carry no styles of their own, so the Default style is always the one to resize,
        // even when many lines of a track happen to be positioned with {\pos}.
        return new ResizableSubtitle(header, new HashSet<string>(StringComparer.Ordinal) { DefaultStyleName }, lines.ToString());
    }

    private static string BuildDialogueLine(
        MatroskaBlock block, string layer, string style, string name, string marginL, string marginR, string marginV, string effect, string text)
    {
        // mkvextract works in whole milliseconds: it drops the fraction of the start and of the
        // duration, then adds them. Doing the same keeps the output identical to its output.
        long startMs = block.StartNs / 1_000_000;
        long endMs = startMs + (block.DurationNs / 1_000_000);
        return $"Dialogue: {layer},{FormatTimestamp(startMs)},{FormatTimestamp(endMs)},{style},{name},{marginL},{marginR},{marginV},{effect},{text}";
    }

    /// <summary>
    /// Splits the CodecPrivate into lines, and moves the [Events] section to the end so the
    /// rebuilt events can follow it directly. Everything else stays as it was, in its order.
    /// </summary>
    private static List<string> ArrangeHeader(string header, string eventFormat)
    {
        List<string> lines = new();
        List<string> eventsSection = new();
        bool isInEvents = false;
        foreach (string raw in header.TrimStart('\uFEFF').TrimEnd('\0').Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (IsSectionHeader(line))
            {
                isInEvents = line.Equals(EventsSection, StringComparison.OrdinalIgnoreCase);
                if (isInEvents) continue;
            }

            if (!isInEvents)
            {
                lines.Add(line);
            }
            else if (line.Length > 0 && !line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                // Comment lines. The original Format line is replaced below, because the rebuilt
                // events follow the fixed Matroska layout whatever order it listed the columns in.
                eventsSection.Add(line);
            }
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        lines.Add(string.Empty);
        lines.Add(EventsSection);
        lines.Add(eventFormat);
        lines.AddRange(eventsSection);
        return lines;
    }

    private static HashSet<string> FindDialogueStyles(IEnumerable<(string Style, string Text)> events)
    {
        Dictionary<string, (int Total, int Positioned)> counts = new(StringComparer.Ordinal);
        foreach ((string style, string text) in events)
        {
            string name = NormaliseStyleName(style);
            counts.TryGetValue(name, out (int Total, int Positioned) count);
            counts[name] = (count.Total + 1, count.Positioned + (HasPositionOverride(text) ? 1 : 0));
        }

        HashSet<string> dialogue = new(StringComparer.Ordinal);
        foreach ((string name, (int total, int positioned)) in counts)
        {
            if (positioned * 2 < total) dialogue.Add(name);
        }

        return dialogue;
    }

    /// <summary>Returns whether the text has a <c>\pos</c>, <c>\move</c> or <c>\org</c> tag in an override block.</summary>
    private static bool HasPositionOverride(string text)
    {
        int open = text.IndexOf('{');
        while (open >= 0)
        {
            int close = text.IndexOf('}', open + 1);
            if (close < 0) return false;
            ReadOnlySpan<char> block = text.AsSpan(open + 1, close - open - 1);
            if (HasTag(block, @"\pos") || HasTag(block, @"\move") || HasTag(block, @"\org")) return true;
            open = text.IndexOf('{', close + 1);
        }

        return false;
    }

    private static bool HasTag(ReadOnlySpan<char> block, string tag)
    {
        while (true)
        {
            int index = block.IndexOf(tag, StringComparison.Ordinal);
            if (index < 0) return false;

            // These tags always take arguments, which also tells \pos( apart from any longer name.
            block = block[(index + tag.Length)..];
            ReadOnlySpan<char> rest = block.TrimStart();
            if (rest.Length > 0 && rest[0] == '(') return true;
        }
    }

    private static string NormaliseStyleName(string style) => style.Trim().TrimStart('*');

    private static bool IsSectionHeader(string line) => line.StartsWith('[') && line.TrimEnd().EndsWith(']');

    private static string ScaleStyleLine(string line, StyleColumns columns, IReadOnlySet<string> dialogueStyles, int scalePercent)
    {
        if (scalePercent == SubtitleScaleRange.Default) return line;

        int colon = line.IndexOf(':');
        string[] fields = line[(colon + 1)..].Split(',');
        if (columns.Name >= fields.Length) return line;
        if (!dialogueStyles.Contains(NormaliseStyleName(fields[columns.Name]))) return line;

        ScaleField(fields, columns.FontSize, scalePercent);
        ScaleField(fields, columns.Outline, scalePercent);
        ScaleField(fields, columns.Shadow, scalePercent);
        return line[..(colon + 1)] + string.Join(',', fields);
    }

    private static void ScaleField(string[] fields, int column, int scalePercent)
    {
        if (column < 0 || column >= fields.Length) return;
        if (!double.TryParse(fields[column], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return;

        // Keep the field's leading space, if it has one, so that nothing else about the line moves.
        string prefix = fields[column].StartsWith(' ') ? " " : string.Empty;
        double scaled = value * scalePercent / 100d;
        fields[column] = prefix + scaled.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>Reads a <c>&lt;tag&gt;</c> at the position, if what is there looks like a tag.</summary>
    private static bool TryReadTag(string text, int position, out string tag, out int next)
    {
        tag = string.Empty;
        next = position;

        // "a < b" is ordinary text: a tag starts with a letter, or with a slash and then a letter.
        int nameStart = position + 1;
        if (nameStart < text.Length && text[nameStart] == '/') nameStart++;
        if (nameStart >= text.Length || !char.IsAsciiLetter(text[nameStart])) return false;

        int close = text.IndexOf('>', nameStart);
        if (close < 0) return false;

        tag = text[(position + 1)..close];
        next = close + 1;
        return true;
    }

    private static string ConvertSrtTag(string tag, Stack<bool> fonts)
    {
        bool isClosing = tag.StartsWith('/');
        string body = isClosing ? tag[1..] : tag;
        int nameEnd = body.AsSpan().IndexOfAny(' ', '\t', '/');
        string name = (nameEnd < 0 ? body : body[..nameEnd]).ToLowerInvariant();
        switch (name)
        {
            case "i": return isClosing ? @"{\i0}" : @"{\i1}";
            case "b": return isClosing ? @"{\b0}" : @"{\b1}";
            case "u": return isClosing ? @"{\u0}" : @"{\u1}";
            case "font" when isClosing:
                return fonts.TryPop(out bool hadColour) && hadColour ? @"{\c}" : string.Empty;
            case "font":
                string? colour = FindFontColour(body);
                fonts.Push(colour is not null);
                return colour is null ? string.Empty : $@"{{\c&H{colour}&}}";
            default:
                return string.Empty;
        }
    }

    /// <summary>Finds a <c>color="#rrggbb"</c> attribute and returns it in ASS order, BBGGRR.</summary>
    private static string? FindFontColour(string tag)
    {
        int attribute = tag.IndexOf("color", StringComparison.OrdinalIgnoreCase);
        if (attribute < 0) return null;

        ReadOnlySpan<char> value = tag.AsSpan(attribute + "color".Length).TrimStart();
        if (value.Length == 0 || value[0] != '=') return null;
        value = value[1..].TrimStart();
        if (value.Length > 0 && value[0] is '"' or '\'') value = value[1..];
        if (value.Length == 0 || value[0] != '#') return null;
        value = value[1..];

        if (value.Length < 6) return null;
        ReadOnlySpan<char> hex = value[..6];
        foreach (char digit in hex)
        {
            if (!char.IsAsciiHexDigit(digit)) return null;
        }

        return string.Concat(hex[4..6], hex[2..4], hex[..2]).ToUpperInvariant();
    }

    /// <summary>The positions of the style columns the resizer changes, from a Styles Format line.</summary>
    private sealed record StyleColumns(int Name, int FontSize, int Outline, int Shadow)
    {
        public static StyleColumns? Parse(string formatLine)
        {
            string[] columns = formatLine[(formatLine.IndexOf(':') + 1)..].Split(',');
            int name = -1, fontSize = -1, outline = -1, shadow = -1;
            for (int i = 0; i < columns.Length; i++)
            {
                string column = columns[i].Trim();
                if (column.Equals("Name", StringComparison.OrdinalIgnoreCase)) name = i;
                else if (column.Equals("Fontsize", StringComparison.OrdinalIgnoreCase)) fontSize = i;
                else if (column.Equals("Outline", StringComparison.OrdinalIgnoreCase)) outline = i;
                else if (column.Equals("Shadow", StringComparison.OrdinalIgnoreCase)) shadow = i;
            }

            return name < 0 ? null : new StyleColumns(name, fontSize, outline, shadow);
        }
    }
}
