using System.Text;

namespace Lokad.Sift.Tests;

public sealed class LiteralWholeLineTests
{
    [Theory]
    [InlineData("alpha")]
    [InlineData("café")]
    [InlineData("😀")]
    public async Task LiteralBoundariesIgnoreExactlyOneTrailingCarriageReturn(string stem)
    {
        // Explicit expectations: extra CRs belong to the match, including at EOF.
        var documents = new (string Path, string Text)[]
        {
            ("eof.txt", "π\n" + stem),
            ("lf.txt", "π\n" + stem + "\n"),
            ("crlf.txt", "π\n" + stem + "\r\n"),
            ("crcrlf.txt", "π\n" + stem + "\r\r\n"),
            ("crcrcrlf.txt", "π\n" + stem + "\r\r\r\n"),
            ("bare-cr.txt", "π\n" + stem + "\r"),
            ("two-cr-eof.txt", "π\n" + stem + "\r\r"),
            ("three-cr-eof.txt", "π\n" + stem + "\r\r\r"),
            ("uppercase.txt", "π\n" + stem.ToUpperInvariant() + "\r\r\n"),
            ("prefix.txt", "π\nx" + stem + "\n"),
            ("suffix.txt", "π\n" + stem + "x\n")
        };
        foreach (var mode in new[] { "snapshot", "parallel", "overlay" })
        foreach (var favorIndex in new[] { false, true })
        foreach (var caseMode in new[] { CaseMode.Sensitive, CaseMode.Insensitive, CaseMode.Smart })
        {
            using var index = SiftIndex.CreateInMemory(new SiftOptions
            {
                TargetSegmentDocumentCount = 1,
                DirectScanScopeDocumentsThreshold = favorIndex ? 0 : 256,
                DirectScanScopeBytesThreshold = favorIndex ? 0 : 1024 * 1024,
                DirectScanCandidateDensityThreshold = 1.1
            });
            await index.Build(new InMemoryDocumentSource(documents
                .Select(doc => (doc.Path, mode == "overlay" ? "stale\n" : doc.Text))
                .Concat(Enumerable.Range(0, 64).Select(i => ($"noise/{i}.txt", ""))).ToArray()));
            using IIndexSnapshot snapshot = mode == "overlay"
                ? index.OpenOverlaySnapshot(new SearchOptions(2))
                : index.OpenSnapshot(new SearchOptions(mode == "parallel" ? 2 : 1));
            if (snapshot is IOverlaySnapshot overlay)
                await overlay.Update(new InMemoryDocumentSource(documents), []);

            foreach (var (suffix, paths) in new (string, string[])[]
            {
                ("", ["eof.txt", "lf.txt", "crlf.txt", "bare-cr.txt"]),
                ("\r", ["crcrlf.txt", "two-cr-eof.txt"]),
                ("\r\r", ["crcrcrlf.txt", "three-cr-eof.txt"])
            })
            {
                var pattern = stem + suffix;
                var expected = paths.AsEnumerable();
                if (suffix == "\r" && (caseMode != CaseMode.Sensitive || stem == stem.ToUpperInvariant()))
                    expected = expected.Append("uppercase.txt");
                var hits = new HitBuffer();
                snapshot.Search(new SearchQuery(pattern, PatternMode.Literal, caseMode) { WholeLine = true }, new(), hits);

                Assert.Equal(expected.Order(StringComparer.Ordinal), hits.Hits
                    .Select(hit => snapshot.Materialize(hit, 0, 0).Path).Order(StringComparer.Ordinal));
                foreach (var raw in hits.Hits)
                {
                    Assert.Equal((3, 3 + Encoding.UTF8.GetByteCount(pattern)), (raw.StartByte, raw.EndByte));
                    var hit = snapshot.Materialize(raw, 0, 0);
                    Assert.Equal((2, 1), (hit.StartLine, hit.StartColumn));
                    Assert.Equal(documents.Single(doc => doc.Path == hit.Path).Text.Substring(2, pattern.Length), hit.MatchText);
                }
            }
        }
    }

    [Theory]
    [InlineData("alpha")]
    [InlineData("café")]
    [InlineData("😀")]
    public async Task LiteralsMaySpanCompleteLinesAndKeepEmbeddedCarriageReturns(string stem)
    {
        var pattern = stem + "\r\n" + stem + "\r";
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("a.txt", "π\n" + pattern + "\r\n")]));
        using var snapshot = index.OpenSnapshot();
        var hits = new HitBuffer();

        snapshot.Search(new SearchQuery(pattern, PatternMode.Literal, CaseMode.Sensitive) { WholeLine = true }, new(), hits);

        var raw = Assert.Single(hits.Hits);
        Assert.Equal((3, 3 + Encoding.UTF8.GetByteCount(pattern)), (raw.StartByte, raw.EndByte));
        var hit = snapshot.Materialize(raw, 0, 0);
        Assert.Equal(pattern, hit.MatchText);
        Assert.Equal((2, 1), (hit.StartLine, hit.StartColumn));
    }
}
