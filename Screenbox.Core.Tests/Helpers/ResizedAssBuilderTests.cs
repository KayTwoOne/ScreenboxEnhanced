using System.Globalization;
using System.Text;
using Screenbox.Core.Helpers;
using Screenbox.Core.Models;

namespace Screenbox.Core.Tests.Helpers;

public sealed class ResizedAssBuilderTests
{
    private const string StyleFormat =
        "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding";

    private const string EventFormat = "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text";

    private static string Header(params string[] styleLines) =>
        "[Script Info]\nTitle: Test\nScriptType: v4.00+\nPlayResX: 1920\nPlayResY: 1080\n\n[V4+ Styles]\n" + StyleFormat + "\n" +
        string.Join("\n", styleLines) + "\n\n[Events]\n" + EventFormat + "\n";

    private static string Style(string name, string fontSize = "60", string outline = "3", string shadow = "1.5") =>
        $"Style: {name},Arial,{fontSize},&H00FFFFFF,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,{outline},{shadow},2,40,40,40,1";

    private static MatroskaBlock Block(long startMs, long durationMs, string payload) =>
        new(startMs * 1_000_000, durationMs * 1_000_000, Encoding.UTF8.GetBytes(payload));

    private static MatroskaTrackData Track(string codecId, string? codecPrivate, params MatroskaBlock[] blocks) => new()
    {
        Header = new MatroskaHeader(),
        Track = new MatroskaTrack
        {
            Number = 3,
            Type = MatroskaTrack.SubtitleTrackType,
            CodecId = codecId,
            CodecPrivate = codecPrivate is null ? [] : Encoding.UTF8.GetBytes(codecPrivate),
        },
        Blocks = blocks,
        IsComplete = true,
    };

    private static string Render(MatroskaTrackData data, int scale) =>
        ResizedAssBuilder.Render(ResizedAssBuilder.Parse(data)!, scale);

    private static string[] Lines(string document) => document.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string[] DialogueLines(string document) =>
        Lines(document).Where(line => line.StartsWith("Dialogue: ", StringComparison.Ordinal)).ToArray();

    private static string StyleLine(string document, string name) =>
        Lines(document).Single(line => line.StartsWith($"Style: {name},", StringComparison.Ordinal));

    // ------------------------------------------------------------------
    // Events
    // ------------------------------------------------------------------

    [Test]
    public async Task Render_RebuildsDialogueLineFromThePayloadAndBlockTiming()
    {
        string document = Render(Track("S_TEXT/ASS", Header(Style("Main")),
            Block(1500, 2250, "3,1,Main,Narrator,10,20,30,Banner;5,Hello")), 100);

        await Assert.That(DialogueLines(document)).IsEquivalentTo(new[]
        {
            "Dialogue: 1,0:00:01.50,0:00:03.75,Main,Narrator,10,20,30,Banner;5,Hello",
        });
    }

    [Test]
    public async Task Render_KeepsCommasInsideTheTextField()
    {
        string document = Render(Track("S_TEXT/ASS", Header(Style("Main")),
            Block(0, 1000, @"0,0,Main,,0,0,0,,One, two, three, {\i1}four{\i0}, five")), 100);

        await Assert.That(DialogueLines(document)[0]).IsEqualTo(
            @"Dialogue: 0,0:00:00.00,0:00:01.00,Main,,0,0,0,,One, two, three, {\i1}four{\i0}, five");
    }

