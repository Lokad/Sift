# Lokad.Sift

Lokad.Sift is a local indexed grep/code-search engine for UTF-8 text, written in pure C#/.NET.

It stores immutable index segments on disk or in memory, serves snapshot-based searches, and supports incremental updates through document upserts and deletions. Its candidate stage uses document-level content bigram/trigram postings plus path trigram postings; match ordering is enforced later by exact verification rather than an ordered-trigram index.

```powershell
dotnet add package Lokad.Sift
```

The package targets .NET 10 (`net10.0`).

## Regex engines

The core package has no runtime NuGet dependencies. Regex search uses invariant
.NET semantics and defaults to .NET Regex, preferring `NonBacktracking` when the
pattern supports it. Classic backtracking has a one-second match timeout;
`RegexTimeoutPolicy` controls whether a timeout stops the search or throws.

Search observes caller cancellation and `MaxElapsedMilliseconds` between matches
within a document. A running .NET regex match is bounded by the existing safety
timeout. `MaxPatternNesting` applies to structural parentheses in content and path
regexes, respecting comments, escapes, character classes and inline whitespace
option scopes.

Applications can supply byte-oriented verification through
`SiftOptions.RegexEngine`, an implementation of Sift's own `ISiftRegexEngine`
contract. The application references its chosen producer directly; no adapter
library is required. See [application-side adapter source](examples/Utf8RegexAdapter.cs).
Successful compilation must guarantee byte-oriented execution on the declared
input domain. Unsupported or unqualified capabilities use Sift's managed fallback.
The current native route is qualified for ASCII documents; Unicode documents
continue through the managed verifier, preserving case folding and byte offsets.

The application owns the engine and keeps its configuration stable while its
indexes/snapshots use it. Sift owns compiled handles and keeps bounded caches
shared across snapshots and index generations. It releases handles after cache
eviction or final context disposal once active users finish. The engine itself
is borrowed and is never disposed by Sift. Inputs, match sinks, and checkpoints
are synchronous borrowed values and must not be retained by the engine.

The pinned `Lokad.Utf8Regex 0.1.0` API does not expose a public compile-time native
capability query. The example therefore declines matching unless the application
supplies a supported capability guarantee. A successful producer constructor
alone does not establish native execution. Tests still reference and exercise
the real producer directly alongside controlled contract providers.

`SearchPlan.RegexRouteReason`, verifier labels, and invocation counters describe
the selected route. Byte-native verification is counted only when an explicitly
accepted engine runs; managed fallback reports its actual non-backtracking usage.
Use [the package check](tools/verify-regex-package.ps1) to qualify the exact Release
package with isolated core-only and producer-backed consumers.

`SearchStats.Utf8RegexCacheHits` and `Utf8RegexCacheMisses` count one native
prepared-query cache lookup per logical search, including overlays. `Explain`
can warm that cache before a search. The process-wide `SiftProcessMetrics`
managed regex hit/miss counters count actual compilation-cache lookups; reusing
an index's prepared query does not access that managed cache.

Planning budgets such as `MaxSelectedNgrams` bound acceleration work without
changing regex results. An indexed union must cover every alternative; when the
budget cannot do so, Sift uses a safe plan. `SearchPlan.AdvisorySeeds` are diagnostic
hints and never exclude candidates. Fallback patterns without a proven indexable
constraint scan their scope, which may examine more candidates.

Regex analysis gives up conservatively on scoped whitespace options (`x`/`X`)
and character classes starting with a literal `]`. These patterns retain .NET
matching semantics through managed verification.

Named capture forms use .NET validation and managed matching while retaining
sound index hints. Unconsumed quantifier or brace tokens also defer to .NET, so
literal acceleration cannot silently accept malformed capture names or quantifiers.

Literal searches include overlapping occurrences. Regex searches follow .NET's
non-overlapping enumeration, including zero-length match progression. Nonempty
exact-literal regexes without whole-word/whole-line constraints can use literal
verification when the literal is valid Unicode; empty or boundary-constrained
exact-literal forms and lone-surrogate regexes use managed matching. Regex-derived
byte constraints also require valid Unicode, so shared prefixes or suffixes that
split surrogate pairs cannot discard content or path matches.
A truly empty query skips content regex preparation and remains a path-listing
shortcut: one hit per document with no content scan.

Literal queries retain ordinal or ordinal-insensitive UTF-16 matching, including
lone surrogates. Literals that cannot be encoded faithfully as UTF-8 supply no
byte/index constraints.
Positions inside a surrogate pair project to the start of that UTF-8 scalar,
consistently with managed regex matching; a projected hit can have an empty byte
range. Mapping both boundaries independently preserves overlapping occurrences.

Include/exclude globs match the complete canonical path. `*` and `?` stay within
a path segment; `**` can cross separators and newline characters. A trailing
newline is part of the path. User-supplied path regexes retain .NET anchor semantics.

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

