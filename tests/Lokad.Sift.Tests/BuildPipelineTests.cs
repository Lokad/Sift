using Lokad.Sift;
using Lokad.Sift.Indexer;
using Lokad.Sift.Search;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Tests;

public sealed class BuildPipelineTests
{
    [Fact]
    public async Task PartitionDocumentsSplitsOnTargetContentBytes()
    {
        var largeSize = (int)(IndexBuilder.DefaultTargetSegmentContentBytes / 2) + 1;
        var documents = new[]
        {
            CreateStoredDocument("src/a.txt", largeSize),
            CreateStoredDocument("src/b.txt", largeSize),
            CreateStoredDocument("src/c.txt", 16)
        };

        var partitions = IndexBuilder.PartitionDocuments(documents);

        Assert.Equal(2, partitions.Count);
        Assert.Equal(["src/a.txt"], partitions[0].Select(static doc => doc.Path).ToArray());
        Assert.Equal(["src/b.txt", "src/c.txt"], partitions[1].Select(static doc => doc.Path).ToArray());
    }

    [Fact]
    public async Task BuildRejectsControlHeavyBinaryLikeDocuments()
    {
        var binaryLike = Enumerable.Repeat((byte)1, 64).ToArray();
        using var fixture = new CorpusFixture();
        using var index = new SiftIndex(fixture.CorpusPath);

        var stats = await index.Build(new BinaryDocumentSource([new SubmittedDocument("src/a.bin", binaryLike)]));

        Assert.Equal(1, stats.RejectedDocuments);
        Assert.Equal(0, stats.IndexedDocuments);
        Assert.Equal(binaryLike.Length, stats.SubmittedBytes);
        Assert.Equal(binaryLike.Length, stats.RejectedBytes);
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
    public async Task PartitionDocumentsCanUseCustomThresholds()
    {
        var documents = new[]
        {
            CreateStoredDocument("src/a.txt", 5),
            CreateStoredDocument("src/b.txt", 5),
            CreateStoredDocument("src/c.txt", 5)
        };

        var partitions = IndexBuilder.PartitionDocuments(documents, new SiftOptions
        {
            TargetSegmentContentBytes = 8,
            TargetSegmentDocumentCount = 8
        });

        Assert.Equal(3, partitions.Count);
        Assert.Equal(["src/a.txt"], partitions[0].Select(static doc => doc.Path).ToArray());
        Assert.Equal(["src/b.txt"], partitions[1].Select(static doc => doc.Path).ToArray());
        Assert.Equal(["src/c.txt"], partitions[2].Select(static doc => doc.Path).ToArray());
    }

    private static StoredDocument CreateStoredDocument(string path, int contentLength)
    {
        return new StoredDocument
        {
            Path = path,
            PathUtf8 = System.Text.Encoding.UTF8.GetBytes(path),
            Content = new byte[contentLength],
            LineCount = 1,
            EncodedLineIndex = [0, 0, 0, 0, 0],
            Flags = 0,
            MTimeUtcTicks = 0,
            ContentHash64 = 0
        };
    }

    private sealed class BinaryDocumentSource : IDocumentSource
    {
        private readonly IReadOnlyList<SubmittedDocument> _documents;
        private int _index;

        public BinaryDocumentSource(IReadOnlyList<SubmittedDocument> documents)
        {
            _documents = documents;
        }

        public bool TryReadNext(out SubmittedDocument document)
        {
            if (_index >= _documents.Count)
            {
                document = default;
                return false;
            }

            document = _documents[_index++];
            return true;
        }
    }
}




