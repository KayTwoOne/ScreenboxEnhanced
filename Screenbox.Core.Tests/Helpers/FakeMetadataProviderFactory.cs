using System.Collections.Generic;
using Screenbox.Core.Services;

namespace Screenbox.Core.Tests.Helpers;

/// <summary>
/// A scriptable <see cref="IMetadataProviderFactory"/> stand-in that hands back a fixed list of
/// providers and counts how many times it was asked for them. In the real factory,
/// <see cref="GetEnabledProviders"/> is the one method that constructs providers (and, lazily, the
/// shared <see cref="System.Net.Http.HttpClient"/> behind them) - so <see cref="ConstructionCount"/>
/// here stands in for "a provider was constructed" without this test needing a real HttpClient at
/// all. The coordinator must only call this after confirming the master toggle is on.
/// </summary>
public sealed partial class FakeMetadataProviderFactory : IMetadataProviderFactory
{
    private readonly IReadOnlyList<IMetadataProvider> _providers;

    public FakeMetadataProviderFactory(params IMetadataProvider[] providers)
    {
        _providers = providers;
    }

    /// <summary>Number of times <see cref="GetEnabledProviders"/> was called.</summary>
    public int ConstructionCount { get; private set; }

    /// <inheritdoc/>
    public IReadOnlyList<IMetadataProvider> GetEnabledProviders()
    {
        ConstructionCount++;
        return _providers;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}
