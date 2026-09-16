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
    /// Searches the provider for candidates matching <paramref name="query"/>. Returns an
    /// empty list when nothing matches - never throws for a plain no-match result.
    /// </summary>
    Task<IReadOnlyList<MetadataMatch>> SearchAsync(string query, CancellationToken ct);
}
