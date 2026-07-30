# Lokad.Sift

Lokad.Sift is a local indexed grep/code-search engine for UTF-8 text, written in pure C#/.NET.

It stores immutable index segments on disk or in memory, serves snapshot-based searches, and supports incremental updates through document upserts and deletions. Its candidate stage uses document-level content bigram/trigram postings plus path trigram postings; match ordering is enforced later by exact verification rather than an ordered-trigram index.

```powershell
dotnet add package Lokad.Sift
```

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
    TargetSegmentContentBytes = 64L * 1024 * 1024 // Or once a segment reaches about 64 MiB of content.
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

var storage = index.GetStorageStats();
Console.WriteLine(
    $"manifestBytes={storage.ManifestSegmentBytes} " +
    $"physicalBytes={storage.PhysicalSegmentBytes} " +
    $"retiredBytes={storage.RetiredSegmentBytes} " +
    $"deferredBytes={storage.SnapshotDeferredSegmentBytes} " +
    $"pendingDeletionBytes={storage.PendingDeletionSegmentBytes} " +
    $"blockedBytes={storage.BlockedReclamationSegmentBytes} " +
    $"reclamationBlocked={storage.IsReclamationBlocked}");
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

`Build(...)` and `Compact(...)` publish replacement segments and reclaim the packs
that the new manifest no longer references. A pack remains in `RetiredSegmentBytes`
only while an older live snapshot still needs it, or when the operating system
refuses deletion. Dispose snapshots promptly; release retries reclamation
automatically. Corpus startup also sweeps packs left by a crash between segment
publication and manifest publication. No separate cleanup call is required.
Snapshots acquire an immutable generation lease read-only; opening or disposing a
snapshot does not create files or take the writer lock.

Corrupt manifest, lease, or segment metadata is fail-closed. The `Sift` instance
remains constructible and reports `RequiresRebuild`; searches, updates, and
maintenance reject the unusable index. Calling `Build(...)` is the explicit
recovery operation: it purges the derived index artifacts, publishes a fresh
generation, and clears `RequiresRebuild`. An interrupted recovery remains marked
as requiring another full build instead of appearing to be an empty index.

To keep disk growth bounded, `Build(...)` and a compaction that would replace
segments are refused while an earlier retired generation is still leased. Dispose
the old snapshot and retry. A compaction pass selects at most
`CompactOptions.MaxInputSegmentBytes` of physical packs by default, except that one
larger debt-bearing segment remains eligible. Count-driven maintenance only merges
clean segments when at least two fit into one target segment. Compaction still needs
temporary disk headroom for the selected segments' new packs before the replaced
packs can be deleted, so it requires space for a same-size replacement plus
`CompactOptions.MinimumFreeBytesAfterCompaction` (512 MiB by default) when the
filesystem exposes available-space information.

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
- Dispose snapshots when they are no longer needed; old generations intentionally defer physical segment reclamation.
- `Update(...)` is batch-atomic: a path cannot appear in both upserts and deletions in the same batch.
- `Compact(...)` is optional but useful after many updates, and owns cleanup of the segments it replaces.

## Related docs

- [SPEC.md](SPEC.md)
- [BENCH.md](BENCH.md)
- [DEPENDENCIES.md](DEPENDENCIES.md)
