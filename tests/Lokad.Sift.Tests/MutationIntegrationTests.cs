using System.Text;
using Lokad.Sift;
using Lokad.Sift.Search;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Tests;

public sealed class MutationIntegrationTests
{
    [Fact]
    public async Task BuildPullsDocumentsFromAsynchronousSource()
    {
        static async IAsyncEnumerable<SubmittedDocument> ReadDocuments()
        {
            await Task.Yield();
            yield return new SubmittedDocument("src/a.txt", "alpha"u8.ToArray());
            await Task.Yield();
            yield return new SubmittedDocument("src/b.txt", "beta"u8.ToArray());
        }

        using var index = SiftIndex.CreateInMemory();

        var stats = await index.Build(ReadDocuments());

        Assert.Equal(2, stats.IndexedDocuments);
        using var snapshot = index.OpenSnapshot();
        Assert.True(snapshot.TryGetDocumentBytes(new DocumentKey("src/a.txt"), out _));
        Assert.True(snapshot.TryGetDocumentBytes(new DocumentKey("src/b.txt"), out _));
    }

    [Fact]
    public async Task UpdatePullsDocumentsFromAsynchronousSource()
    {
        static async IAsyncEnumerable<SubmittedDocument> ReadDocuments()
        {
            await Task.Yield();
            yield return new SubmittedDocument("src/a.txt", "omega"u8.ToArray());
            await Task.Yield();
            yield return new SubmittedDocument("src/b.txt", "beta"u8.ToArray());
        }

        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha")]));

        var stats = await index.Update(ReadDocuments(), []);

        Assert.Equal(2, stats.UpsertedDocuments);
        using var snapshot = index.OpenSnapshot();
        Assert.True(snapshot.TryGetDocumentBytes(new DocumentKey("src/a.txt"), out var replaced));
        Assert.Equal("omega"u8.ToArray(), replaced.ToArray());
        Assert.True(snapshot.TryGetDocumentBytes(new DocumentKey("src/b.txt"), out _));
    }

