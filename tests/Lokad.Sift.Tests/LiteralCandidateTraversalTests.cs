using System.Text;

namespace Lokad.Sift.Tests;

public sealed class LiteralCandidateTraversalTests
{
    [Theory]
    [InlineData(CaseMode.Sensitive)]
    [InlineData(CaseMode.Insensitive)]
    public async Task CorrelatedGramsPreserveMatchesOrderAndScopesAcrossGenerations(CaseMode caseMode)
    {
        var documents = Enumerable.Range(0, 512).ToDictionary(
            i => $"src/{i:D4}.txt",
            i => new string('x', 2048) + "é😀\n" + ((i % 4) switch
            {
                0 => "amberneedle amberneedle",
                1 => "AMBERNEEDLE",
                2 => "amber---needle",
                _ => "needle---amber"
            }), StringComparer.Ordinal);
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            TargetSegmentDocumentCount = 128,
            DirectScanScopeDocumentsThreshold = 0,
            DirectScanScopeBytesThreshold = 0,
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(Source(documents));
        using var retained = index.OpenSnapshot();
        var changed = new Dictionary<string, string>(documents, StringComparer.Ordinal)
        {
            ["src/0002.txt"] = "é😀\namberneedle",
            ["src/new.txt"] = "AMBERNEEDLE amberneedle"
        };
        changed.Remove("src/0000.txt");
        await index.Update(new InMemoryDocumentSource([
            ("src/0002.txt", changed["src/0002.txt"]),
            ("src/new.txt", changed["src/new.txt"])
        ]), [new DocumentKey("src/0000.txt")]);
        using var current = index.OpenSnapshot();
        using var overlay = index.OpenOverlaySnapshot();
        await overlay.Update(new InMemoryDocumentSource([("src/overlay.txt", "π\namberneedle")]),
            [new DocumentKey("src/0001.txt")]);
        var overlaid = new Dictionary<string, string>(changed, StringComparer.Ordinal)
        {
            ["src/overlay.txt"] = "π\namberneedle"
        };
        overlaid.Remove("src/0001.txt");

        Check(retained, documents);
        Check(current, changed);
        Check(overlay, overlaid);
        await index.Compact(new CompactOptions { MinimumSegmentCountForSmallMerge = 1 });
        using var compacted = index.OpenSnapshot();
        Check(compacted, changed);
        Check(retained, documents);

        void Check(IIndexSnapshot snapshot, Dictionary<string, string> contents)
        {
            // An empty search defines natural search order, including overlay-first
            // visibility. Match membership and byte ranges come from the text oracle.
            var listing = new HitBuffer();
            snapshot.Search(new SearchQuery("", PatternMode.Literal, CaseMode.Sensitive)
                { MaxHits = int.MaxValue }, new(), listing);
            var paths = listing.Hits.Select(hit => snapshot.GetDocumentKey(hit).Path).ToArray();
            var includes = contents.Keys.Where((_, i) => i % 3 == 0).ToArray();
            var included = includes.ToHashSet(StringComparer.Ordinal);
            foreach (var scoped in new[] { false, true })
            {
                var expected = new List<(string Path, int Start, int End)>();
                foreach (var path in paths)
                {
                    if (scoped && !included.Contains(path)) continue;
                    var text = contents[path];
                    for (var offset = 0; offset <= text.Length;)
                    {
                        var start = text.IndexOf("amberneedle", offset, caseMode == CaseMode.Sensitive
                            ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                        if (start < 0) break;
                        expected.Add((path, Encoding.UTF8.GetByteCount(text.AsSpan(0, start)),
                            Encoding.UTF8.GetByteCount(text.AsSpan(0, start + "amberneedle".Length))));
                        offset = start + 1;
                    }
                }
                var filter = new PathFilter { IncludeGlobs = scoped ? includes : [] };
                var query = new SearchQuery("amberneedle", PatternMode.Literal, caseMode) { MaxHits = int.MaxValue };
                var hits = new HitBuffer();
                var stats = snapshot.Search(query, filter, hits);
                Assert.Equal(SearchStopReason.None, stats.StopReason);
                Assert.Equal(expected, hits.Hits.Select(hit =>
                    (snapshot.GetDocumentKey(hit).Path, hit.StartByte, hit.EndByte)));

                hits = new HitBuffer();
                snapshot.Search(query with { MaxHits = 2 }, filter, hits);
                Assert.Equal(expected.Take(2), hits.Hits.Select(hit =>
                    (snapshot.GetDocumentKey(hit).Path, hit.StartByte, hit.EndByte)));
            }
        }
    }

    private static InMemoryDocumentSource Source(Dictionary<string, string> documents) =>
        new(documents.Select(document => (document.Key, document.Value)).ToArray());
}
