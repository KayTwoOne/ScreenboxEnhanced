using Screenbox.Core.Helpers;

namespace Screenbox.Core.Tests.Helpers;

public sealed class LruCacheTests
{
    [Test]
    public async Task TryGet_FindsWhatWasAdded()
    {
        LruCache<string, int> cache = new(2);
        cache.Add("a", 1);

        await Assert.That(cache.TryGet("a", out int value)).IsTrue();
        await Assert.That(value).IsEqualTo(1);
        await Assert.That(cache.TryGet("b", out _)).IsFalse();
    }

    [Test]
    public async Task Add_EvictsTheOldestEntryOverCapacity()
    {
        LruCache<string, int> cache = new(2);
        cache.Add("a", 1);
        cache.Add("b", 2);
        cache.Add("c", 3);

        await Assert.That(cache.Count).IsEqualTo(2);
        await Assert.That(cache.TryGet("a", out _)).IsFalse();
        await Assert.That(cache.TryGet("b", out _)).IsTrue();
        await Assert.That(cache.TryGet("c", out _)).IsTrue();
    }

    [Test]
    public async Task TryGet_CountsAsARecentUse()
    {
        LruCache<string, int> cache = new(2);
        cache.Add("a", 1);
        cache.Add("b", 2);

        cache.TryGet("a", out _);
        cache.Add("c", 3);

        await Assert.That(cache.TryGet("a", out _)).IsTrue();
        await Assert.That(cache.TryGet("b", out _)).IsFalse();
    }

    [Test]
    public async Task Add_ReplacesTheValueOfAnExistingKeyWithoutGrowing()
    {
        LruCache<string, int> cache = new(2);
        cache.Add("a", 1);
        cache.Add("b", 2);
        cache.Add("a", 10);
        cache.Add("c", 3);

        await Assert.That(cache.Count).IsEqualTo(2);
        await Assert.That(cache.TryGet("a", out int value)).IsTrue();
        await Assert.That(value).IsEqualTo(10);
        await Assert.That(cache.TryGet("b", out _)).IsFalse();
    }

    [Test]
    public async Task TryGet_CanCacheANullValue()
    {
        // The resizer remembers "this track cannot be read", so it does not read it again.
        LruCache<string, string?> cache = new(2);
        cache.Add("unreadable", null);

        await Assert.That(cache.TryGet("unreadable", out string? value)).IsTrue();
        await Assert.That(value).IsNull();
    }
}
