using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;

namespace Screenbox.Core.Helpers;

/// <summary>
/// Finds the image a web page advertises for itself in its Open Graph (or Twitter card) metadata.
/// This is how an image host's share link, which points at a page, gives up the image behind it.
/// <para>
/// Deliberately not an HTML parser: it looks at <c>&lt;meta&gt;</c> tags only, which is all the
/// answer lives in, and tolerates what real pages do - either attribute order, single, double or
/// no quotes, upper-case markup, and HTML-encoded characters in the address.
/// </para>
/// </summary>
internal static partial class OpenGraphImage
{
    // The most specific, and so most trustworthy, address first: the secure variant is https by
    // definition, so a page that offers one next to a plain-http og:image should be read as having
    // offered the secure one.
    private static readonly string[] PreferredProperties = ["og:image:secure_url", "og:image", "twitter:image"];

    // A meta tag, up to the '>' that really closes it: a '>' inside a quoted value (a description
    // that mentions one, say) does not. Each alternative starts with a different character, so the
    // scan stays linear, and the match timeout is a backstop against hostile input.
    // [GeneratedRegex] rather than RegexOptions.Compiled: this project sets IsAotCompatible, and
    // Compiled silently falls back to the interpreter under NativeAOT.
    [GeneratedRegex("""<meta\b(?:[^>"']|"[^"]*"|'[^']*')*>""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MetaTagRegex();

    // One name=value attribute, the value double-quoted, single-quoted or bare.
    [GeneratedRegex("""([^\s"'<>/=]+)\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'<>`]+))""",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AttributeRegex();

    /// <summary>
    /// Returns the address of the page's advertised image exactly as the page wrote it (decoded, but
    /// not resolved against the page's own address), or null when it names none.
    /// </summary>
    /// <param name="html">The start of the document. Anything after the head is not needed.</param>
    public static string? Find(string html)
    {
        Dictionary<string, string> found = new(StringComparer.Ordinal);
        try
        {
            foreach (Match tag in MetaTagRegex().Matches(html))
            {
                string? property = null;
                string? content = null;
                foreach (Match attribute in AttributeRegex().Matches(tag.Value))
                {
                    string name = attribute.Groups[1].Value;
                    string value = attribute.Groups[2].Success ? attribute.Groups[2].Value
                        : attribute.Groups[3].Success ? attribute.Groups[3].Value
                        : attribute.Groups[4].Value;

                    // Open Graph says "property", Twitter's cards say "name"; pages mix them freely.
                    if (name.Equals("property", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("name", StringComparison.OrdinalIgnoreCase))
                    {
                        property ??= value.Trim().ToLowerInvariant();
                    }
                    else if (name.Equals("content", StringComparison.OrdinalIgnoreCase))
                    {
                        content ??= value;
                    }
                }

                if (property is null || content is null || Array.IndexOf(PreferredProperties, property) < 0) continue;

                // An empty value is the same as no tag, so a later tag for the same property still counts.
                string address = WebUtility.HtmlDecode(content).Trim();
                if (address.Length > 0) found.TryAdd(property, address);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Whatever was found before the scan gave up still stands.
        }

        foreach (string property in PreferredProperties)
        {
            if (found.TryGetValue(property, out string? address)) return address;
        }

        return null;
    }
}
