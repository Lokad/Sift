using System.Runtime.InteropServices;
using Lokad.Sift;
using Lokad.Sift.Indexer;
using Lokad.Sift.Search;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Tests;

public sealed class BuildPipelineTests
{
    [Theory]
    [InlineData("/src/a.txt")]
    [InlineData("\\src\\a.txt")]
    [InlineData("C:/src/a.txt")]
    [InlineData("C:\\src\\a.txt")]
    [InlineData("C:src/a.txt")]
    [InlineData("c:src/a.txt")]
    public void DocumentKeyRejectsRootedAndDriveQualifiedPaths(string path)
    {
        Assert.Throws<ArgumentException>(() => new DocumentKey(path));
    }

    [Fact]
    public void DocumentContractsRejectMalformedUtf16Paths()
    {
        foreach (var surrogate in new[] { '\uD800', '\uDC00' })
        {
            var path = $"src/{surrogate}.txt";
            Assert.Throws<ArgumentException>(() => new DocumentKey(path));
            Assert.Throws<ArgumentException>(() =>
                new SubmittedDocument(path, ReadOnlyMemory<byte>.Empty));
        }
    }

    [Fact]
    public void SubmittedDocumentRejectsInvalidContentLifetime()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SubmittedDocument(
            "src/a.txt",
            ReadOnlyMemory<byte>.Empty,
            lastModifiedUtc: null,
            contentLifetime: (DocumentContentLifetime)int.MaxValue));
    }

    [Fact]
    public async Task BuildSplitsOnTargetContentBytes()
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            TargetSegmentContentBytes = 8
        });
        var stats = await index.Build(new InMemoryDocumentSource(
            [("src/a.txt", "aaaaa"), ("src/b.txt", "bbbbb"), ("src/c.txt", "c")]));

        Assert.Equal(3, stats.IndexedDocuments);
        Assert.Equal(2, stats.SegmentCount);
        Assert.Equal([1, 2], index.Storage.ReadManifest().Segments.Select(static segment => segment.DocumentCount));
    }

    [Fact]
    public async Task BuildRejectsControlHeavyBinaryLikeDocuments()
    {
        var binaryLike = Enumerable.Repeat((byte)1, 64).ToArray();
        using var fixture = new CorpusFixture();
        using var index = new SiftIndex(fixture.CorpusPath);

        var stats = await index.Build(new SubmittedDocumentSource([new SubmittedDocument("src/a.bin", binaryLike)]));

        Assert.Equal(1, stats.RejectedDocuments);
        Assert.Equal(0, stats.IndexedDocuments);
        Assert.Equal(binaryLike.Length, stats.SubmittedBytes);
        Assert.Equal(binaryLike.Length, stats.RejectedBytes);
        var rejected = Assert.Single(stats.RejectedDocumentDetails);
        Assert.Equal("src/a.bin", rejected.Path);
        Assert.Equal(DocumentRejectionReason.BinaryContent, rejected.Reason);
        Assert.Equal(index.GetIndexIdentity(), stats.CommittedIdentity);
    }

    [Fact]
    public async Task EmptyAndVeryShortDocumentsRemainIndexable()
    {
        using var fixture = new CorpusFixture(
            ("src/empty.txt", string.Empty),
            ("src/one.txt", "a"),
            ("src/two.txt", "ab"));
        using var index = new SiftIndex(fixture.CorpusPath);

        var stats = await index.Build(fixture.CreateSource());

        Assert.Equal(3, stats.IndexedDocuments);
        Assert.Equal("a".Length + "ab".Length, stats.ContentBytes);
        Assert.Equal(stats.SubmittedBytes, stats.ContentBytes);

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        snapshot.Search(new SearchQuery("a", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), collector);
        var paths = collector.Hits.Select(hit => snapshot.Materialize(hit, 0, 0).Path).OrderBy(static path => path, StringComparer.Ordinal).ToArray();
        Assert.Equal(["src/one.txt", "src/two.txt"], paths);
    }

    [Fact]
    public async Task BuildCanUseCustomMaxDocumentBytes()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alphabet"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            MaxDocumentBytes = 4
        });

        var stats = await index.Build(fixture.CreateSource());

        Assert.Equal(1, stats.RejectedDocuments);
        Assert.Equal(0, stats.IndexedDocuments);
        Assert.Equal("alphabet".Length, stats.RejectedBytes);
        Assert.Equal("alphabet".Length, stats.SubmittedBytes);
    }

    [Fact]
    public async Task BuildSplitsOnTargetDocumentCount()
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            TargetSegmentDocumentCount = 1
        });
        var stats = await index.Build(new InMemoryDocumentSource(
            [("src/a.txt", "aaaaa"), ("src/b.txt", "bbbbb"), ("src/c.txt", "ccccc")]));

        Assert.Equal(3, stats.IndexedDocuments);
        Assert.Equal(3, stats.SegmentCount);
        Assert.All(index.Storage.ReadManifest().Segments, static segment => Assert.Equal(1, segment.DocumentCount));
    }

    [Fact]
    public async Task BuildPublishesFullPartitionsBeforeReadingTheWholeSource()
    {
        using var fixture = new CorpusFixture();
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            TargetSegmentContentBytes = 5,
            TargetSegmentDocumentCount = 50_000
        });
        var source = new ObservingDocumentSource(
            [
                new SubmittedDocument("src/a.txt", "aaaaa"u8.ToArray()),
                new SubmittedDocument("src/b.txt", "bbbbb"u8.ToArray()),
                new SubmittedDocument("src/c.txt", "ccccc"u8.ToArray())
            ],
            beforeThirdDocument: () =>
            {
                var segmentsPath = Path.Combine(fixture.CorpusPath, "segments");
                Assert.Single(Directory.EnumerateFiles(segmentsPath, "segment.pack", SearchOption.AllDirectories));
            });

        var stats = await index.Build(source);

        Assert.Equal(3, stats.IndexedDocuments);
        Assert.Equal(3, stats.SegmentCount);
    }

    [Fact]
    public async Task BuildCopiesBorrowedContentBeforeReadingNextDocument()
    {
        using var fixture = new CorpusFixture();
        using var index = new SiftIndex(fixture.CorpusPath);

        var stats = await index.Build(new ReusingBufferDocumentSource());

        using var snapshot = index.OpenSnapshot();
        Assert.True(snapshot.TryGetDocumentBytes(new DocumentKey("src/a.txt"), out var first));
        Assert.True(snapshot.TryGetDocumentBytes(new DocumentKey("src/b.txt"), out var second));
        Assert.Equal("alpha"u8.ToArray(), first.ToArray());
        Assert.Equal("bravo"u8.ToArray(), second.ToArray());
        Assert.Equal(2, stats.IndexedDocuments);
    }

    [Fact]
    public void StableContentAvoidsAnIntermediateBodyCopy()
    {
        var content = "alpha"u8.ToArray();
        var source = new SubmittedDocumentSource([
            new SubmittedDocument(
                "src/a.txt",
                content,
                lastModifiedUtc: null,
                contentLifetime: DocumentContentLifetime.UntilOperationCompletes)
        ]);

        var load = IndexBuilder.LoadDocuments(
            source,
            CancellationToken.None,
            new SiftOptions(),
            operationName: "build",
            RejectedUpsertPolicy.FailUpdate,
            retainContentBeyondOperation: false);

        Assert.True(MemoryMarshal.TryGetArray(load.Documents.Single().Content, out var stored));
        Assert.Same(content, stored.Array);
    }

    [Fact]
    public async Task FailedStreamingBuildRemovesAlreadyPublishedPartitions()
    {
        using var fixture = new CorpusFixture();
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            TargetSegmentContentBytes = 5,
            TargetSegmentDocumentCount = 50_000
        });
        var source = new SubmittedDocumentSource(
            [
                new SubmittedDocument("src/a.txt", "aaaaa"u8.ToArray()),
                new SubmittedDocument("src/b.txt", "bbbbb"u8.ToArray()),
                new SubmittedDocument("src/a.txt", "ccccc"u8.ToArray())
            ]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => index.Build(source));

        var storage = TestHelpers.GetStorage(index);
        Assert.False(storage.HasManifest);
        Assert.Empty(Directory.EnumerateFiles(storage.SegmentsPath, "segment.pack", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFileSystemEntries(storage.TempPath));
    }

    [Fact]
    public async Task FailedInitialManifestSwapRollsBackManifestAndPacksTogether()
    {
        using var fixture = new CorpusFixture();
        using var index = new SiftIndex(fixture.CorpusPath);
        var storage = TestHelpers.GetStorage(index);
        storage.FaultInjector = new PublicationFaultInjector
        {
            ThrowOnStage = PublicationStage.AfterManifestFlushBeforeSwap
        };

        await Assert.ThrowsAsync<IOException>(
            () => index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha\n")])));

        storage.FaultInjector = null;
        Assert.False(storage.HasManifest);
        Assert.False(File.Exists(storage.ManifestPath + ".tmp"));
        Assert.Empty(Directory.EnumerateFiles(storage.SegmentsPath, "segment.pack", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(storage.GenerationLeasesPath, "*.lease"));

        var retry = await index.Build(
            new InMemoryDocumentSource([("src/a.txt", "alpha\n")]));

        Assert.Equal(1, retry.IndexedDocuments);
        using var snapshot = index.OpenSnapshot();
        Assert.True(snapshot.TryGetDocumentBytes(
            new DocumentKey("src/a.txt"),
            out var content));
        Assert.Equal("alpha\n"u8.ToArray(), content.ToArray());
    }

    [Fact]
    public void SegmentIndexConstructionObservesCancellationInsideADocument()
    {
        using var cancellation = new CancellationTokenSource();
        var document = new CancelOnContentReadDocument(cancellation);
        using var workspaceLease = SegmentIndexBuilder.AcquireWorkspaceLease();

        Assert.Throws<OperationCanceledException>(() =>
            SegmentWriter.WriteToBytes(
                segmentId: new SegmentId(1),
                [document],
                cancellation.Token));
        Assert.Equal(1, document.ContentReadCount);
    }

    private sealed class ObservingDocumentSource : IDocumentSource
    {
        private readonly IReadOnlyList<SubmittedDocument> _documents;
        private readonly Action _beforeThirdDocument;
        private int _index;

        public ObservingDocumentSource(IReadOnlyList<SubmittedDocument> documents, Action beforeThirdDocument)
        {
            _documents = documents;
            _beforeThirdDocument = beforeThirdDocument;
        }

        public bool TryReadNext(out SubmittedDocument document)
        {
            if (_index == 2)
            {
                _beforeThirdDocument();
            }

            if (_index >= _documents.Count)
            {
                document = default;
                return false;
            }

            document = _documents[_index++];
            return true;
        }
    }

    private sealed class ReusingBufferDocumentSource : IDocumentSource
    {
        private readonly byte[] _buffer = new byte[5];
        private int _index;

        public bool TryReadNext(out SubmittedDocument document)
        {
            if (_index >= 2)
            {
                document = default;
                return false;
            }

            var path = _index == 0 ? "src/a.txt" : "src/b.txt";
            (_index == 0 ? "alpha"u8 : "bravo"u8).CopyTo(_buffer);
            _index++;
            document = new SubmittedDocument(path, _buffer);
            return true;
        }
    }

    private sealed class CancelOnContentReadDocument : ISegmentBuildDocument
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly byte[] _content = "alphabet"u8.ToArray();

        public CancelOnContentReadDocument(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public int ContentReadCount { get; private set; }
        public int LineCount => 1;
        public SegmentDocumentFlags Flags => 0;
        public long MTimeUtcTicks => 0;
        public ulong ContentHash64 => 0;

        public ReadOnlySpan<byte> GetPathUtf8() => "src/a.txt"u8;

        public ReadOnlySpan<byte> GetContentBytes()
        {
            ContentReadCount++;
            _cancellation.Cancel();
            return _content;
        }

        public ReadOnlySpan<byte> GetEncodedLineIndex() => [0];
    }
}




