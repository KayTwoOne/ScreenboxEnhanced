using System;
using System.Collections.Generic;

namespace Screenbox.Core.Services;

/// <summary>
/// Lazily constructs the set of <see cref="IMetadataProvider"/> implementations the user has
/// enabled. Construction is deferred to <see cref="GetEnabledProviders"/> so that nothing
/// network-capable - not even the shared <see cref="System.Net.Http.HttpClient"/> a provider's
/// constructor requires - comes into existence until a caller has already confirmed the user
/// opted in to online metadata scraping.
/// <para>
/// Registering a provider, or an <see cref="System.Net.Http.HttpClient"/>, directly in dependency
/// injection would construct them at app startup and break that guarantee: only this factory and
/// the coordinator that calls it belong in the container.
/// </para>
/// </summary>
public interface IMetadataProviderFactory : IDisposable
{
    /// <summary>
    /// Returns the providers the user has enabled, in a fixed priority order (AniList, then Jikan,
    /// then TMDB), or an empty list when none are enabled. The first call that needs to return a
    /// non-empty list is what creates the single <see cref="System.Net.Http.HttpClient"/> shared by
    /// every provider this factory hands out; calling this when nothing is enabled creates no
    /// client at all.
    /// </summary>
    IReadOnlyList<IMetadataProvider> GetEnabledProviders();
}
