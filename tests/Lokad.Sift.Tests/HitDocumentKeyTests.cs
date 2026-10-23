namespace Lokad.Sift.Tests;

public sealed class HitDocumentKeyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeysResolveDuringSearchAndSurviveGenerationReplacement(bool persistent)
    {
        const string path = "src/café-😀.txt";
        using var fixture = new CorpusFixture((path, "before😀\nneedle café after"));
        using var index = persistent ? new SiftIndex(fixture.CorpusPath) : SiftIndex.CreateInMemory();
        await index.Build(fixture.CreateSource());
        using var snapshot = index.OpenSnapshot();
        var collector = new KeyCollector(snapshot);
        snapshot.Search(new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive), new(), collector);
        var key = Assert.Single(collector.Keys);
        var hit = Assert.Single(collector.Hits);
        Assert.Equal(path, key.Path);
        Assert.Equal(snapshot.Materialize(hit, 0, 0).Path, key.Path);
        Assert.True(snapshot.TryGetDocumentBytes(key, out var content));
        Assert.Equal("before😀\nneedle café after"u8.ToArray(), content.ToArray());
        Assert.Equal(content.ToArray(), snapshot.GetDocumentBytes(hit).ToArray());
        Assert.Equal(content.ToArray(), Assert.Single(collector.Contents).ToArray());

        await index.Build(new InMemoryDocumentSource([("src/replacement.txt", "new content")]));
        Assert.Equal(key, snapshot.GetDocumentKey(hit));
        Assert.Equal(content.ToArray(), snapshot.GetDocumentBytes(hit).ToArray());
        using var replacement = index.OpenSnapshot();
        Assert.Throws<ArgumentException>(() => replacement.GetDocumentKey(hit));
        Assert.Throws<ArgumentException>(() => snapshot.GetDocumentKey(default));
        Assert.Throws<ArgumentException>(() => replacement.GetDocumentBytes(hit));
        Assert.Throws<ArgumentException>(() => snapshot.GetDocumentBytes(default));

        snapshot.Dispose();
        Assert.Throws<ObjectDisposedException>(() => snapshot.GetDocumentKey(hit));
        Assert.Throws<ObjectDisposedException>(() => snapshot.GetDocumentBytes(hit));
        Assert.Equal(path, key.Path);
    }

    [Fact]
    public async Task OverlayKeysFollowVisibilityAndRejectForeignAndExpiredHits()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([
            ("src/base.txt", "needle"), ("src/replaced.txt", "needle"), ("src/deleted.txt", "needle")]));
        using var overlay = index.OpenOverlaySnapshot();
        await overlay.Update(new InMemoryDocumentSource([
            ("src/replaced.txt", "new needle"), ("src/added-😀.txt", "needle")]),
            [new DocumentKey("src/deleted.txt")]);
        var collector = new KeyCollector(overlay);
        overlay.Search(new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive), new(), collector);
        Assert.Equal(new[] { "src/added-😀.txt", "src/base.txt", "src/replaced.txt" },
            collector.Keys.Select(key => key.Path).Order(StringComparer.Ordinal));
        Assert.Equal(collector.Hits.Select(hit => overlay.Materialize(hit, 0, 0).Path),
            collector.Keys.Select(key => key.Path));

        using var other = index.OpenOverlaySnapshot();
        using var plain = index.OpenSnapshot();
        foreach (var hit in collector.Hits)
        {
            Assert.Throws<ArgumentException>(() => other.GetDocumentKey(hit));
            Assert.Throws<ArgumentException>(() => plain.GetDocumentKey(hit));
            Assert.Throws<ArgumentException>(() => other.GetDocumentBytes(hit));
            Assert.Throws<ArgumentException>(() => plain.GetDocumentBytes(hit));
        }
        await overlay.Update(new InMemoryDocumentSource([
            ("src/later.txt", "needle"), ("src/base.txt", "shadow needle")]), []);
        foreach (var hit in collector.Hits)
        {
            if (collector.Keys[collector.Hits.IndexOf(hit)].Path == "src/base.txt")
            {
                Assert.Equal(overlay.Materialize(hit, 0, 0).Path, overlay.GetDocumentKey(hit).Path);
                Assert.Equal("needle"u8.ToArray(), overlay.GetDocumentBytes(hit).ToArray());
                Assert.True(overlay.TryGetDocumentBytes(new DocumentKey("src/base.txt"), out var currentContent));
                Assert.Equal("shadow needle"u8.ToArray(), currentContent.ToArray());
            }
            else
            {
                Assert.Throws<ArgumentException>(() => overlay.Materialize(hit, 0, 0));
                Assert.Throws<ArgumentException>(() => overlay.GetDocumentKey(hit));
                Assert.Throws<ArgumentException>(() => overlay.GetDocumentBytes(hit));
            }
        }
        overlay.Dispose();
        Assert.All(collector.Keys, key => Assert.StartsWith("src/", key.Path));
        Assert.Throws<ObjectDisposedException>(() => overlay.GetDocumentKey(collector.Hits[0]));
        Assert.Throws<ObjectDisposedException>(() => overlay.GetDocumentBytes(collector.Hits[0]));
    }

    [Fact]
    public async Task EmptyDocumentBytesResolveFromAnOwnedZeroLengthHit()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("src/empty.txt", "")]));
        using var snapshot = index.OpenSnapshot();
        var collector = new KeyCollector(snapshot);
        snapshot.Search(new SearchQuery("(?:)", PatternMode.Regex, CaseMode.Sensitive), new(), collector);
        Assert.Empty(Assert.Single(collector.Contents).ToArray());
        Assert.Equal("src/empty.txt", Assert.Single(collector.Keys).Path);
    }

    [Fact]
    public async Task ExistingSnapshotImplementationsUseTheMaterializationFallback()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("src/custom.txt", "needle")]));
        using var snapshot = index.OpenSnapshot();
        IIndexSnapshot custom = new ExistingSnapshot(snapshot);
        var collector = new KeyCollector(custom);
        custom.Search(new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive), new(), collector, default);
        Assert.Equal("src/custom.txt", Assert.Single(collector.Keys).Path);
        Assert.Equal("needle"u8.ToArray(), Assert.Single(collector.Contents).ToArray());
    }

    private sealed class KeyCollector(IIndexSnapshot snapshot) : IHitCollector
    {
        public List<DocumentKey> Keys { get; } = [];
        public List<RawHit> Hits { get; } = [];
        public List<ReadOnlyMemory<byte>> Contents { get; } = [];

        public bool OnHit(in RawHit hit)
        {
            Keys.Add(snapshot.GetDocumentKey(hit));
            Contents.Add(snapshot.GetDocumentBytes(hit));
            Hits.Add(hit);
            return true;
        }
    }

    // Deliberately implements only the pre-existing snapshot contract.
    private sealed class ExistingSnapshot(IIndexSnapshot inner) : IIndexSnapshot
    {
        public IndexIdentity Identity => inner.Identity;
        public ReadOnlyMemory<byte> CommitMetadata => inner.CommitMetadata;
        public DocumentVisitStats VisitDocuments(IDocumentKeyCollector collector, CancellationToken cancel) => inner.VisitDocuments(collector, cancel);
        public SearchPlan Explain(SearchQuery query, PathFilter filter) => inner.Explain(query, filter);
        public SearchStats Search(SearchQuery query, PathFilter filter, IHitCollector collector, CancellationToken cancel) => inner.Search(query, filter, collector, cancel);
        public MaterializedHit Materialize(in RawHit hit, int before, int after) => inner.Materialize(hit, before, after);
        public MaterializedHitWithLine MaterializeWithMatchingLine(in RawHit hit, int before, int after) => inner.MaterializeWithMatchingLine(hit, before, after);
        public bool TryGetDocumentBytes(DocumentKey key, out ReadOnlyMemory<byte> content) => inner.TryGetDocumentBytes(key, out content);
        public void Dispose() => inner.Dispose();
    }
}