    [Fact]
    public async Task SnapshotVisitsOnlyLiveDocumentKeysAndHonorsCollectorStop()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([
            ("src/a.txt", "alpha"),
            ("src/b.txt", "beta")
        ]));
        await index.Update(
            new InMemoryDocumentSource([
                ("src/a.txt", "omega"),
                ("src/c.txt", "gamma")
            ]),
            [new DocumentKey("src/b.txt")]);

        using var snapshot = index.OpenSnapshot();
        var all = new DocumentKeyBuffer();
        var completed = snapshot.VisitDocuments(all);
        Assert.True(completed.Completed);
        Assert.Equal(2, completed.VisitedDocuments);
        Assert.Equal(
            ["src/a.txt", "src/c.txt"],
            all.Keys.Select(static key => key.Path).Order(StringComparer.Ordinal));

        var first = new DocumentKeyBuffer(maximumDocuments: 1);
        var stopped = snapshot.VisitDocuments(first);
        Assert.False(stopped.Completed);
        Assert.Equal(1, stopped.VisitedDocuments);
        Assert.Single(first.Keys);
    }

    [Fact]
    public async Task OverlayVisitsItsMergedLiveDocumentKeys()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([
            ("src/a.txt", "alpha"),
            ("src/b.txt", "beta")
        ]));
        using var overlay = index.OpenOverlaySnapshot();
        await overlay.Update(
            new InMemoryDocumentSource([
                ("src/a.txt", "omega"),
                ("src/c.txt", "gamma")
            ]),
            [new DocumentKey("src/b.txt")]);

        var collector = new DocumentKeyBuffer();
        var stats = overlay.VisitDocuments(collector);

        Assert.True(stats.Completed);
        Assert.Equal(2, stats.VisitedDocuments);
        Assert.Equal(
            ["src/a.txt", "src/c.txt"],
            collector.Keys.Select(static key => key.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ExpectedRevisionRejectsAStaleUpdateBeforeConsumingItsSource()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha")]));
        var staleRevision = index.GetIndexIdentity().Revision;
        var committed = await index.Update(new InMemoryDocumentSource([("src/b.txt", "beta")]), []);
        Assert.Equal(index.GetIndexIdentity(), committed.CommittedIdentity);
        Assert.NotEqual(staleRevision, committed.CommittedIdentity.Revision);
        var source = new SubmittedDocumentSource([
            new SubmittedDocument("src/c.txt", "gamma"u8.ToArray())
        ]);

        var exception = await Assert.ThrowsAsync<StaleIndexRevisionException>(() => index.Update(
            source,
            [],
            new UpdateOptions
            {
                ExpectedRevision = staleRevision
            }));

        Assert.Equal(staleRevision, exception.ExpectedRevision);
        Assert.Equal(index.GetIndexIdentity().Revision, exception.ActualRevision);
        Assert.Equal(0, source.ConsumedDocuments);
        using var snapshot = index.OpenSnapshot();
        Assert.False(snapshot.TryGetDocumentBytes(new DocumentKey("src/c.txt"), out _));
    }

    [Fact]
    public async Task ExpectedRevisionRemainsValidAcrossCompaction()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha")]));
        await index.Update(new InMemoryDocumentSource([("src/b.txt", "beta")]), []);
        var expectedRevision = index.GetIndexIdentity().Revision;

        var compaction = await index.Compact(new CompactOptions { MinimumSegmentCountForSmallMerge = 1 });
        var compactedIdentity = index.GetIndexIdentity();
        Assert.True(compaction.InputSegments > 0);
        Assert.Equal(expectedRevision, compactedIdentity.Revision);

        var update = await index.Update(
            new InMemoryDocumentSource([]),
            [],
            new UpdateOptions { ExpectedRevision = expectedRevision });

        Assert.Equal(compactedIdentity, update.CommittedIdentity);
    }

    [Fact]
    public async Task ExpectedIdentityRejectsCompactionBeforeConsumingItsSource()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha")]));
        await index.Update(new InMemoryDocumentSource([("src/b.txt", "beta")]), []);
        var expectedIdentity = index.GetIndexIdentity();

        var compaction = await index.Compact(new CompactOptions { MinimumSegmentCountForSmallMerge = 1 });
        var compactedIdentity = index.GetIndexIdentity();
        Assert.True(compaction.InputSegments > 0);
        Assert.Equal(expectedIdentity.Revision, compactedIdentity.Revision);
        Assert.NotEqual(expectedIdentity, compactedIdentity);
        var source = new SubmittedDocumentSource([
            new SubmittedDocument("src/c.txt", "gamma"u8.ToArray())
        ]);

        var exception = await Assert.ThrowsAsync<StaleIndexIdentityException>(() => index.Update(
            source,
            [],
            new UpdateOptions { ExpectedIdentity = expectedIdentity }));

        Assert.Equal(expectedIdentity, exception.ExpectedIdentity);
        Assert.Equal(compactedIdentity, exception.ActualIdentity);
        Assert.Equal(0, source.ConsumedDocuments);
    }

    [Fact]
    public async Task UpdateAppliesUpsertsAndDeletions()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha\n"),
            ("src/b.txt", "beta\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        var updateSource = new InMemoryDocumentSource([
            ("src/a.txt", "omega\n"),
            ("src/c.txt", "beta\n")
        ]);

        var stats = await index.Update(updateSource, [new DocumentKey("src/b.txt")]);
        Assert.Equal(2, stats.SubmittedUpserts);
        Assert.Equal(2, stats.UpsertedDocuments);
        Assert.Equal(1, stats.DeletedDocuments);
        Assert.Equal("omega\nbeta\n".Length, stats.ContentBytes);
        Assert.Equal(stats.SubmittedBytes, stats.ContentBytes);

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        snapshot.Search(new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), collector);
        var paths = collector.Hits.Select(hit => snapshot.Materialize(hit, 0, 0).Path).ToArray();
        Assert.Equal(["src/c.txt"], paths);
    }

    [Fact]
    public async Task InMemoryModeSupportsBuildSearchUpdateAndDelete()
    {
        using var index = SiftIndex.CreateInMemory();
        var build = await index.Build(new InMemoryDocumentSource([
            ("src/a.txt", "alpha\n"),
            ("src/b.txt", "beta\n")
        ]));

        Assert.Equal(2, build.IndexedDocuments);

        using (var snapshot = index.OpenSnapshot())
        {
            var collector = new HitBuffer();
            snapshot.Search(new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), collector);
            Assert.Single(collector.Hits);
        }

        var update = await index.Update(
            new InMemoryDocumentSource([
                ("src/a.txt", "omega\n"),
                ("src/c.txt", "beta\n")
            ]),
            [new DocumentKey("src/b.txt")]);

        Assert.Equal(2, update.UpsertedDocuments);
        Assert.Equal(1, update.DeletedDocuments);

        using var updatedSnapshot = index.OpenSnapshot();
        var omegaCollector = new HitBuffer();
        updatedSnapshot.Search(new SearchQuery("omega", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), omegaCollector);
        Assert.Single(omegaCollector.Hits);

        var betaCollector = new HitBuffer();
        updatedSnapshot.Search(new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), betaCollector);
        var paths = betaCollector.Hits.Select(hit => updatedSnapshot.Materialize(hit, 0, 0).Path).OrderBy(static path => path).ToArray();
        Assert.Equal(new[] { "src/c.txt" }, paths);
    }

    [Fact]
    public async Task OverlaySnapshotShadowsBaseWithoutMutatingIt()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha\n"),
            ("src/b.txt", "beta\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var overlay = index.OpenOverlaySnapshot();
        var apply = await overlay.Update(
            new InMemoryDocumentSource([
                ("src/a.txt", "omega\n"),
                ("src/c.txt", "gamma\n")
            ]),
            [new DocumentKey("src/b.txt")]);

        Assert.Equal(2, apply.UpsertedDocuments);
        Assert.Equal(1, apply.DeletedDocuments);

        var overlayCollector = new HitBuffer();
        var overlayStats = overlay.Search(new SearchQuery("omega|gamma|beta", PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), overlayCollector);
        Assert.Equal(2, overlayStats.HitsReturned);
        var overlayPaths = overlayCollector.Hits.Select(hit => overlay.Materialize(hit, 0, 0).Path).OrderBy(static path => path, StringComparer.Ordinal).ToArray();
        Assert.Equal(["src/a.txt", "src/c.txt"], overlayPaths);

        using var baseSnapshot = index.OpenSnapshot();
        var baseCollector = new HitBuffer();
        var baseStats = baseSnapshot.Search(new SearchQuery("alpha|beta", PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), baseCollector);
        Assert.Equal(2, baseStats.HitsReturned);
        var basePaths = baseCollector.Hits.Select(hit => baseSnapshot.Materialize(hit, 0, 0).Path).OrderBy(static path => path, StringComparer.Ordinal).ToArray();
        Assert.Equal(["src/a.txt", "src/b.txt"], basePaths);
    }

    [Fact]
    public async Task OverlayUpdateExpiresHitsFromThePreviousGeneration()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([]));
        using var overlay = index.OpenOverlaySnapshot();
        await overlay.Update(
            new InMemoryDocumentSource([("src/a.txt", "old value")]),
            []);
        var oldHits = new HitBuffer();
        overlay.Search(
            new SearchQuery("old", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter(),
            oldHits);

        await overlay.Update(
            new InMemoryDocumentSource([("src/a.txt", "new value")]),
            []);
        var newHits = new HitBuffer();
        overlay.Search(
            new SearchQuery("new", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter(),
            newHits);

        Assert.Throws<ArgumentException>(() => overlay.Materialize(oldHits.Hits.Single(), 0, 0));
        Assert.Equal("new", overlay.Materialize(newHits.Hits.Single(), 0, 0).MatchText);
    }

    [Fact]
    public async Task OverlayDocumentMemoryRemainsValidAcrossUpdates()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([]));
        using var overlay = index.OpenOverlaySnapshot();
        await overlay.Update(
            new InMemoryDocumentSource([("src/a.txt", "old value")]),
            []);
        Assert.True(overlay.TryGetDocumentBytes(new DocumentKey("src/a.txt"), out var oldContent));

        await overlay.Update(
            new InMemoryDocumentSource([("src/a.txt", "new value")]),
            []);

        Assert.Equal("old value"u8.ToArray(), oldContent.ToArray());
    }

    [Fact]
    public async Task OverlayDocumentRetrievalDoesNotCopyTheDocumentBody()
    {
        var body = new string('a', 128 * 1024);
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([]));
        using var overlay = index.OpenOverlaySnapshot();
        await overlay.Update(new InMemoryDocumentSource([("src/a.txt", body)]), []);
        var key = new DocumentKey("src/a.txt");
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        for (var iteration = 0; iteration < 5; iteration++)
        {
            Assert.True(overlay.TryGetDocumentBytes(key, out var content));
            Assert.Equal(body.Length, content.Length);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.True(allocated < body.Length, $"Document retrieval allocated {allocated} bytes.");
    }

    [Fact]
    public async Task OverlayFaultsWhenCommittedStateCannotBeOpened()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([]));
        using var overlay = Assert.IsType<OverlaySnapshot>(index.OpenOverlaySnapshot());
        var injectedFailure = new IOException("Injected snapshot-open failure.");
        overlay.BeforeOpeningCommittedSnapshot = () => throw injectedFailure;

        var failure = await Assert.ThrowsAsync<IOException>(() => overlay.Update(
            new InMemoryDocumentSource([("src/a.txt", "alpha")]),
            []));

        Assert.Same(injectedFailure, failure);
        var unavailable = Assert.Throws<InvalidOperationException>(() =>
            overlay.TryGetDocumentBytes(new DocumentKey("src/a.txt"), out _));
        Assert.Same(injectedFailure, unavailable.InnerException);
        var updateUnavailable = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            overlay.Update(new InMemoryDocumentSource([]), []));
        Assert.Same(injectedFailure, updateUnavailable.InnerException);
    }

    [Fact]
    public async Task OverlayUpdatesBoundHitOriginTrackingToTheCurrentGeneration()
    {
        const int revisions = 8;
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            OverlayCompactionSegmentThreshold = 1
        });
        await index.Build(new InMemoryDocumentSource([]));
        using var overlay = Assert.IsType<OverlaySnapshot>(index.OpenOverlaySnapshot());
        RawHit firstHit = default;

        for (var revision = 0; revision < revisions; revision++)
        {
            await overlay.Update(
                new InMemoryDocumentSource([("src/a.txt", $"revision {revision}")]),
                []);
            var hits = new HitBuffer();
            overlay.Search(
                new SearchQuery("revision", PatternMode.Literal, CaseMode.Sensitive),
                new PathFilter(),
                hits);
            Assert.InRange(overlay.RetainedOverlayHitOriginCount, 1, 2);
            if (revision == 0)
            {
                firstHit = hits.Hits.Single();
            }
        }

        Assert.Throws<ArgumentException>(() => overlay.Materialize(firstHit, 0, 0));
        Assert.InRange(overlay.GetOverlayMaintenanceStats().SegmentCount, 1, 2);
    }

    [Fact]
    public async Task OverlayHitsCannotBeMaterializedByAnotherOverlay()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([]));
        using var first = index.OpenOverlaySnapshot();
        using var second = index.OpenOverlaySnapshot();
        await first.Update(new InMemoryDocumentSource([("src/a.txt", "needle")]), []);
        await second.Update(new InMemoryDocumentSource([("src/a.txt", "needle")]), []);
        var firstHits = new HitBuffer();
        var secondHits = new HitBuffer();
        first.Search(
            new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter(),
            firstHits);
        second.Search(
            new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter(),
            secondHits);

        Assert.Throws<ArgumentException>(() => second.Materialize(firstHits.Hits.Single(), 0, 0));
    }

    [Fact]
    public async Task RawHitsCannotBeMaterializedByAnotherSnapshot()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "needle")]));
        using var first = index.OpenSnapshot();
        using var second = index.OpenSnapshot();
        var hits = new HitBuffer();
        first.Search(
            new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter(),
            hits);
        var secondHits = new HitBuffer();
        second.Search(
            new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter(),
            secondHits);

        var firstHit = hits.Hits.Single();
        var secondHit = secondHits.Hits.Single();
        Assert.Equal(firstHit, secondHit);
        Assert.True(firstHit == secondHit);
        Assert.Throws<ArgumentException>(() => second.Materialize(firstHit, 0, 0));
    }

    [Fact]
    public async Task OverlayDeletionStatsCountOnlyLiveDocuments()
    {
        using var index = SiftIndex.CreateInMemory();
        var largeBody = new string('a', 4 * 1024 * 1024);
        await index.Build(new InMemoryDocumentSource([("src/base.txt", largeBody)]));
        var baseKey = new DocumentKey("src/base.txt");
        using var baseSnapshot = Assert.IsType<SearchSnapshot>(index.OpenSnapshot());
        Assert.True(baseSnapshot.ContainsDocument(baseKey));

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var containsBaseDocument = baseSnapshot.ContainsDocument(baseKey);
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.True(containsBaseDocument);
        Assert.InRange(allocatedBytes, 0, 64 * 1024);

        using var overlay = index.OpenOverlaySnapshot();

        var deleteBase = await overlay.Update(
            new InMemoryDocumentSource([]),
            [new DocumentKey("src/base.txt"), new DocumentKey("src/missing.txt")]);
        Assert.Equal(1, deleteBase.DeletedDocuments);

        await overlay.Update(
            new InMemoryDocumentSource([("src/overlay.txt", "beta")]),
            []);
        var deleteAgain = await overlay.Update(
            new InMemoryDocumentSource([]),
            [
                new DocumentKey("src/base.txt"),
                new DocumentKey("src/missing.txt"),
                new DocumentKey("src/overlay.txt")
            ]);

        Assert.Equal(1, deleteAgain.DeletedDocuments);
    }

    [Fact]
    public async Task OverlayUpdateDoesNotCommitBeforeBaseVisibilityIsKnown()
    {
        using var fixture = new CorpusFixture(("src/base.txt", "base"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        using var overlay = index.OpenOverlaySnapshot();
        var segmentPath = Directory.EnumerateFiles(
            Path.Combine(fixture.CorpusPath, "segments"),
            "segment.pack",
            SearchOption.AllDirectories).Single();
        var segmentBytes = File.ReadAllBytes(segmentPath);
        File.Delete(segmentPath);

        await Assert.ThrowsAnyAsync<IOException>(async () =>
            await overlay.Update(
                new InMemoryDocumentSource([("src/pending.txt", "pending")]),
                [new DocumentKey("src/base.txt")]));

        File.WriteAllBytes(segmentPath, segmentBytes);
        await overlay.Update(
            new InMemoryDocumentSource([]),
            [new DocumentKey("src/missing.txt")]);

        Assert.False(overlay.TryGetDocumentBytes(new DocumentKey("src/pending.txt"), out _));
    }

    [Fact]
    public async Task OverlayUpdatesReportLogicalRevisions()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([]));
        using var overlay = index.OpenOverlaySnapshot();

        var added = await overlay.Update(
            new InMemoryDocumentSource([("src/a.txt", "alpha")]),
            []);
        var unchanged = await overlay.Update(
            new InMemoryDocumentSource([]),
            [new DocumentKey("src/missing.txt")]);
        var deleted = await overlay.Update(
            new InMemoryDocumentSource([]),
            [new DocumentKey("src/a.txt")]);

        Assert.Equal(new OverlayRevision(1), added.CommittedRevision);
        Assert.Equal(added.CommittedRevision, unchanged.CommittedRevision);
        Assert.Equal(new OverlayRevision(2), deleted.CommittedRevision);
    }

    [Fact]
    public async Task OverlaySnapshotMaterializesOverlayHitsAndCanCommitToBase()
    {
        using var fixture = new CorpusFixture(
            ("src/lib/math.cs", "static class MathEx { int Add(int a, int b) => a + b; }\n"),
            ("src/app/program.cs", "Console.WriteLine(\"hello\");\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var overlay = index.OpenOverlaySnapshot();
        await overlay.Update(
            new InMemoryDocumentSource([
                ("src/lib/math.cs", "static class MathEx { int Add(int a, int b) => checked(a + b); int Mul(int a, int b) => a * b; }\n")
            ]),
            []);

        var overlayCollector = new HitBuffer();
        overlay.Search(new SearchQuery("Mul", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), overlayCollector);
        var overlayMaterialized = overlay.MaterializeWithMatchingLine(overlayCollector.Hits[0], 0, 0);
        var overlayHit = overlayMaterialized.Hit;
        Assert.Equal("src/lib/math.cs", overlayHit.Path);
        Assert.Contains("Mul", overlayHit.MatchText, StringComparison.Ordinal);
        Assert.Contains("Mul", overlayMaterialized.MatchingLine, StringComparison.Ordinal);

        var commit = await overlay.CommitToBase(index);
        Assert.Equal(1, commit.UpsertedDocuments);

        using var snapshot = index.OpenSnapshot();
        var foldedCollector = new HitBuffer();
        var foldedStats = snapshot.Search(new SearchQuery("Mul", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), foldedCollector);
        Assert.Equal(1, foldedStats.HitsReturned);
        Assert.Equal("src/lib/math.cs", snapshot.Materialize(foldedCollector.Hits[0], 0, 0).Path);
    }

    [Fact]
    public async Task OverlayCompactsExcessSegmentsAndDropsIrrelevantTombstones()
    {
        const int compactionThreshold = 3;
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            OverlayCompactionSegmentThreshold = compactionThreshold
        });
        await index.Build(new InMemoryDocumentSource([("src/base.txt", "base")]));
        using var overlay = Assert.IsType<OverlaySnapshot>(index.OpenOverlaySnapshot());

        for (var revision = 0; revision < 12; revision++)
        {
            await overlay.Update(
                new InMemoryDocumentSource([("src/overlay.txt", $"revision {revision}")]),
                [new DocumentKey($"src/missing-{revision}.txt")]);
        }

        var maintenance = overlay.GetOverlayMaintenanceStats();
        Assert.InRange(maintenance.SegmentCount, 1, compactionThreshold);
        Assert.InRange(maintenance.DeadDocuments, 0, compactionThreshold - 1);
        Assert.Equal(0, overlay.GetRetainedTombstoneCount());
    }

    [Fact]
    public async Task OverlayCommitHonorsTransactionalUpdateOptions()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha")]));
        using var overlay = index.OpenOverlaySnapshot();
        await overlay.Update(new InMemoryDocumentSource([("src/a.txt", "omega")]), []);
        var baseIdentity = index.GetIndexIdentity();

        var update = await overlay.CommitToBase(index, new UpdateOptions
        {
            ExpectedRevision = baseIdentity.Revision,
            CommitMetadataProvider = static _ => "overlay-commit"u8.ToArray()
        });

        Assert.NotEqual(baseIdentity, update.CommittedIdentity);
        Assert.Equal("overlay-commit"u8.ToArray(), index.GetIndexState().CommitMetadata.ToArray());
    }

    [Fact]
    public async Task OverlayCommitRejectsAChangedBaseByDefault()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha")]));
        using var overlay = index.OpenOverlaySnapshot();
        var capturedIdentity = overlay.Identity;
        await overlay.Update(new InMemoryDocumentSource([("src/a.txt", "omega")]), []);
        await index.Update(new InMemoryDocumentSource([("src/b.txt", "beta")]), []);

        Assert.Equal(capturedIdentity, overlay.Identity);
        Assert.NotEqual(index.GetIndexIdentity(), overlay.Identity);
        await Assert.ThrowsAsync<StaleIndexRevisionException>(() => overlay.CommitToBase(index));

        using var snapshot = index.OpenSnapshot();
        Assert.True(snapshot.TryGetDocumentBytes(new DocumentKey("src/a.txt"), out var content));
        Assert.Equal("alpha"u8.ToArray(), content.ToArray());
    }

    [Fact]
    public async Task UpdateRejectsConflictingBatch()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await index.Update(
            new InMemoryDocumentSource([("src/a.txt", "beta\n")]),
            [new DocumentKey("src/a.txt")]));
    }

    [Fact]
    public async Task UpdateRejectsDuplicateUpsertPathsAfterCanonicalization()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await index.Update(
            new InMemoryDocumentSource([
                ("src/a.txt", "beta\n"),
                ("src\\a.txt", "gamma\n")
            ]),
            []));
    }

    [Fact]
    public async Task UpdateRejectsDuplicateDeletionPathsAfterCanonicalization()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await index.Update(
            new InMemoryDocumentSource([]),
            [new DocumentKey("src/a.txt"), new DocumentKey("src\\a.txt")]));
    }

    [Fact]
    public async Task RejectedUpsertPreservesPreviouslyIndexedContent()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            MaxDocumentBytes = 8,
            RejectedUpsertPolicy = RejectedUpsertPolicy.PreserveExisting
        });
        await index.Build(fixture.CreateSource());
        var before = index.Storage.ReadManifest();

        var stats = await index.Update(
            new InMemoryDocumentSource([("src/a.txt", "replacement is too large\n")]),
            []);
        var after = index.Storage.ReadManifest();

        Assert.Equal(1, stats.SubmittedUpserts);
        Assert.Equal(0, stats.UpsertedDocuments);
        Assert.Equal(1, stats.RejectedUpserts);
        var rejected = Assert.Single(stats.RejectedDocumentDetails);
        Assert.Equal("src/a.txt", rejected.Path);
        Assert.Equal(DocumentRejectionReason.MaximumDocumentBytesExceeded, rejected.Reason);
        Assert.Equal(before.Generation, after.Generation);

        using var snapshot = index.OpenSnapshot();
        var oldContent = new HitBuffer();
        snapshot.Search(
            new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter(),
            oldContent);
        Assert.Single(oldContent.Hits);

        var replacement = new HitBuffer();
        snapshot.Search(
            new SearchQuery("replacement", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter(),
            replacement);
        Assert.Empty(replacement.Hits);
    }

    [Fact]
    public async Task DeleteExistingPolicyCommitsHealthyUpsertsAndRemovesRejectedReplacements()
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions { MaxDocumentBytes = 8 });
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha")]));
        var before = index.Storage.ReadManifest();

        var stats = await index.Update(
            new InMemoryDocumentSource([
                ("src/a.txt", "replacement is too large"),
                ("src/b.txt", "bravo"),
                ("src/c.txt", "also too large")
            ]),
            [],
            new UpdateOptions(RejectedUpsertPolicy.DeleteExisting));
        var after = index.Storage.ReadManifest();

        Assert.Equal(3, stats.SubmittedUpserts);
        Assert.Equal(1, stats.UpsertedDocuments);
        Assert.Equal(2, stats.RejectedUpserts);
        Assert.Equal(1, stats.DeletedDocuments);
        Assert.Equal(["src/a.txt", "src/c.txt"], stats.RejectedDocumentDetails.Select(static document => document.Path));
        Assert.Equal(before.Generation.Next(), after.Generation);

        using var snapshot = index.OpenSnapshot();
        Assert.False(snapshot.TryGetDocumentBytes(new DocumentKey("src/a.txt"), out _));
        Assert.True(snapshot.TryGetDocumentBytes(new DocumentKey("src/b.txt"), out var healthy));
        Assert.Equal("bravo"u8.ToArray(), healthy.ToArray());
        Assert.False(snapshot.TryGetDocumentBytes(new DocumentKey("src/c.txt"), out _));
    }

    [Fact]
    public async Task RejectedUpsertFailsEntireUpdateByDefault()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            MaxDocumentBytes = 8
        });
        await index.Build(fixture.CreateSource());
        var before = index.Storage.ReadManifest();

        var exception = await Assert.ThrowsAsync<RejectedUpsertException>(() => index.Update(
            new InMemoryDocumentSource([
                ("src/b.txt", "bravo"),
                ("src/a.txt", "replacement is too large")
            ]),
            []));

        var after = index.Storage.ReadManifest();
        Assert.Equal(1, exception.RejectedUpserts);
        Assert.Equal("replacement is too large".Length, exception.RejectedBytes);
        var rejected = Assert.Single(exception.RejectedDocuments);
        Assert.Equal("src/a.txt", rejected.Path);
        Assert.Equal(DocumentRejectionReason.MaximumDocumentBytesExceeded, rejected.Reason);
        Assert.Equal("replacement is too large".Length, rejected.SubmittedBytes);
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(before.Segments.Select(static segment => segment.SegmentId),
            after.Segments.Select(static segment => segment.SegmentId));
        using var snapshot = index.OpenSnapshot();
        Assert.True(snapshot.TryGetDocumentBytes(new DocumentKey("src/a.txt"), out _));
        Assert.False(snapshot.TryGetDocumentBytes(new DocumentKey("src/b.txt"), out _));
    }

    [Theory]
    [InlineData(new byte[] { 0, 1, 2 }, DocumentRejectionReason.BinaryContent)]
    [InlineData(new byte[] { 0xC3, 0x28 }, DocumentRejectionReason.InvalidUtf8)]
    public async Task RejectedUpsertReportsContentFailure(byte[] content, DocumentRejectionReason expectedReason)
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([]));
        var source = new SubmittedDocumentSource([
            new SubmittedDocument("src/rejected.txt", content),
            new SubmittedDocument("src/unread.txt", "valid"u8.ToArray())
        ]);

        var exception = await Assert.ThrowsAsync<RejectedUpsertException>(() => index.Update(source, []));

        var rejected = Assert.Single(exception.RejectedDocuments);
        Assert.Equal("src/rejected.txt", rejected.Path);
        Assert.Equal(expectedReason, rejected.Reason);
        Assert.Equal(1, source.ConsumedDocuments);
    }

    [Fact]
    public async Task IneffectiveDeletionDoesNotPublishManifest()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        var before = index.Storage.ReadManifest();

        var stats = await index.Update(
            new InMemoryDocumentSource([]),
            [new DocumentKey("src/missing.txt")]);
        var after = index.Storage.ReadManifest();

        Assert.Equal(0, stats.DeletedDocuments);
        Assert.Equal(0, stats.PublishedSegments);
        Assert.Equal(before.Generation, after.Generation);
    }

    [Fact]
    public async Task IdenticalUpsertDoesNotPublishSegmentOrManifest()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        var beforeManifest = index.Storage.ReadManifest();
        var beforeStorage = index.GetStorageStats();
        var storage = TestHelpers.GetStorage(index);
        var opensBeforeUpdate = storage.SegmentOpenCount;

        var stats = await index.Update(
            new InMemoryDocumentSource([("src/a.txt", "alpha\n")]),
            []);

        var afterManifest = index.Storage.ReadManifest();
        var afterStorage = index.GetStorageStats();
        Assert.Equal(1, stats.SubmittedUpserts);
        Assert.Equal(0, stats.UpsertedDocuments);
        Assert.Equal(1, stats.SkippedUpserts);
        Assert.Equal("alpha\n".Length, stats.SkippedBytes);
        Assert.Equal(0, stats.ContentBytes);
        Assert.Equal(0, stats.PublishedSegments);
        Assert.Equal(beforeManifest.Generation, afterManifest.Generation);
        Assert.Equal(beforeStorage.ManifestSegmentBytes, afterStorage.ManifestSegmentBytes);
        Assert.Equal(0, index.GetMaintenanceStats().DeadDocuments);
        Assert.Equal(1, storage.SegmentOpenCount - opensBeforeUpdate);
    }

    [Fact]
    public async Task StreamedIdenticalUpsertsReuseLiveSegmentMappings()
    {
        var documents = Enumerable.Range(0, 4)
            .Select(static index => ($"src/{index}.txt", $"content {index}"))
            .ToArray();
        using var fixture = new CorpusFixture();
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            TargetSegmentDocumentCount = 1
        });
        await index.Build(new InMemoryDocumentSource(documents));
        var storage = TestHelpers.GetStorage(index);
        var opensBeforeUpdate = storage.SegmentOpenCount;

        var stats = await index.Update(new InMemoryDocumentSource(documents), []);

        Assert.Equal(documents.Length, stats.SkippedUpserts);
        Assert.Equal(documents.Length, storage.SegmentOpenCount - opensBeforeUpdate);
    }

    [Fact]
    public async Task MixedUpdateSkipsOnlyIdenticalUpserts()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([
            ("src/a.txt", "alpha"),
            ("src/b.txt", "beta")
        ]));

        var stats = await index.Update(
            new InMemoryDocumentSource([
                ("src/a.txt", "alpha"),
                ("src/b.txt", "omega"),
                ("src/c.txt", "gamma")
            ]),
            []);

        Assert.Equal(3, stats.SubmittedUpserts);
        Assert.Equal(2, stats.UpsertedDocuments);
        Assert.Equal(1, stats.SkippedUpserts);
        Assert.Equal("omega".Length + "gamma".Length, stats.ContentBytes);
        Assert.Equal("alpha".Length, stats.SkippedBytes);
        Assert.Equal(1, index.GetMaintenanceStats().DeadDocuments);
    }

    [Fact]
    public async Task ChangedUpsertFindsLatestDocumentWithoutRescanningOlderSegments()
    {
        using var fixture = new CorpusFixture();
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            TargetSegmentDocumentCount = 1
        });
        await index.Build(new InMemoryDocumentSource([
            ("src/a.txt", "alpha"),
            ("src/b.txt", "beta"),
            ("src/c.txt", "gamma")
        ]));
        var storage = TestHelpers.GetStorage(index);
        var opensBeforeUpdate = storage.SegmentOpenCount;

        var stats = await index.Update(
            new InMemoryDocumentSource([("src/c.txt", "omega")]),
            []);

        Assert.Equal(1, stats.UpsertedDocuments);
        Assert.Equal(1, storage.SegmentOpenCount - opensBeforeUpdate);
    }

    [Fact]
    public async Task DuplicateRejectedUpsertPathsAreRejected()
    {
        using var fixture = new CorpusFixture();
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            MaxDocumentBytes = 1,
            RejectedUpsertPolicy = RejectedUpsertPolicy.PreserveExisting
        });
        await index.Build(fixture.CreateSource());

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await index.Update(
            new InMemoryDocumentSource([
                ("src/a.txt", "too large"),
                ("src\\a.txt", "also too large")
            ]),
            []));
    }

    [Fact]
    public async Task CompactDropsDeadDocuments()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha\n"),
            ("src/b.txt", "beta\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        await index.Update(new InMemoryDocumentSource([("src/a.txt", "omega\n")]), [new DocumentKey("src/b.txt")]);

        var compact = await index.Compact(new CompactOptions { MinimumSegmentCountForSmallMerge = 1 });
        Assert.Equal(1, compact.InputSegments);
        Assert.Equal(0, compact.OutputSegments);
        Assert.Equal(0, compact.ContentBytes);

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        snapshot.Search(new SearchQuery("omega", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), collector);
        Assert.Single(collector.Hits);

        var missing = new HitBuffer();
        snapshot.Search(new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), missing);
        Assert.Empty(missing.Hits);
    }

    [Fact]
    public async Task MaintenanceStatsExposeDeadDocumentsAndFragmentationSignals()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha\n"),
            ("src/b.txt", "beta\n"));

        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            TargetSegmentDocumentCount = 1000
        });
        await index.Build(fixture.CreateSource());
        await index.Update(new InMemoryDocumentSource([("src/a.txt", "omega\n")]), [new DocumentKey("src/b.txt")]);

        var stats = index.GetMaintenanceStats();

        Assert.Equal(2, stats.SegmentCount);
        Assert.Equal(3, stats.TotalDocuments);
        Assert.Equal(2, stats.DeadDocuments);
        Assert.Equal(1, stats.LiveDocuments);
        Assert.Equal(2, stats.DeadOrdinalEntries);
        Assert.Equal(1, stats.SegmentsWithDeletions);
        Assert.Equal(1, stats.SmallSegmentCount);
        Assert.True(stats.DeadDocumentFraction > 0d);
        Assert.True(stats.EstimatedReclaimableContentBytes > 0);
        Assert.Equal(stats.ContentBytes - stats.EstimatedReclaimableContentBytes, stats.EstimatedLiveContentBytes);
    }

    [Fact]
    public async Task MaintenanceStatsUseDeletedDocumentSizes()
    {
        var largeContent = new string('x', 10_000);
        using var fixture = new CorpusFixture(
            ("src/small.txt", "x"),
            ("src/large.txt", largeContent));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        await index.Update(
            new InMemoryDocumentSource([]),
            [new DocumentKey("src/large.txt")]);

        var stats = index.GetMaintenanceStats();

        Assert.Equal(largeContent.Length, stats.EstimatedReclaimableContentBytes);
        Assert.Equal(1, stats.EstimatedLiveContentBytes);
    }

    [Fact]
    public async Task MaintenanceStatsUseManifestMetadataWithoutOpeningSegments()
    {
        using var fixture = new CorpusFixture(
            ("src/small.txt", "x"),
            ("src/large.txt", new string('x', 10_000)));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        await index.Update(
            new InMemoryDocumentSource([]),
            [new DocumentKey("src/large.txt")]);
        var storage = TestHelpers.GetStorage(index);
        var segmentOpensBeforeStats = storage.SegmentOpenCount;

        var first = index.GetMaintenanceStats();
        var second = index.GetMaintenanceStats();

        Assert.Equal(10_000, first.EstimatedReclaimableContentBytes);
        Assert.Equivalent(first, second, strict: true);
        Assert.Equal(segmentOpensBeforeStats, storage.SegmentOpenCount);
    }

    [Fact]
    public async Task SnapshotOpenedBeforeUpdateRemainsUsableAfterPublication()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha\n"),
            ("src/b.txt", "beta\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var oldSnapshot = index.OpenSnapshot();
        var oldCollectorBefore = new HitBuffer();
        oldSnapshot.Search(new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), oldCollectorBefore);
        Assert.Single(oldCollectorBefore.Hits);

        await index.Update(new InMemoryDocumentSource([
            ("src/a.txt", "omega\n"),
            ("src/c.txt", "alpha\n")
        ]), []);

        var oldCollectorAfter = new HitBuffer();
        oldSnapshot.Search(new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), oldCollectorAfter);
        var oldPaths = oldCollectorAfter.Hits.Select(hit => oldSnapshot.Materialize(hit, 0, 0).Path).OrderBy(static path => path, StringComparer.Ordinal).ToArray();
        Assert.Equal(["src/a.txt"], oldPaths);

        using var newSnapshot = index.OpenSnapshot();
        var newCollector = new HitBuffer();
        newSnapshot.Search(new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), newCollector);
        var newPaths = newCollector.Hits.Select(hit => newSnapshot.Materialize(hit, 0, 0).Path).OrderBy(static path => path, StringComparer.Ordinal).ToArray();
        Assert.Equal(["src/c.txt"], newPaths);
    }

    [Fact]
    public async Task SnapshotOpenedBeforeCompactionRemainsUsableAfterPublication()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha\n"),
            ("src/b.txt", "beta\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        await index.Update(new InMemoryDocumentSource([("src/a.txt", "omega\n")]), [new DocumentKey("src/b.txt")]);

        using var oldSnapshot = index.OpenSnapshot();
        var beforeCollector = new HitBuffer();
        oldSnapshot.Search(new SearchQuery("omega", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), beforeCollector);
        Assert.Single(beforeCollector.Hits);

        await index.Compact(new CompactOptions());

        var oldCollectorAfter = new HitBuffer();
        oldSnapshot.Search(new SearchQuery("omega", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), oldCollectorAfter);
        Assert.Single(oldCollectorAfter.Hits);
        Assert.Equal("src/a.txt", oldSnapshot.Materialize(oldCollectorAfter.Hits[0], 0, 0).Path);

        using var newSnapshot = index.OpenSnapshot();
        var newCollector = new HitBuffer();
        newSnapshot.Search(new SearchQuery("omega", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), newCollector);
        Assert.Single(newCollector.Hits);
        Assert.Equal("src/a.txt", newSnapshot.Materialize(newCollector.Hits[0], 0, 0).Path);
    }

    [Fact]
    public async Task CountDrivenCompactionDoesNotRewriteOneCleanSegment()
    {
        var documents = Enumerable.Range(0, 150_003)
            .Select(static i => ($"src/{i:D6}.txt", "alpha\n"))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        await index.Update(new InMemoryDocumentSource([("src/000000.txt", "omega\n")]), []);

        var compact = await index.Compact(new CompactOptions { MaxMergedSegments = 1, MinimumSegmentCountForSmallMerge = 1 });

        Assert.Equal(0, compact.InputSegments);

        var storage = new CorpusStorage(fixture.CorpusPath);
        var manifest = storage.ReadManifest();
        Assert.True(manifest.Segments.Count >= 3);

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        snapshot.Search(new SearchQuery("omega", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), collector);
        Assert.Single(collector.Hits);
    }

    [Fact]
    public async Task ShouldCompactIgnoresHealthyTargetSizedSegments()
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            TargetSegmentDocumentCount = 100,
            TargetSegmentContentBytes = 10
        });
        await index.Build(new InMemoryDocumentSource([
            ("src/a.txt", "0123456789"),
            ("src/b.txt", "0123456789"),
            ("src/c.txt", "0123456789")
        ]));
        var options = new CompactOptions { MinimumSegmentCountForSmallMerge = 1 };

        var maintenance = index.GetMaintenanceStats();

        Assert.Equal(3, maintenance.SegmentCount);
        Assert.False(index.ShouldCompact(options));
    }

    [Fact]
    public async Task ShouldCompactTracksActionableWorkForTheSamePolicy()
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            TargetSegmentDocumentCount = 100,
            TargetSegmentContentBytes = 100
        });
        await index.Build(new InMemoryDocumentSource([]));
        await index.Update(new InMemoryDocumentSource([("src/a.txt", "alpha")]), []);
        await index.Update(new InMemoryDocumentSource([("src/b.txt", "beta")]), []);
        await index.Update(new InMemoryDocumentSource([("src/c.txt", "gamma")]), []);
        var options = new CompactOptions { MinimumSegmentCountForSmallMerge = 1 };

        Assert.True(index.ShouldCompact(options));

        var compact = await index.Compact(options);

        Assert.Equal(3, compact.InputSegments);
        Assert.False(compact.HasMoreWork);
        Assert.False(index.ShouldCompact(options));

        await index.Update(
            new InMemoryDocumentSource([]),
            [new DocumentKey("src/a.txt")]);

        Assert.True(index.ShouldCompact(options));

        compact = await index.Compact(options);

        Assert.Equal(1, compact.InputSegments);
        Assert.False(index.ShouldCompact(options));
    }

    [Fact]
    public async Task CompactionPreservesSearchAcrossMultipleSegmentsAndGenerations()
    {
        var documents = Enumerable.Range(0, 100_002)
            .Select(static i => ($"src/{i:D6}.txt", "alpha\n"))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        await index.Update(new InMemoryDocumentSource([
            ("src/000000.txt", "omega\n"),
            ("src/050000.txt", "theta\n")
        ]), [new DocumentKey("src/099999.txt")]);

        await index.Compact(new CompactOptions { MaxMergedSegments = 2 });

        using var snapshot = index.OpenSnapshot();
        var omega = new HitBuffer();
        snapshot.Search(new SearchQuery("omega", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), omega);
        Assert.Single(omega.Hits);

        var theta = new HitBuffer();
        snapshot.Search(new SearchQuery("theta", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), theta);
        Assert.Single(theta.Hits);

        var removed = new HitBuffer();
        snapshot.Search(new SearchQuery(string.Empty, PatternMode.Literal, CaseMode.Sensitive), new PathFilter { PathPrefix = "src/099999" }, removed);
        Assert.Empty(removed.Hits);
    }

    [Fact]
    public async Task CompactionSkipsLowDeadFractionWithoutTriggerHeuristics()
    {
        var documents = Enumerable.Range(0, 100)
            .Select(static i => ($"src/{i:D3}.txt", "alpha\n"))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            TargetSegmentDocumentCount = 1000
        });
        await index.Build(fixture.CreateSource());
        await index.Update(new InMemoryDocumentSource([("src/000.txt", "omega\n")]), []);

        var compact = await index.Compact(new CompactOptions());

        Assert.Equal(0, compact.InputSegments);
        Assert.Equal(0, compact.OutputSegments);
        Assert.Equal(0, compact.MergedDocuments);
    }

    [Fact]
    public async Task CompactionSelectsLowCountHighByteDeletionDebt()
    {
        var documents = Enumerable.Range(0, 100)
            .Select(static i => (
                $"src/{i:D3}.txt",
                i == 0 ? new string('x', 1_000_000) : "alpha\n"))
            .ToArray();
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            TargetSegmentDocumentCount = 1000
        });
        await index.Build(new InMemoryDocumentSource(documents));
        await index.Update(
            new InMemoryDocumentSource([]),
            [new DocumentKey("src/000.txt")]);

        var before = index.GetMaintenanceStats();
        var compact = await index.Compact(new CompactOptions());
        var after = index.GetMaintenanceStats();

        Assert.Equal(0.01d, before.DeadDocumentFraction, precision: 2);
        Assert.True(before.DeadContentFraction > 0.99d);
        Assert.Equal(1, compact.InputSegments);
        Assert.Equal(1, compact.OutputSegments);
        Assert.Equal(99, compact.MergedDocuments);
        Assert.Equal(0d, after.DeadContentFraction);
    }

    [Fact]
    public async Task CompactionDoesNotRewriteContentFullSegments()
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            TargetSegmentDocumentCount = 100,
            TargetSegmentContentBytes = 10
        });
        await index.Build(new InMemoryDocumentSource([
            ("src/a.txt", "0123456789"),
            ("src/b.txt", "0123456789"),
            ("src/c.txt", "0123456789")
        ]));
        var before = index.Storage.ReadManifest();

        var compact = await index.Compact(new CompactOptions { MinimumSegmentCountForSmallMerge = 1 });
        var after = index.Storage.ReadManifest();

        Assert.Equal(0, compact.InputSegments);
        Assert.Equal(0, compact.OutputSegments);
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(
            before.Segments.Select(static segment => segment.SegmentId),
            after.Segments.Select(static segment => segment.SegmentId));
    }

    [Fact]
    public async Task CompactionMergesCleanSegmentsOnlyWhenOutputCountDrops()
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            TargetSegmentDocumentCount = 100,
            TargetSegmentContentBytes = 10
        });
        await index.Build(new InMemoryDocumentSource([]));
        await index.Update(new InMemoryDocumentSource([("src/a.txt", "abc")]), []);
        await index.Update(new InMemoryDocumentSource([("src/b.txt", "def")]), []);
        await index.Update(new InMemoryDocumentSource([("src/c.txt", "ghi")]), []);

        var compact = await index.Compact(new CompactOptions { MinimumSegmentCountForSmallMerge = 1 });

        Assert.Equal(3, compact.InputSegments);
        Assert.Equal(1, compact.OutputSegments);
        Assert.Single(index.Storage.ReadManifest().Segments);
    }

    [Fact]
    public async Task CompactionPreservesOrdinalPathOrderingAcrossUnicodePlanes()
    {
        const string supplementaryPath = "src/\U00010000.txt";
        const string privateUsePath = "src/\uE000.txt";
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([(supplementaryPath, "alpha")]));
        await index.Update(
            new InMemoryDocumentSource([(privateUsePath, "beta")]),
            []);

        var compact = await index.Compact(new CompactOptions { MinimumSegmentCountForSmallMerge = 1 });

        Assert.Equal(2, compact.InputSegments);
        using var snapshot = index.OpenSnapshot();
        Assert.True(snapshot.TryGetDocumentBytes(
            new DocumentKey(supplementaryPath),
            out var supplementaryContent));
        Assert.True(snapshot.TryGetDocumentBytes(
            new DocumentKey(privateUsePath),
            out var privateUseContent));
        Assert.Equal("alpha", Encoding.UTF8.GetString(supplementaryContent.Span));
        Assert.Equal("beta", Encoding.UTF8.GetString(privateUseContent.Span));
    }

    [Fact]
    public async Task CompactionRefusesInsufficientReplacementHeadroom()
    {
        using var fixture = new CorpusFixture();
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            TargetSegmentDocumentCount = 100,
            TargetSegmentContentBytes = 100
        });
        await index.Build(new InMemoryDocumentSource([]));
        await index.Update(new InMemoryDocumentSource([("src/a.txt", "alpha")]), []);
        await index.Update(new InMemoryDocumentSource([("src/b.txt", "beta")]), []);
        var before = index.Storage.ReadManifest();

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            index.Compact(new CompactOptions { MinimumSegmentCountForSmallMerge = 1, MinimumFreeBytesAfterCompaction = long.MaxValue }));

        Assert.Contains("retaining", exception.Message, StringComparison.Ordinal);
        var after = index.Storage.ReadManifest();
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(
            before.Segments.Select(static segment => segment.SegmentId),
            after.Segments.Select(static segment => segment.SegmentId));
        Assert.Equal(0, index.GetStorageStats().RetiredSegmentCount);
    }

    [Fact]
    public async Task CompactionBudgetsTheExactReplacementPack()
    {
        using var fixture = new CorpusFixture();
        var largeBody = new string('x', 2 * 1024 * 1024);
        using (var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            MinimumFreeBytesAfterMutation = 0
        }))
        {
            await index.Build(new InMemoryDocumentSource([
                ("src/large.txt", largeBody),
                ("src/keep.txt", "keep")
            ]));
            await index.Update(
                new InMemoryDocumentSource([("src/large.txt", "replacement")]),
                []);
        }

        var currentBytes = Directory
            .EnumerateFiles(fixture.CorpusPath, "*", SearchOption.AllDirectories)
            .Sum(static path => new FileInfo(path).Length);
        using var constrainedIndex = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            MaximumPhysicalBytes = currentBytes + 256 * 1024,
            MinimumFreeBytesAfterMutation = 0
        });

        var compact = await constrainedIndex.Compact(new CompactOptions { MinimumFreeBytesAfterCompaction = 0 });

        Assert.Equal(1, compact.InputSegments);
        Assert.Equal(1, compact.OutputSegments);
        using var snapshot = constrainedIndex.OpenSnapshot();
        var collector = new HitBuffer();
        snapshot.Search(
            new SearchQuery("keep", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter(),
            collector);
        Assert.Single(collector.Hits);
    }

    [Fact]
    public async Task BuildRefusesAConfiguredCorpusQuotaBeforeCreatingAPack()
    {
        using var fixture = new CorpusFixture();
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            MaximumPhysicalBytes = 64,
            MinimumFreeBytesAfterMutation = 0
        });

        await Assert.ThrowsAsync<IOException>(() =>
            index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha")])));

        Assert.False(File.Exists(Path.Combine(fixture.CorpusPath, "manifest.bin")));
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(fixture.CorpusPath, "segments"),
            "segment.pack",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CompactionReclaimsFullyDeadPacksWithoutReplacementHeadroom()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        await index.Update(
            new InMemoryDocumentSource([]),
            [new DocumentKey("src/a.txt")]);

        var compact = await index.Compact(new CompactOptions { MinimumFreeBytesAfterCompaction = long.MaxValue });

        Assert.Equal(1, compact.InputSegments);
        Assert.Equal(0, compact.OutputSegments);
        Assert.Equal(0, compact.MergedDocuments);
        Assert.Empty(index.Storage.ReadManifest().Segments);
        Assert.Equal(0, index.GetStorageStats().PhysicalSegmentCount);
    }

    [Fact]
    public async Task CompactionFailureBeforeSegmentPublicationPreservesCurrentManifest()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha\n"),
            ("src/b.txt", "beta\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        await index.Update(new InMemoryDocumentSource([("src/a.txt", "omega\n")]), []);

        var storage = TestHelpers.GetStorage(index);
        var before = storage.ReadManifest();
        storage.FaultInjector = new PublicationFaultInjector
        {
            ThrowOnStage = PublicationStage.AfterSegmentFlushBeforePublish
        };

        await Assert.ThrowsAsync<IOException>(async () => await index.Compact(new CompactOptions { MinimumSegmentCountForSmallMerge = 1 }));

        storage.FaultInjector = null;
        storage.EnsureLayout();
        var after = storage.ReadManifest();
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(before.Segments.Select(static segment => segment.SegmentId), after.Segments.Select(static segment => segment.SegmentId));
        var storageStats = index.GetStorageStats();
        Assert.Equal(0, storageStats.RetiredSegmentCount);
        Assert.Equal(0, storageStats.TemporaryBytes);

        using var snapshot = index.OpenSnapshot();
        var omega = new HitBuffer();
        snapshot.Search(new SearchQuery("omega", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), omega);
        Assert.Single(omega.Hits);

        var beta = new HitBuffer();
        snapshot.Search(new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), beta);
        Assert.Single(beta.Hits);
    }

    [Fact]
    public async Task CompactionFailureAfterManifestFlushPreservesCurrentManifest()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha\n"),
            ("src/b.txt", "beta\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        await index.Update(new InMemoryDocumentSource([("src/a.txt", "omega\n")]), []);

        var storage = TestHelpers.GetStorage(index);
        var before = storage.ReadManifest();
        storage.FaultInjector = new PublicationFaultInjector
        {
            ThrowOnStage = PublicationStage.AfterManifestFlushBeforeSwap
        };

        await Assert.ThrowsAsync<IOException>(async () => await index.Compact(new CompactOptions { MinimumSegmentCountForSmallMerge = 1 }));

        storage.FaultInjector = null;
        storage.EnsureLayout();
        var after = storage.ReadManifest();
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(before.Segments.Select(static segment => segment.SegmentId), after.Segments.Select(static segment => segment.SegmentId));
        var storageStats = index.GetStorageStats();
        Assert.Equal(0, storageStats.RetiredSegmentCount);
        Assert.Equal(storageStats.ManifestSegmentBytes, storageStats.PhysicalSegmentBytes);

        using var snapshot = index.OpenSnapshot();
        var omega = new HitBuffer();
        snapshot.Search(new SearchQuery("omega", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), omega);
        Assert.Single(omega.Hits);

        var beta = new HitBuffer();
        snapshot.Search(new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), beta);
        Assert.Single(beta.Hits);
    }
}




