using System.Collections;

namespace Lokad.Sift.Tests;

public sealed class DeletionCollectionFailureTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var disk in new[] { false, true })
        foreach (var asyncSource in new[] { false, true })
        foreach (var phase in new[] { "get-enumerator", "move-next", "current", "dispose" })
            yield return [disk, asyncSource, phase];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CallerFailureLeavesCommittedStateUsable(bool disk, bool asyncSource, string phase)
    {
        foreach (var failure in new Exception[] { new InvalidDataException("External data failure."),
            new FileNotFoundException("External file failure."), new DirectoryNotFoundException("External directory failure."),
            new EndOfStreamException("External stream failure."), new IOException("External I/O failure."),
            new OperationCanceledException("External cancellation.") })
        {
            using var fixture = new CorpusFixture();
            using var index = disk ? new SiftIndex(fixture.CorpusPath) : SiftIndex.CreateInMemory();
            await index.Build(new InMemoryDocumentSource([("old.txt", "alpha")]),
                new BuildOptions { CommitMetadataProvider = _ => "checkpoint"u8.ToArray() });
            var before = index.GetIndexState();
            using var retained = index.OpenSnapshot();
            var deletions = new FailingDeletions(phase, failure);
            var caught = await Record.ExceptionAsync(async () =>
            {
                if (asyncSource) await index.Update(EmptyAsync(), deletions);
                else await index.Update(new InMemoryDocumentSource([]), deletions);
            });
            Assert.Same(failure, caught);
            Assert.Contains(nameof(FailingDeletions), failure.StackTrace ?? "");
            Assert.Equal(phase == "get-enumerator" ? 0 : 1, deletions.DisposeCalls);
            Assert.False(index.RequiresRebuild);
            Assert.Equal(before.Identity, index.GetIndexIdentity());
            Assert.Equal(before.CommitMetadata.ToArray(), index.GetIndexState().CommitMetadata.ToArray());
            Assert.True(retained.TryGetDocumentBytes(new DocumentKey("old.txt"), out _));
            using (var fresh = index.OpenSnapshot()) Assert.True(fresh.TryGetDocumentBytes(new DocumentKey("old.txt"), out _));
            Assert.Equal(0, index.GetStorageStats().TemporaryBytes);
            await index.Validate();
            if (disk)
            {
                using var reopened = new SiftIndex(fixture.CorpusPath);
                Assert.Equal(before.Identity, reopened.GetIndexIdentity());
                await reopened.Validate();
            }
            await index.Update(new InMemoryDocumentSource([("new.txt", "beta")]), [new DocumentKey("old.txt")]);
            using var updated = index.OpenSnapshot();
            Assert.True(updated.TryGetDocumentBytes(new DocumentKey("new.txt"), out _));
            Assert.False(updated.TryGetDocumentBytes(new DocumentKey("old.txt"), out _));
            Assert.True(retained.TryGetDocumentBytes(new DocumentKey("old.txt"), out _));
        }
    }

    private static async IAsyncEnumerable<SubmittedDocument> EmptyAsync()
    {
        await Task.Yield();
        yield break;
    }

    [Theory]
    [InlineData("get-enumerator")]
    [InlineData("move-next")]
    [InlineData("current")]
    [InlineData("dispose")]
    public async Task OverlayDeletionFailuresPreserveVisibleState(string phase)
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("base.txt", "alpha")]));
        using var overlay = index.OpenOverlaySnapshot();
        await overlay.Update(new InMemoryDocumentSource([("overlay.txt", "beta")]), []);
        var failure = new InvalidDataException("External deletion failure.");
        var caught = await Record.ExceptionAsync(() => overlay.Update(new InMemoryDocumentSource([]), new FailingDeletions(phase, failure)));
        Assert.Same(failure, caught);
        Assert.True(overlay.TryGetDocumentBytes(new DocumentKey("base.txt"), out _));
        Assert.True(overlay.TryGetDocumentBytes(new DocumentKey("overlay.txt"), out _));
        await overlay.Update(new InMemoryDocumentSource([]), [new DocumentKey("overlay.txt")]);
        Assert.False(overlay.TryGetDocumentBytes(new DocumentKey("overlay.txt"), out _));
    }

    private sealed class FailingDeletions(string phase, Exception failure) : IReadOnlyList<DocumentKey>
    {
        public int DisposeCalls { get; private set; }
        public int Count => 1;
        public DocumentKey this[int index] => new("missing.txt");
        public IEnumerator<DocumentKey> GetEnumerator() => phase == "get-enumerator" ? throw failure : new Enumerator(this);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        private sealed class Enumerator(FailingDeletions owner) : IEnumerator<DocumentKey>
        {
            private bool _moved;
            public DocumentKey Current => owner.GetCurrent();
            object IEnumerator.Current => Current;
            public bool MoveNext()
            {
                owner.CheckMoveNext();
                if (_moved) return false;
                _moved = true;
                return true;
            }
            public void Dispose() => owner.DisposeEnumerator();
            public void Reset() => throw new NotSupportedException();
        }
        private DocumentKey GetCurrent() => phase == "current" ? throw failure : new("missing.txt");
        private void CheckMoveNext() { if (phase == "move-next") throw failure; }
        private void DisposeEnumerator() { DisposeCalls++; if (phase == "dispose") throw failure; }
    }
}
