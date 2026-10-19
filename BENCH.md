# Lokad.Sift Real-Corpus Benchmark Spec

## 1. Goal

This document specifies how Lokad.Sift should be benchmarked against real local codebases rather than only synthetic microbenchmarks.

The objective is to answer these questions with evidence:

- How fast is full indexing on a sizeable real repository?
- How large is the resulting on-disk index?
- How does query latency behave for realistic developer searches?
- How often does the planner choose direct scan vs indexed search?
- How often do regex queries stay on the byte-regex path vs fall back?
- Where do decode, candidate, and verifier costs actually land on a real corpus?

This benchmark track is intended to guide architecture and performance work, not just to produce a vanity number.

## 2. Local corpus convention

Real benchmark repositories are checked out locally under:

```text
bench/repos/
```

This directory is git-ignored by convention and must never be treated as a source-controlled artifact location.

Persistent benchmark indexes should be cached under:

```text
bench/indexes/
```

This directory is also git-ignored and is intended for reusable query-side benchmark indexes.

Expected shape:

```text
.
  bench/
    repos/
      runtime/
      roslyn/
      aspnetcore/
      llvm-project/
      dotnet-runtime/
      vscode/
    indexes/
```

The benchmark runner must treat these repositories as optional local inputs. If a repository is missing, the runner should skip it cleanly rather than fail the entire benchmark session.

## 3. Corpus classes

The benchmark suite should cover at least three different repository profiles.

### 3.1 Native / very large

A large C or C++ repository with many source files and headers.

Examples:

- `llvm-project`
- `linux`
- `chromium` subset if locally practical

Purpose:

- stress large file counts
- stress trigram selectivity on identifier-heavy code
- stress build throughput and index size

### 3.2 Managed / mixed text

A large .NET or mixed managed repository.

Examples:

- `dotnet-runtime`
- `roslyn`
- `aspnetcore`

Purpose:

- stress realistic identifier, namespace, and path-filter queries
- exercise Sift on a code shape close to its expected local usage

### 3.3 Web / script heavy

A large TypeScript/JavaScript repository.

Examples:

- `vscode`
- `typescript`
- `next.js`

Purpose:

- stress many medium-sized files
- exercise path globs, short literals, and regexes common in frontend stacks

## 4. Benchmark phases

The runner should support an optional capped sanity mode for fast roundtrips on real repositories, for example `--max-files=100`.

When a cap is active:

- the JSON report must record the cap explicitly
- the run must be treated as a tooling or sanity benchmark, not as a full baseline
- build throughput, planner behavior, fallback rate, and index inflation must not be interpreted as representative of the uncapped corpus

Every real-corpus run should execute these phases separately.

### 4.1 Build

Measure:

- submitted documents
- indexed documents
- rejected documents
- submitted bytes
- accepted original content bytes
- elapsed build time for full ingest, index build, segment write, and manifest publication
- produced segment count
- total segment-pack bytes
- index inflation ratio versus accepted original content bytes
- index inflation percent versus accepted original content bytes
- build documents per second
- build bytes per second

### 4.2 Cold-open snapshot

Measure:

- snapshot open latency
- mapped bytes after open
- segment count

### 4.3 Warm search pack

Run a fixed query pack over an already-open snapshot after at least one warmup pass.

Measure per query:

- elapsed time
- scope documents
- scope bytes
- postings bytes read
- candidate documents after index
- candidate documents after precheck
- verified documents
- bytes scanned
- decoded chars
- regex invocations
- byte-regex invocations
- fallback-regex invocations
- hits returned
- stop reason
- explain plan family
- explain candidate kind
- explain verifier kind

### 4.4 Optional update / compaction

This phase is not mandatory for the first implementation, but should be added later.

Measure:

- update time for a representative upsert/delete batch
- compaction time
- resulting segment count
- manifest and segment size changes

## 5. Query pack design

The real-corpus benchmark must use a checked-in JSON query pack, not ad hoc command-line strings.

Current checked packs:

- `src/Lokad.Sift.Benchmarks/Assets/real-default.json`
  Small regression pack used for tight benchmarking loops and stable before/after comparisons.
- `src/Lokad.Sift.Benchmarks/Assets/real-diverse.json`
  Broader diagnostic pack used to discover unoptimized query families before investing in narrower polish.
  It should preferentially cover realistic repo-search patterns such as async APIs, DI/service registration, logging calls, test-method discovery, build-property searches, and plausible API-family alternations used in real code search.

Each query entry should include:

- `id`
- `pattern`
- `patternMode`
- `caseMode`
- `wholeWord`
- `wholeLine`
- `multiline`
- `dotAll`
- `pathPrefix`
- `includeGlobs`
- `excludeGlobs`
- `pathRegex`
- `intent`

`intent` is descriptive metadata for humans only. It should categorize the query without affecting execution.

Recommended categories:

- `literal_identifier`
- `literal_short`
- `literal_long`
- `literal_path_scoped`
- `regex_byte_fast`
- `regex_fallback`
- `regex_multiline`
- `path_only`

## 6. Query pack content rules

The initial checked-in real-corpus query packs should aim for realism over exhaustiveness.

Each corpus should have roughly 15 to 30 queries with a balanced mix of:

- exact identifier lookups
- common API or symbol name lookups
- short literals that are likely to be dense
- longer literals that should be selective
- whole-word queries
- path-prefixed searches
- include-glob searches
- regexes intended to remain on the byte-regex path
- regexes intentionally forcing fallback

Examples of realistic query shapes:

- `TaskScheduler`
- `TryParse`
- `TODO`
- `Content-Length`
- `\bHttpClient\b`
- `async\s+Task<`
- `namespace\s+System\.`
- path-prefix scoped literals such as `src/` + `Build`
- include-glob constrained searches such as `**/*.cs`

The benchmark suite should avoid query packs made only of rare, highly selective queries, because that would distort planner and verifier behavior.

## 7. Output format

Real-corpus benchmark output should be JSON only.

No YAML output should be introduced.

Recommended output structure:

```text
{
  "toolVersion": "...",
  "timestampUtc": "...",
  "machine": {
    "os": "...",
    "cpu": "...",
    "logicalCores": 0,
    "runtime": "net10.0"
  },
  "corpora": [
    {
      "id": "...",
      "path": "...",
      "build": {
        "submittedDocuments": 0,
        "indexedDocuments": 0,
        "rejectedDocuments": 0,
        "submittedBytes": 0,
        "contentBytesOriginal": 0,
        "indexBytesOnDisk": 0,
        "indexInflationRatio": 0.0,
        "indexInflationPercent": 0.0,
        "buildElapsedMilliseconds": 0,
        "buildDocsPerSecond": 0.0,
        "buildBytesPerSecond": 0.0
      },
      "snapshot": { ... },
      "queries": [ ... ]
    }
  ]
}
```

The JSON should be written to a caller-selected path, for example:

```text
bench/out/real-corpus-*.json
```

This output is an artifact, not a checked-in source file.

## 8. First acceptance targets

The first version of the real-corpus benchmark does not need strict hard gates.

Instead, it should establish baselines that are stable and reviewable.

Minimum success criteria:

- the runner can index at least one large real repository from `bench/repos/`
- the runner emits a complete JSON report
- the query pack runs end-to-end without manual intervention
- explain/search metrics are recorded per query
- byte-regex vs fallback-regex usage is visible in the report

## 9. What should be reviewed from results

A benchmark run is only useful if the results are interpreted.

Every real-corpus run should make these questions easy to answer:

- How large is the index relative to the original accepted corpus bytes?
- Is build throughput acceptable in docs/sec and bytes/sec?
- Which queries are doing direct scan that should probably be indexed?
- Which queries are reading too many postings bytes for too few hits?
- Which regexes still fall back too often?
- Which path filters are expensive because they force string verification?
- Is build throughput collapsing on a specific repository shape?
- Is index size acceptable relative to content size?

## 10. Intended implementation path

The code implementing this spec should likely live in `Lokad.Sift.Benchmarks`.

Suggested order:

1. add a JSON corpus manifest or simple convention-based repo discovery under `bench/repos/`
2. add checked-in JSON query packs under `src/Lokad.Sift.Benchmarks/Assets/`
3. add a real-corpus benchmark command that indexes a repo and runs the query pack
4. emit one JSON report containing build, snapshot, and per-query metrics
5. add a smoke test that validates query-pack loading and JSON report generation shape without requiring the benchmark corpora to be present

## 11. Non-goals

This benchmark track is not intended to:

- compare Sift to ripgrep, git grep, or Zoekt yet
- establish publishable headline numbers before the methodology is stable
- require network access or automatic cloning
- require the benchmark repositories to exist on every developer machine

The purpose is local realism, repeatability, and architectural guidance.

Managed-heap peaks for builds and compactions are sampled by this harness around
those operations. The core library has no managed-memory profiling option or
peak-memory statistics; benchmark report `PeakManagedBytes` fields remain harness
measurements, with the limits of sampling and process-wide heap accounting.