    [Test]
    public async Task Render_OrdersEventsByReadOrder()
    {
        // Blocks are stored by time. Overlapping lines stack by the order they appear in the
        // script, so the original order has to come back.
        string document = Render(Track("S_TEXT/ASS", Header(Style("Main")),
            Block(1000, 1000, "2,0,Main,,0,0,0,,third"),
            Block(2000, 1000, "0,0,Main,,0,0,0,,first"),
            Block(3000, 1000, "10,0,Main,,0,0,0,,fourth"),
            Block(4000, 1000, "1,0,Main,,0,0,0,,second")), 100);

        string[] texts = DialogueLines(document).Select(line => line[(line.LastIndexOf(',') + 1)..]).ToArray();
        await Assert.That(texts).IsEquivalentTo(new[] { "first", "second", "third", "fourth" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(0L, "0:00:00.00")]
    [Arguments(1234L, "0:00:01.23")]
    [Arguments(1235L, "0:00:01.23")] // mkvextract truncates: 1.235 s is written .23, not .24
    [Arguments(1239L, "0:00:01.23")]
    [Arguments(1999L, "0:00:01.99")]
    [Arguments(61_000L, "0:01:01.00")]
    [Arguments(3_661_990L, "1:01:01.99")]
    [Arguments(36_000_000L, "10:00:00.00")]
    [Arguments(-500L, "0:00:00.00")]
    public async Task FormatTimestamp_TruncatesToCentisecondsLikeMkvextract(long milliseconds, string expected)
    {
        await Assert.That(ResizedAssBuilder.FormatTimestamp(milliseconds)).IsEqualTo(expected);
    }

    [Test]
    public async Task Render_ComputesTheEndInMillisecondsBeforeTruncatingLikeMkvextract()
    {
        // Each row below was measured against mkvextract's output for a synthetic file.
        MatroskaBlock[] blocks =
        [
            Block(1235, 1006, "0,0,Main,,0,0,0,,a"),    // 2241 ms: end .24
            Block(1235, 1004, "1,0,Main,,0,0,0,,b"),    // 2239 ms: end .23
            Block(1999, 1, "2,0,Main,,0,0,0,,c"),       // 2000 ms: end 2.00
            // 1235.9 ms + 1004.2 ms is 2240.1 ms exactly, but mkvextract drops the fraction of
            // each before adding them: 1235 + 1004 = 2239, so the end is .23 rather than .24.
            new MatroskaBlock(1_235_900_000, 1_004_200_000, Encoding.UTF8.GetBytes("3,0,Main,,0,0,0,,d")),
        ];

        string[] lines = DialogueLines(Render(Track("S_TEXT/ASS", Header(Style("Main")), blocks), 100));

        await Assert.That(lines[0]).StartsWith("Dialogue: 0,0:00:01.23,0:00:02.24,");
        await Assert.That(lines[1]).StartsWith("Dialogue: 0,0:00:01.23,0:00:02.23,");
        await Assert.That(lines[2]).StartsWith("Dialogue: 0,0:00:01.99,0:00:02.00,");
        await Assert.That(lines[3]).StartsWith("Dialogue: 0,0:00:01.23,0:00:02.23,");
    }

    [Test]
    public async Task Render_WritesAnEmptyLayerFieldAsZero()
    {
        // VLC's subtitle demuxer drops a Dialogue line whose first field is empty.
        string document = Render(Track("S_TEXT/ASS", Header(Style("Main")), Block(0, 1000, "0,,Main,,0,0,0,,text")), 100);

        await Assert.That(DialogueLines(document)[0]).StartsWith("Dialogue: 0,0:00:00.00,");
    }

    [Test]
    public async Task Render_SkipsPayloadsWithTooFewFields()
    {
        string document = Render(Track("S_TEXT/ASS", Header(Style("Main")),
            Block(0, 1000, "not an event"),
            Block(1000, 1000, "1,0,Main,,0,0,0,,kept")), 100);

        await Assert.That(DialogueLines(document).Length).IsEqualTo(1);
        await Assert.That(DialogueLines(document)[0]).EndsWith(",kept");
    }

    [Test]
    public async Task Parse_ReturnsNullForUnsupportedCodecs()
    {
        await Assert.That(ResizedAssBuilder.Parse(Track("S_HDMV/PGS", null, Block(0, 1000, "x")))).IsNull();
        await Assert.That(ResizedAssBuilder.Parse(Track("S_VOBSUB", "size: 720x480", Block(0, 1000, "x")))).IsNull();
    }

    // ------------------------------------------------------------------
    // Header
    // ------------------------------------------------------------------

    [Test]
    public async Task Render_KeepsTheHeaderSectionsAndCommentLines()
    {
        string header =
            "\uFEFF[Script Info]\r\n; Script generated by Aegisub\r\nTitle: Death Note\r\nPlayResY: 1080\r\n\r\n" +
            "[Aegisub Project Garbage]\r\nActive Line: 473\r\n\r\n" +
            "[V4+ Styles]\r\n" + StyleFormat + "\r\n" + Style("Main") + "\r\n\r\n" +
            "[Fonts]\r\nfontname: Cabin.ttf\r\nabcdef\r\n\r\n" +
            "[Graphics]\r\nfilename: logo.png\r\nghijkl\r\n\r\n" +
            "[Events]\r\n" + EventFormat + "\r\nComment: 0,0:00:00.00,0:00:00.00,Main,,0,0,0,,OPENING\r\n";

        string[] lines = Lines(Render(Track("S_TEXT/ASS", header, Block(0, 1000, "0,0,Main,,0,0,0,,text")), 100));

        await Assert.That(lines[0]).IsEqualTo("[Script Info]");
        await Assert.That(lines).Contains("; Script generated by Aegisub");
        await Assert.That(lines).Contains("Title: Death Note");
        await Assert.That(lines).Contains("[Aegisub Project Garbage]");
        await Assert.That(lines).Contains("Active Line: 473");
        await Assert.That(lines).Contains("fontname: Cabin.ttf");
        await Assert.That(lines).Contains("abcdef");
        await Assert.That(lines).Contains("filename: logo.png");
        await Assert.That(lines).Contains("ghijkl");
        await Assert.That(lines).Contains("Comment: 0,0:00:00.00,0:00:00.00,Main,,0,0,0,,OPENING");
        await Assert.That(lines).Contains(Style("Main"));
        await Assert.That(lines.Any(line => line.Contains('\r'))).IsFalse();
    }

    [Test]
    public async Task Render_PutsTheEventsSectionLastEvenWhenTheHeaderHasSectionsAfterIt()
    {
        string header =
            "[Script Info]\nTitle: Test\n\n[V4+ Styles]\n" + StyleFormat + "\n" + Style("Main") + "\n\n" +
            "[Events]\n" + EventFormat + "\n\n[Fonts]\nfontname: Cabin.ttf\n";

        string[] lines = Lines(Render(Track("S_TEXT/ASS", header, Block(0, 1000, "0,0,Main,,0,0,0,,text")), 100));

        int fonts = Array.IndexOf(lines, "[Fonts]");
        int events = Array.IndexOf(lines, "[Events]");
        await Assert.That(fonts).IsGreaterThanOrEqualTo(0);
        await Assert.That(events).IsGreaterThan(fonts);
        await Assert.That(lines[events + 1]).IsEqualTo(EventFormat);
        await Assert.That(lines[^1]).StartsWith("Dialogue: ");
    }

    [Test]
    public async Task Render_AddsAnEventsSectionWhenTheHeaderHasNone()
    {
        string header = "[Script Info]\nTitle: Test\n\n[V4+ Styles]\n" + StyleFormat + "\n" + Style("Main") + "\n";

        string[] lines = Lines(Render(Track("S_TEXT/ASS", header, Block(0, 1000, "0,0,Main,,0,0,0,,text")), 100));

        int events = Array.IndexOf(lines, "[Events]");
        await Assert.That(events).IsGreaterThan(0);
        await Assert.That(lines[events + 1]).IsEqualTo(EventFormat);
    }

    [Test]
    public async Task Render_WritesAFormatLineMatchingTheRebuiltDialogueLines()
    {
        // The block layout is fixed, whatever order the original Format line used. The rebuilt
        // lines follow the block layout, so the Format line has to describe that layout.
        string header =
            "[Script Info]\nTitle: Test\n\n[V4+ Styles]\n" + StyleFormat + "\n" + Style("Main") + "\n\n" +
            "[Events]\nFormat: Start, End, Layer, Style, Actor, MarginL, MarginR, MarginV, Effect, Text\n";

        string[] lines = Lines(Render(Track("S_TEXT/ASS", header, Block(0, 1000, "0,4,Main,Bob,0,0,0,,text")), 100));

        await Assert.That(lines).Contains(EventFormat);
        await Assert.That(lines).DoesNotContain("Format: Start, End, Layer, Style, Actor, MarginL, MarginR, MarginV, Effect, Text");
        await Assert.That(DialogueLines(string.Join('\n', lines))[0]).IsEqualTo("Dialogue: 4,0:00:00.00,0:00:01.00,Main,Bob,0,0,0,,text");
    }

    [Test]
    public async Task Render_AtOneHundredPercentLeavesStyleLinesExactlyAsTheyWere()
    {
        // Numbers written in a way that passing through a parse and format would change.
        string style = Style("Main", "77.50", "4.2755", "1.80");

        string document = Render(Track("S_TEXT/ASS", Header(style), Block(0, 1000, "0,0,Main,,0,0,0,,text")), 100);

        await Assert.That(StyleLine(document, "Main")).IsEqualTo(style);
    }

    // ------------------------------------------------------------------
    // Style scaling
    // ------------------------------------------------------------------

    [Test]
    public async Task Render_ScalesFontsizeOutlineAndShadowOfDialogueStyles()
    {
        string document = Render(Track("S_TEXT/ASS", Header(Style("Main", "60", "3", "1.5")),
            Block(0, 1000, "0,0,Main,,0,0,0,,text")), 150);

        await Assert.That(StyleLine(document, "Main")).IsEqualTo(Style("Main", "90", "4.5", "2.25"));
    }

    [Test]
    public async Task Render_FindsStyleColumnsFromTheFormatLine()
    {
        // Same columns, shuffled, and with the numbers placed where a fixed-order parser would
        // scale the wrong things: 11 and 22 sit in the standard Fontsize and Outline positions.
        const string format = "Format: Name, Shadow, Fontname, Fontsize, MarginV, Outline, Bold";
        string header =
            "[Script Info]\nTitle: Test\n\n[V4+ Styles]\n" + format + "\nStyle: Main,2,Arial,40,11,6,22\n\n" +
            "[Events]\n" + EventFormat + "\n";

        string document = Render(Track("S_TEXT/ASS", header, Block(0, 1000, "0,0,Main,,0,0,0,,text")), 200);

        await Assert.That(StyleLine(document, "Main")).IsEqualTo("Style: Main,4,Arial,80,11,12,22");
    }

    [Test]
    public async Task Render_LeavesStylesAloneWhenTheStylesSectionHasNoFormatLine()
    {
        string header =
            "[Script Info]\nTitle: Test\n\n[V4+ Styles]\n" + Style("Main") + "\n\n[Events]\n" + EventFormat + "\n";

        string document = Render(Track("S_TEXT/ASS", header, Block(0, 1000, "0,0,Main,,0,0,0,,text")), 200);

        await Assert.That(StyleLine(document, "Main")).IsEqualTo(Style("Main"));
    }

    [Test]
    public async Task Render_LeavesPositionedStylesUnscaled()
    {
        string document = Render(Track("S_TEXT/ASS", Header(Style("Dialogue"), Style("Sign"), Style("Move"), Style("Org"), Style("Half"), Style("Unused")),
            // One positioned line out of three is still dialogue: fewer than half.
            Block(0, 1000, @"0,0,Dialogue,,0,0,0,,one"),
            Block(0, 1000, @"1,0,Dialogue,,0,0,0,,two"),
            Block(0, 1000, @"2,0,Dialogue,,0,0,0,,{\pos(10,10)}three"),
            Block(0, 1000, @"3,0,Sign,,0,0,0,,{\an8\pos(100,200)}sign"),
            Block(0, 1000, @"4,0,Move,,0,0,0,,{\move(0,0,10,10)}moving"),
            Block(0, 1000, @"5,0,Org,,0,0,0,,{\org(5,5)\frz10}rotating"),
            // Exactly half is not fewer than half.
            Block(0, 1000, @"6,0,Half,,0,0,0,,plain"),
            Block(0, 1000, @"7,0,Half,,0,0,0,,{\pos(10,10)}positioned")), 200);

        await Assert.That(StyleLine(document, "Dialogue")).IsEqualTo(Style("Dialogue", "120", "6", "3"));
        await Assert.That(StyleLine(document, "Sign")).IsEqualTo(Style("Sign"));
        await Assert.That(StyleLine(document, "Move")).IsEqualTo(Style("Move"));
        await Assert.That(StyleLine(document, "Org")).IsEqualTo(Style("Org"));
        await Assert.That(StyleLine(document, "Half")).IsEqualTo(Style("Half"));
        await Assert.That(StyleLine(document, "Unused")).IsEqualTo(Style("Unused"));
    }

    [Test]
    public async Task Render_OnlyCountsPositionTagsInsideOverrideBlocks()
    {
        // The text itself may mention these words. Outside {...} they are not override tags, even
        // on a line that has a harmless override block elsewhere.
        string document = Render(Track("S_TEXT/ASS", Header(Style("Main")),
            Block(0, 1000, @"0,0,Main,,0,0,0,,{\i1}typing \pos(1,2) as text{\i0}")), 200);

        await Assert.That(StyleLine(document, "Main")).IsEqualTo(Style("Main", "120", "6", "3"));
    }

    [Test]
    public async Task Render_MatchesStyleNamesWrittenWithALeadingAsterisk()
    {
        // Old scripts write "*Default". libass treats it as the same style.
        string document = Render(Track("S_TEXT/ASS", Header(Style("*Default")),
            Block(0, 1000, "0,0,Default,,0,0,0,,text")), 200);

        await Assert.That(StyleLine(document, "*Default")).IsEqualTo(Style("*Default", "120", "6", "3"));
    }

    [Test]
    public async Task Render_LeavesInlineFontSizeOverridesUntouched()
    {
        const string text = @"{\fs40}big{\fs20} and small";

        string document = Render(Track("S_TEXT/ASS", Header(Style("Main")), Block(0, 1000, "0,0,Main,,0,0,0,," + text)), 200);

        await Assert.That(DialogueLines(document)[0]).EndsWith(",," + text);
    }

    [Test]
    public async Task Render_WritesNumbersWithAFullStopWhateverTheCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            string document = Render(Track("S_TEXT/ASS", Header(Style("Main", "60", "4.275", "1.5")),
                Block(0, 1000, "0,0,Main,,0,0,0,,text")), 150);

            await Assert.That(StyleLine(document, "Main")).IsEqualTo(Style("Main", "90", "6.413", "2.25"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Test]
    public async Task Render_CanBeCalledAtDifferentSizesFromTheSameDocument()
    {
        ResizableSubtitle subtitle = ResizedAssBuilder.Parse(Track("S_TEXT/ASS", Header(Style("Main")), Block(0, 1000, "0,0,Main,,0,0,0,,text")))!;

        string first = ResizedAssBuilder.Render(subtitle, 100);
        string large = ResizedAssBuilder.Render(subtitle, 250);
        string again = ResizedAssBuilder.Render(subtitle, 100);

        await Assert.That(StyleLine(large, "Main")).IsEqualTo(Style("Main", "150", "7.5", "3.75"));
        await Assert.That(again).IsEqualTo(first);
    }

    [Test]
    public async Task Render_ScalesSsaStylesAndUsesTheSsaFormatLine()
    {
        string header =
            "[Script Info]\nScriptType: v4.00\n\n[V4 Styles]\n" +
            "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, TertiaryColour, BackColour, Bold, Italic, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, AlphaLevel, Encoding\n" +
            "Style: Main,Arial,40,65535,255,0,0,0,0,1,2,1,2,10,10,10,0,1\n";

        string document = Render(Track("S_TEXT/SSA", header, Block(0, 1000, "0,0,Main,,0,0,0,,text")), 200);

        await Assert.That(StyleLine(document, "Main")).IsEqualTo("Style: Main,Arial,80,65535,255,0,0,0,0,1,4,2,2,10,10,10,0,1");
        await Assert.That(Lines(document)).Contains("Format: Marked, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
        await Assert.That(DialogueLines(document).Length).IsEqualTo(1);
    }

    [Test]
    [Arguments("S_TEXT/ASS")]
    [Arguments("S_ASS")]
    [Arguments("S_TEXT/SSA")]
    [Arguments("S_SSA")]
    public async Task Parse_AcceptsEveryAssAndSsaCodecId(string codecId)
    {
        await Assert.That(ResizedAssBuilder.Parse(Track(codecId, Header(Style("Main")), Block(0, 1000, "0,0,Main,,0,0,0,,text")))).IsNotNull();
    }

    // ------------------------------------------------------------------
    // SRT
    // ------------------------------------------------------------------

    [Test]
    [Arguments("<i>a</i> <b>b</b> <u>c</u>", @"{\i1}a{\i0} {\b1}b{\b0} {\u1}c{\u0}")]
    [Arguments("<I>a</I><B>b</B><U>c</U>", @"{\i1}a{\i0}{\b1}b{\b0}{\u1}c{\u0}")]
    [Arguments("<font color=\"#FF8000\">x</font>", @"{\c&H0080FF&}x{\c}")]
    [Arguments("<FONT COLOR='#ff8000'>x</FONT>", @"{\c&H0080FF&}x{\c}")]
    [Arguments("<font color=#102030>x</font>", @"{\c&H302010&}x{\c}")]
    [Arguments("<font face=\"Arial\" color=\"#102030\">x</font>", @"{\c&H302010&}x{\c}")]
    [Arguments("<font face=\"Arial\">x</font>", "x")]
    [Arguments("<font color=\"red\">x</font>", "x")]
    [Arguments("</font>x", "x")]
    [Arguments("a\r\nb\nc", @"a\Nb\Nc")]
    [Arguments("a\rb", @"a\Nb")]
    [Arguments("\r\ntext\r\n\r\n", "text")]
    [Arguments("<ruby>x</ruby> <span style=\"a\">y</span> <br/>z", "x y z")]
    [Arguments("a < b and c > d", "a < b and c > d")]
    [Arguments("unclosed <i", "unclosed <i")]
    [Arguments(@"{\an8}top line", @"{\an8}top line")]
    public async Task ConvertSrtText_ConvertsTagsAndNewlines(string input, string expected)
    {
        await Assert.That(ResizedAssBuilder.ConvertSrtText(input)).IsEqualTo(expected);
    }

    [Test]
    public async Task Render_ConvertsSrtTracksToDialogueLinesWithTheDefaultStyle()
    {
        string document = Render(Track("S_TEXT/UTF8", null,
            Block(1000, 1500, "<i>Line, one</i>\nline two"),
            Block(3000, 500, "Second")), 100);

        await Assert.That(DialogueLines(document)).IsEquivalentTo(new[]
        {
            @"Dialogue: 0,0:00:01.00,0:00:02.50,Default,,0,0,0,,{\i1}Line, one{\i0}\Nline two",
            "Dialogue: 0,0:00:03.00,0:00:03.50,Default,,0,0,0,,Second",
        });
    }

    [Test]
    public async Task Render_SkipsEmptySrtBlocks()
    {
        string document = Render(Track("S_TEXT/UTF8", null,
            Block(0, 500, "\r\n"),
            Block(1000, 500, "kept")), 100);

        await Assert.That(DialogueLines(document).Length).IsEqualTo(1);
    }

    [Test]
    public async Task Render_WritesAnSrtHeaderWithOneBottomCentreDefaultStyle()
    {
        string[] lines = Lines(Render(Track("S_TEXT/UTF8", null, Block(0, 500, "text")), 100));

        await Assert.That(lines[0]).IsEqualTo("[Script Info]");
        await Assert.That(lines).Contains("PlayResX: 1920");
        await Assert.That(lines).Contains("PlayResY: 1080");
        await Assert.That(lines).Contains("ScaledBorderAndShadow: yes");
        await Assert.That(lines.Count(line => line.StartsWith("Style: ", StringComparison.Ordinal))).IsEqualTo(1);

        string[] format = lines.First(line => line.StartsWith("Format: Name", StringComparison.Ordinal))["Format: ".Length..].Split(", ");
        string[] style = lines.First(line => line.StartsWith("Style: ", StringComparison.Ordinal))["Style: ".Length..].Split(',');
        await Assert.That(style.Length).IsEqualTo(format.Length);
        await Assert.That(style[Array.IndexOf(format, "Name")]).IsEqualTo("Default");
        await Assert.That(style[Array.IndexOf(format, "Alignment")]).IsEqualTo("2");
        await Assert.That(style[Array.IndexOf(format, "PrimaryColour")]).IsEqualTo("&H00FFFFFF");
        await Assert.That(style[Array.IndexOf(format, "OutlineColour")]).IsEqualTo("&H00000000");
    }

    [Test]
    public async Task Render_ScalesTheSrtDefaultStyle()
    {
        MatroskaTrackData track = Track("S_TEXT/UTF8", null, Block(0, 500, "text"));
        double Column(string document, string column)
        {
            string[] lines = Lines(document);
            string[] format = lines.First(line => line.StartsWith("Format: Name", StringComparison.Ordinal))["Format: ".Length..].Split(", ");
            string[] style = lines.First(line => line.StartsWith("Style: ", StringComparison.Ordinal))["Style: ".Length..].Split(',');
            return double.Parse(style[Array.IndexOf(format, column)], CultureInfo.InvariantCulture);
        }

        string normal = Render(track, 100);
        string doubled = Render(track, 200);

        await Assert.That(Column(doubled, "Fontsize")).IsEqualTo(Column(normal, "Fontsize") * 2);
        await Assert.That(Column(doubled, "Outline")).IsEqualTo(Column(normal, "Outline") * 2);
        await Assert.That(Column(doubled, "Shadow")).IsEqualTo(Column(normal, "Shadow") * 2);
        await Assert.That(Column(normal, "Fontsize")).IsGreaterThan(0d);
    }
}
