using System;
using System.Collections.Generic;

namespace Screenbox.Core.Models;

/// <summary>
/// A subtitle track parsed into the parts needed to render it as an ASS file at any size.
/// Building one means parsing every event, so the resizer caches these. Rendering one at a new
/// size only rewrites the style lines, which is what makes a size change instant.
/// </summary>
public sealed class ResizableSubtitle
{
    internal ResizableSubtitle(IReadOnlyList<string> headerLines, IReadOnlySet<string> dialogueStyles, string eventLines)
    {
        HeaderLines = headerLines;
        DialogueStyles = dialogueStyles;
        EventLines = eventLines;
    }

    /// <summary>Gets the script header, with the [Events] section and its Format line last.</summary>
    internal IReadOnlyList<string> HeaderLines { get; }

    /// <summary>Gets the names of the styles that get resized. Other styles are typesetting.</summary>
    internal IReadOnlySet<string> DialogueStyles { get; }

    /// <summary>Gets the rebuilt Dialogue lines, ready to append after the header.</summary>
    internal string EventLines { get; }
}