using var index = new SiftIndex(indexPath, new SiftOptions
{
    TargetSegmentDocumentCount = 50_000, // Start a new segment once about 50k documents accumulate.
    TargetSegmentContentBytes = 64L * 1024 * 1024, // Or once a segment reaches about 64 MiB of content.
    MaximumPhysicalBytes = 128L * 1024 * 1024 * 1024, // Optional corpus quota, including lifecycle metadata.
    MinimumFreeBytesAfterMutation = 1536L * 1024 * 1024, // Preserve room for one default compaction pass.
    MaxPatternCharacters = 2 * 1024, // Bound query and path-filter compilation work.
    MaxPatternNesting = 64, // Bound recursive regex analysis.
    MaxPathFilterCharacters = 16 * 1024 // Bound aggregate path-filter preparation.
});
// Alternative in-memory form:
// using var index = SiftIndex.CreateInMemory();

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

// On process restart, schedule explicit full-pack validation outside latency-sensitive
// work. Normal search and update opens validate structure without reading every pack byte.
var validation = await index.Validate();
Console.WriteLine($"Validated {validation.ValidatedSegments} segment(s) in {validation.ElapsedMilliseconds} ms.");

// 2. Query the index through a snapshot.
using var snapshot = index.OpenSnapshot();

var query = new SearchQuery(
    @"\b(Add|Mul)\b",
    PatternMode.Regex,
    CaseMode.Sensitive);

var filter = new PathFilter { PathPrefix = "src/" };
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

IReadOnlyList<DocumentKey> deletions =
[
    new DocumentKey("src/app/program.cs")
];

var update = await index.Update(upserts, deletions);
Console.WriteLine($"Upserted {update.UpsertedDocuments}, skipped {update.SkippedUpserts} unchanged, deleted {update.DeletedDocuments} in {update.ElapsedMilliseconds} ms.");

// 4. Query again from a fresh snapshot.
using var updatedSnapshot = index.OpenSnapshot();
var updatedCollector = new HitBuffer();
var updatedStats = updatedSnapshot.Search(
    new SearchQuery("Mul", PatternMode.Literal, CaseMode.Sensitive),
    new PathFilter { PathPrefix = "src/lib/" },
    updatedCollector);

Console.WriteLine($"Updated search returned {updatedStats.HitsReturned} hit(s).");

// 5. Optional: compact only when Sift's policy finds actionable debt.
var compactOptions = new CompactOptions();
if (index.ShouldCompact(compactOptions))
{
    CompactStats compact;
    do
    {
        compact = await index.Compact(compactOptions);
        Console.WriteLine($"Compaction merged {compact.MergedDocuments} live documents in {compact.ElapsedMilliseconds} ms.");
    }
    while (compact.HasMoreWork);
}

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
Document bodies are conservatively treated as borrowed until the next
`TryReadNext` call. A source that keeps each body immutable for the whole
operation can set `ContentLifetime` to
`DocumentContentLifetime.UntilOperationCompletes`; Sift then avoids copying the
body before unchanged-update detection and segment publication.
Deletion lists passed to `Update` are read asynchronously without an
eager copy; keep the list and its entries unchanged until the returned task
completes.

For transient overlay workspaces, open an overlay snapshot on top of the shared base index:

```csharp
using var overlay = index.OpenOverlaySnapshot();

await overlay.Update(
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
var foldBack = await overlay.CommitToBase(index);
```

Use the same policy for the recommendation and the maintenance operation:

```csharp
var options = new CompactOptions
{
    MinimumSegmentCountForSmallMerge = 16,
    DeadFractionThreshold = 0.20
};

if (index.ShouldCompact(options))
{
    CompactStats compact;
    do
    {
        compact = await index.Compact(options);
    }
    while (compact.HasMoreWork);
}
```

`ShouldCompact(...)` uses the same replacement eligibility, capacity check, and
per-segment selection as `Compact(...)`; it
does not recommend work merely because a healthy corpus exceeds
`MinimumSegmentCountForSmallMerge` with target-sized segments, or when current
quota and filesystem headroom cannot hold the estimated replacements. The result
is a snapshot-based hint: a concurrent mutation may change the answer before
`Compact(...)` starts, and a resulting no-op is harmless.
`GetMaintenanceStats()` remains available for telemetry and diagnostics, but
aggregate segment count is not itself a reliable compaction predicate.

`Build(...)` and `Compact(...)` publish replacement segments and reclaim the packs
that the new manifest no longer references. A pack remains in `RetiredSegmentBytes`
only while an older live snapshot still needs it, or when the operating system
refuses deletion. Dispose snapshots promptly; release retries reclamation
automatically. Corpus startup also sweeps packs left by a crash between segment
publication and manifest publication. No separate cleanup call is required.
Snapshots acquire an immutable generation lease read-only; opening or disposing a
snapshot does not create files or take the writer lock.

Build and compaction statistics report elapsed time and indexing results.
Managed-memory profiling belongs in the benchmark harness: the library no longer
exposes `SiftOptions.MeasurePeakManagedMemory`, `BuildStats.PeakManagedBytes`, or
`CompactStats.PeakManagedBytes`.

