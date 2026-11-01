using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Screenbox.Core.Models;
using Screenbox.Core.Services;

namespace Screenbox.Core.Tests.Helpers;

/// <summary>
/// A scriptable <see cref="IMetadataProvider"/> stand-in. Records every call it receives so a test
/// can assert whether - and how many times - it was actually invoked, entirely without HTTP.
/// </summary>
public sealed class FakeMetadataProvider : IMetadataProvider
{
    private readonly IReadOnlyList<MetadataMatch> _results;

    public FakeMetadataProvider(string name, IReadOnlyList<MetadataMatch>? results = null)
    {
        Name = name;
        _results = results ?? Array.Empty<MetadataMatch>();
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <summary>Number of times <see cref="SearchAsync"/> was called.</summary>
    public int CallCount { get; private set; }

    /// <summary>The query most recently passed to <see cref="SearchAsync"/>.</summary>
    public string? LastQuery { get; private set; }

    /// <inheritdoc/>
    public Task<IReadOnlyList<MetadataMatch>> SearchAsync(string query, CancellationToken ct)
    {
        CallCount++;
        LastQuery = query;
        return Task.FromResult(_results);
    }
}
