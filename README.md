# Lokad.Sift

Lokad.Sift is a local indexed grep/code-search engine for UTF-8 text, written in pure C#/.NET.

It stores immutable index segments on disk or in memory, serves snapshot-based searches, and supports incremental updates through document upserts and deletions. Its candidate stage uses document-level content bigram/trigram postings plus path trigram postings; match ordering is enforced later by exact verification rather than an ordered-trigram index.

## C# example

```csharp
using System;
using System.Text;
using System.Threading.Tasks;
using Lokad.Sift;

await Example();

static async Task Example()
{
var indexPath = @"C:\data\sift-index";

using var index = new Sift(indexPath, new SiftOptions
{
    TargetSegmentDocumentCount = 50_000, // Start a new segment once about 50k documents accumulate.
    TargetSegmentContentBytes = 256L * 1024 * 1024 // Or once a segment reaches about 256 MiB of content.
});
// Alternative in-memory form:
// using var index = Sift.CreateInMemory();

// 1. Build the initial index.
var initialDocuments = new InMemoryDocumentSource(
[
    new SubmittedDocument(
        "src/app/program.cs",
        Encoding.UTF8.GetBytes("""
        using System;

        Console.WriteLine("hello");
        """)),
    new SubmittedDocument(
        "src/lib/math.cs",
        Encoding.UTF8.GetBytes("""
        namespace Demo;

        static class MathEx
        {
            public static int Add(int a, int b) => a + b;
        }
        """))
]);

var build = await index.Build(initialDocuments);
Console.WriteLine($"Indexed {build.IndexedDocuments} documents in {build.ElapsedMilliseconds} ms.");

// 2. Query the index through a snapshot.
using var snapshot = index.OpenSnapshot(new SearchOptions(SegmentParallelism: 1));
// Or simply: using var snapshot = index.OpenSnapshot();

var query = new SearchQuery(
    Pattern: @"\b(Add|Mul)\b",
    PatternMode: PatternMode.Regex,
    CaseMode: CaseMode.Sensitive);

var filter = new PathFilter(PathPrefix: "src/");
var collector = new HitBuffer();
var stats = snapshot.Search(query, filter, collector);

Console.WriteLine($"Search returned {stats.HitsReturned} hit(s) in {stats.ElapsedMilliseconds} ms.");

foreach (var rawHit in collector.Hits)
{
    var hit = snapshot.Materialize(
        rawHit,
        contextBefore: 1, // Include 1 line before each match in the materialized result.
        contextAfter: 1); // Include 1 line after each match in the materialized result.
    Console.WriteLine($"{hit.Path}:{hit.StartLine}:{hit.StartColumn} {hit.MatchText}");
}

// 3. Upsert one document and delete another.
var upserts = new InMemoryDocumentSource(
[
    new SubmittedDocument(
        "src/lib/math.cs",
        Encoding.UTF8.GetBytes("""
        namespace Demo;

        static class MathEx
        {
            public static int Add(int a, int b) => checked(a + b);
            public static int Mul(int a, int b) => a * b;
        }
        """)),
    new SubmittedDocument(
        "src/lib/strings.cs",
        Encoding.UTF8.GetBytes("""
        namespace Demo;

        static class StringEx
        {
            public static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);
        }
        """))
]);

ReadOnlySpan<DocumentKey> deletions =
[
    new DocumentKey("src/app/program.cs")
];

var update = await index.Update(upserts, deletions);
Console.WriteLine($"Upserted {update.UpsertedDocuments} document(s), deleted {update.DeletedDocuments} in {update.ElapsedMilliseconds} ms.");

// 4. Query again from a fresh snapshot.
using var updatedSnapshot = index.OpenSnapshot();
var updatedCollector = new HitBuffer();
var updatedStats = updatedSnapshot.Search(
    new SearchQuery("Mul", PatternMode.Literal, CaseMode.Sensitive),
    new PathFilter(PathPrefix: "src/lib/"),
    updatedCollector);

Console.WriteLine($"Updated search returned {updatedStats.HitsReturned} hit(s).");

// 5. Optional: compact delta segments back into fewer immutable segments.
var compact = await index.Compact(new CompactOptions());
Console.WriteLine($"Compaction merged {compact.MergedDocuments} live documents in {compact.ElapsedMilliseconds} ms.");
}
```

The example assumes a small in-memory `IDocumentSource` implementation for the submitted documents and a simple `IHitCollector` that stores `RawHit` values in a list.

For transient overlay workspaces, open an overlay snapshot on top of the shared base index:

```csharp
using var overlay = index.OpenOverlaySnapshot();

await overlay.Apply(
    new InMemoryDocumentSource(
    [
        new SubmittedDocument("src/lib/math.cs", Encoding.UTF8.GetBytes("static class MathEx { int Mul(int a, int b) => a * b; }"))
    ]),
    [new DocumentKey("src/app/program.cs")]);

var overlayCollector = new HitBuffer();
overlay.Search(
    new SearchQuery("Mul|program", PatternMode.Regex, CaseMode.Sensitive),
    new PathFilter(),
    overlayCollector);

// The base index is unchanged until you explicitly fold the overlay back.
var foldBack = await overlay.ApplyTo(index);
```

To decide explicitly whether compaction is worth doing, inspect the maintenance stats first:

```csharp
var maintenance = index.GetMaintenanceStats();

Console.WriteLine(
    $"segments={maintenance.SegmentCount} " +
    $"deadDocs={maintenance.DeadDocuments} " +
    $"deadFraction={maintenance.DeadDocumentFraction:F3} " +
    $"reclaimableBytes={maintenance.EstimatedReclaimableContentBytes}");

if (maintenance.SegmentCount > 16 || maintenance.DeadDocumentFraction >= 0.20)
{
    var compact = await index.Compact(new CompactOptions(
        SegmentCountTrigger: 16,
        DeadFractionThreshold: 0.20));

    Console.WriteLine($"Compaction merged {compact.MergedDocuments} live documents.");
}
```

## What Sift is for

- local code and text search over large corpora
- literal and regex queries
- path prefix, glob, and path-regex filtering
- incremental updates without rebuilding the whole index
- transient overlay workspaces on top of a shared base index
- snapshot-consistent readers

## Project layout

The main consumer-facing assembly is:

- `Lokad.Sift`

For advanced storage-engine consumers, `Lokad.Sift.Storage` is also a supported public namespace for direct manifest/segment access and storage-oriented tooling.

Typical usage:

1. create a `Sift`
2. build the index from `IDocumentSource`
3. open a snapshot
4. search and materialize hits
5. apply `Update(...)` for upserts/deletions
6. optionally run `Compact(...)`

For transient or embedded scenarios, use `Sift.CreateInMemory()` instead of a filesystem-backed corpus root.

## Notes for consumers

- Paths must be relative and canonicalizable to `/`-separated logical paths.
- Input content is `ReadOnlyMemory<byte>` and must be UTF-8.
- Searches operate on snapshots. Open a new snapshot after updates if you want to observe the new generation.
- `Update(...)` is batch-atomic: a path cannot appear in both upserts and deletions in the same batch.
- `Compact(...)` is optional but useful after many updates.

## Related docs

- [SPEC.md](SPEC.md)
- [BENCH.md](BENCH.md)
- [DEPENDENCIES.md](DEPENDENCIES.md)