New segment packs use format version 4, with document/content totals and
non-ASCII statistics only. Existing version 3 packs remain readable: their obsolete
histogram bytes are ignored, with bounds and checksum validation preserved. The
oldest version 3 stats layout lacks non-ASCII totals and retains its conservative
Unicode estimate. Opening or validating an old pack does not rewrite it. Updates
may mix versions 3 and 4; normal compaction writes version 4. No rebuild or migration
is needed for the histogram removal. Other unsupported versions still set
`RequiresRebuild` on access/validation. The manifest format is unchanged.

Corrupt manifest, lease, or segment metadata is fail-closed. The `SiftIndex` instance
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
clean segments when at least two fit into one target segment. Before creating each
replacement pack, Sift checks its exact prepared size against both filesystem free
space and `SiftOptions.MaximumPhysicalBytes`, then retains
`CompactOptions.MinimumFreeBytesAfterCompaction` (512 MiB by default). Builds and
updates similarly retain `SiftOptions.MinimumFreeBytesAfterMutation`, whose
1.5 GiB default preserves room for one default compaction pass. Set
`MaximumPhysicalBytes` when filesystem free-space reporting does not reflect a
container or tenant quota.

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

Storage formats, manifests, and mapped segments are implementation details; the
supported package surface is the root `Lokad.Sift` namespace.

Typical usage:

1. create a `SiftIndex`
2. build the index from `IDocumentSource` or `IAsyncEnumerable<SubmittedDocument>`
3. open a snapshot
4. search and materialize hits
5. apply `Update(...)` for upserts/deletions
6. optionally run `Compact(...)`

For transient or embedded scenarios, use `SiftIndex.CreateInMemory()` instead of a filesystem-backed corpus root.

## Notes for consumers

- Paths must be relative and canonicalizable to `/`-separated logical paths.
- Input content is `ReadOnlyMemory<byte>` and must be UTF-8.
- Async document sources are pulled under Sift's writer transaction, providing
  natural backpressure without a consumer-owned producer queue.
- Searches operate on snapshots. Open a new snapshot after updates if you want to observe the new generation.
- Exceptions from `IHitCollector.OnHit` propagate unchanged to the search caller.
  `RegexTimeoutPolicy` controls Sift's regex preparation/verification timeouts;
  it does not convert collector failures into search results.
- Dispose snapshots when they are no longer needed; old generations intentionally defer physical segment reclamation.
- `Update(...)` is batch-atomic: a path cannot appear in both upserts and deletions in the same batch.
- Document source and commit metadata callback failures propagate unchanged.
  A caller failure during build/update does not itself require rebuilding a
  healthy index; its previous committed state and snapshots remain available.
- Rejected upserts fail the entire update by default, preventing callers from
  checkpointing an event whose replacement was not indexed. The exception
  identifies the first rejected path, reason, and submitted size. Set
  `RejectedUpsertPolicy.PreserveExisting` to retain old searchable versions, or
  `DeleteExisting` to remove them. Those non-failing policies report every
  rejected path and reason through `UpdateStats.RejectedDocumentDetails`; full builds
  report the same details through `BuildStats.RejectedDocumentDetails`.
- `BuildOptions.CommitMetadataProvider` and
  `UpdateOptions.CommitMetadataProvider` copy bounded opaque consumer metadata
  into the same manifest commit as the indexed documents. `GetIndexState()`
  returns that metadata with a corpus id, logical content revision, and physical
  storage generation. `ExpectedRevision` rejects an update based on stale logical
  content while allowing content-preserving compaction; `ExpectedIdentity` also
  rejects physical-generation and metadata-only changes.
- `IIndexSnapshot.Identity` and `IIndexSnapshot.CommitMetadata` expose the state
  captured with the snapshot. For an overlay, they identify its immutable base.
- Use `MaterializeWithMatchingLine(...)` when the complete matching line is
  required; ordinary `Materialize(...)` avoids that additional allocation.
- Context budgets count source bytes, including line endings. Display trimming
  uses the complete line so clipping preserves embedded carriage returns.
- `Explain(...).NeedsUnicodeDecode` describes possible content-verifier decoding
  in the physical path scope before candidate/overlay visibility filtering and
  search limits. Empty queries/scopes return false. It excludes display/context
  materialization; use `SearchStats.DecodedChars` for the decoding actually done.
- `VisitDocuments(...)` streams the live document keys in a snapshot without
  requiring a search query or materializing document bodies.
- Use `ShouldCompact(...)` with the same options passed to `Compact(...)`; do not trigger on aggregate segment count alone.
- `Compact(...)` is optional but useful after many updates, and owns cleanup of the segments it replaces.

## Related docs

- [CHANGELOG.md](CHANGELOG.md)
- [SPEC.md](SPEC.md)
- [BENCH.md](BENCH.md)
- [DEPENDENCIES.md](DEPENDENCIES.md)
