using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Screenbox.Core.Models;

namespace Screenbox.Core.Services;

/// <summary>
/// A source of online metadata (title, synopsis, artwork) for a media folder. Implementations
/// talk to a specific third-party catalog; callers go through <see cref="SearchAsync"/> only.
/// </summary>
public interface IMetadataProvider
{
    /// <summary>Display name of the provider, e.g. "AniList".</summary>
    string Name { get; }

    /// <summary>
    /// Searches the provider for candidates matching <paramref name="query"/>.
    /// </summary>
    /// <remarks>
    /// Implementations MUST return an empty list when nothing matches, and MUST NOT return
    /// <see langword="null"/> - callers iterate the result without a null check. They must also
    /// swallow their own failures: a transport error, a non-success status, a malformed body or a
    /// provider-level error response all yield an empty list rather than an exception, so one dead
    /// catalog cannot abort a scan across the others. Only cancellation propagates.
    /// <para>
    /// Implementations leave <see cref="MetadataMatch.Confidence"/> at its default of 0; scoring
    /// belongs to the coordinator, which ranks candidates across providers on one common measure.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<MetadataMatch>> SearchAsync(string query, CancellationToken ct);
}
