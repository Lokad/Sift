# Lokad.Sift

Lokad.Sift is a .NET 10 library for local indexed search over UTF-8 text, written
in C# with no runtime NuGet dependencies. Store indexes on disk or in memory and
search with literals, regexes, and path filters. It supports incremental updates,
immutable snapshots, and transient overlays.

```powershell
dotnet add package Lokad.Sift
```

**Upgrading to 0.4.0:** `SearchOptions` and `SiftOptions.MaxSegmentParallelism`
have been removed. Use parameterless snapshot factories and recompile consumers.
See the [migration notes](CHANGELOG.md#040---2026-10-27).

## Quick start

This complete example builds a disk index and prints matches under `src/`:

```csharp
using System.Text;
using Lokad.Sift;

using var index = new SiftIndex("./sift-index");
await index.Build(new Documents(
    ("src/math.cs", "int Add(int a, int b) => a + b;"),
    ("docs/notes.txt", "Use Add to combine values.")));

using var snapshot = index.OpenSnapshot();
var stats = snapshot.Search(
    new SearchQuery("Add", PatternMode.Literal, CaseMode.Sensitive),
    new PathFilter { PathPrefix = "src/" },
    new PrintHits(snapshot));

Console.WriteLine($"{stats.HitsReturned} hit(s); stop reason: {stats.StopReason}");

sealed class Documents(params (string Path, string Text)[] documents) : IDocumentSource
{
    private int _next;

    public bool TryReadNext(out SubmittedDocument document)
    {
        if (_next == documents.Length)
        {
            document = default;
            return false;
        }

        var (path, text) = documents[_next++];
        document = new SubmittedDocument(path, Encoding.UTF8.GetBytes(text));
        return true;
    }
}

sealed class PrintHits(IIndexSnapshot snapshot) : IHitCollector
{
    public bool OnHit(in RawHit rawHit)
    {
        var hit = snapshot.Materialize(rawHit, contextBefore: 0, contextAfter: 0);
        Console.WriteLine($"{hit.Path}:{hit.StartLine}:{hit.StartColumn}: {hit.MatchText}");
        return true;
    }
}
```

Use `SiftIndex.CreateInMemory()` for an in-memory index. For an existing disk
index, construct `SiftIndex` and open a snapshot directly; `Build` replaces the
full indexed corpus. Document ingestion accepts either `IDocumentSource` or
`IAsyncEnumerable<SubmittedDocument>`.

## Updates and storage

Upsert documents and delete keys in one atomic batch, using the example's
`Documents` helper:

```csharp
await index.Update(
    new Documents(("src/math.cs", "int Multiply(int a, int b) => a * b;")),
    [new DocumentKey("docs/notes.txt")]);

using var latest = index.OpenSnapshot();
```

Existing snapshots retain their captured generation. Open a new snapshot to see
updates, and dispose old snapshots when finished so their storage can be reclaimed.
Rejected upserts fail the batch by default; `RejectedUpsertPolicy` controls
alternative handling. A key cannot appear in both upserts and deletions.

Submit UTF-8 text under relative logical paths, normalized to `/` separators.
Follow the [document buffer lifetime contract](src/Lokad.Sift/DocumentContracts.cs)
and keep deletion lists unchanged until the update task completes.

Use `ShouldCompact(options)` to decide when to call `Compact(options)`, passing
the same options to both. `GetStorageStats()` reports physical and retained
storage; `Validate()` performs explicit full-pack validation. A corrupt index
reports `RequiresRebuild`; `Build` is the explicit recovery operation.

Windows disk writes require OS/filesystem support for POSIX file rename
(`FileRenameInfoEx`). Unsupported configurations fail publication explicitly.

## Search behavior

- `PatternMode.Literal` includes overlapping matches. `PatternMode.Regex` uses
  invariant .NET regex semantics and defaults to the managed .NET verifier.
- `CaseMode` supports sensitive, insensitive, and smart-case matching.
- `PathFilter` supports prefixes, include/exclude globs, and path regexes.
  In globs, `*` and `?` stay within a path segment; `**` can cross separators.
- Search runs synchronously in natural snapshot order. Callbacks run on the caller
  thread; returning `false` stops traversal. Overlays search their own segments
  before visible base documents. Independent searches can run concurrently.
- Queries bound hits, matched documents, scanned bytes, and elapsed time. Check
  `SearchStats.StopReason` for incomplete results, and pass a cancellation token
  when needed. `RegexTimeoutPolicy` controls verifier timeouts.
- Raw hits and borrowed document content belong to the producing snapshot.
  Materialize hits or copy content before disposing that snapshot.

## Other APIs

| API | Purpose |
| --- | --- |
| `OpenOverlaySnapshot()` | Apply transient edits; use `CommitToBase` to fold them into the base index. |
| `VisitDocuments(...)` | Stream live document keys without scanning content. |
| `VisitCandidates(...)` | Stream possible matching documents for exact verification by the application. |
| `GetDocumentKey(hit)` / `GetDocumentBytes(hit)` | Read a hit's key or borrowed UTF-8 content without materializing text. |
| `MaterializeWithMatchingLine(...)` | Include the complete matching line with a materialized hit. |
| `Explain(...)` | Inspect the query plan, selected index constraints, and verifier route. |

Candidate visits may return false positives or the entire path scope. Verify the
original query against unchanged, Unicode-scalar-aligned slices, including any
field-relative anchors. Visit limits replace query search limits; check
`CandidateVisitStats.Completed` and `StopReason` before treating a visit as exhaustive.

For optional byte-oriented regex engines, use `SiftOptions.RegexEngine` and the
[application-side adapter guide](examples/README.md).

## Command line

The repository includes a CLI. Arguments are the command, index directory, then
input directory or search pattern:

```powershell
dotnet run --project src/Lokad.Sift.Cli -c Release --tl:off -- build ./sift-index ./my-repo
dotnet run --project src/Lokad.Sift.Cli -c Release --tl:off -- search ./sift-index "Add|Multiply"
```

Keep the index separate from the input directory, or in an excluded subtree such
as `.sift`. The CLI skips symbolic links and reports rejected input files.
CLI updates remove previously indexed versions of rejected files.

## Development

```powershell
dotnet restore --locked-mode --tl:off -v minimal
./test.ps1 -Configuration Release
./bench.ps1 -Configuration Release
```

Run benchmarks sequentially in Release. See the [benchmark guide](docs/BENCH.md)
for corpus setup and workload selection.

Pack a release with `dotnet pack src/Lokad.Sift/Lokad.Sift.csproj -c Release --tl:off`.
Packages and symbols are written directly to `artifacts/nuget/`.

## Reference

- [Specification and API contracts](docs/SPEC.md)
- [Regex integration](examples/README.md)
- [Dependency policy](docs/DEPENDENCIES.md)
- [Changelog and migrations](CHANGELOG.md)
