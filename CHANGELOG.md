# Changelog

## Unreleased

### Changed

- Removed unused internal helpers/defaults, reserved sidecar scaffolding, and
  redundant search validation. Index format versions and read compatibility are
  unchanged; overlay inspection helpers are confined to verification builds.
- Simplified regex preparation while retaining lazy managed fallback and compiled
  handle leases. Managed regex cache counters now count actual compilation-cache
  lookups; native cache accesses are counted once per logical search, including
  overlays. Reusing a prepared verifier no longer synthesizes managed cache hits.
- Clarified that planner candidate estimates are heuristic rather than upper
  bounds; retained the existing scoring behavior after a rarest-gram comparison.
- Removed unused planner/reclamation result fields, regex analysis state, and
  internal convenience helpers. Search costs, regex greediness, snapshot leases,
  and reclamation durability are preserved; public API signatures are unchanged.
- Reduced postings generation to one sizing pass followed by encoding, using the
  written byte count for lexicon entries. Index format versions and bytes remain
  unchanged, including version 3 read compatibility.
- Removed unreachable postings intersection branches and redundant planner
  forwarding methods. The existing SIMD hardware fallback, planner decisions,
  public diagnostics and index compatibility are preserved.
- Removed the unreachable conversion from sparse intersections to dense bitsets;
  initial density routing and candidate results are unchanged.
- Removed unsafe advisory-seed candidate pruning and its seed-selection plumbing.
  Advisory seeds remain available in plan diagnostics; fallback patterns without
  a proven indexable constraint scan their scope and may examine more candidates.

### Fixed

- N-gram budgets no longer omit regex alternatives. An indexed union is used only
  when every branch is covered; insufficient budgets fall back to a safe plan.
- Fallback regex matching no longer loses branches when advisory seeds are capped,
  short, or dense.
- Case-sensitive ASCII literal and managed regex scans now observe caller
  cancellation and search deadlines between matches within one document.
- Regex nesting limits now respect comments, scoped whitespace options, control
  escapes and character classes, preventing bypasses and false rejections for
  both content and path regexes.
- Path regex fragment extraction now gives up conservatively on character-class
  subtraction, preventing nested class delimiters from excluding valid matches.

## 0.3.0 - 2026-10-19

### Added

- Added optional host-supplied UTF-8 regex engines through `SiftOptions.RegexEngine`
  and Sift-owned `ISiftRegexEngine`, `ISiftCompiledRegex`, and match-sink contracts.
  Application-side Utf8Regex adapter source needs no additional adapter library.
- Added `SearchPlan.RegexRouteReason` and CLI/benchmark diagnostics that distinguish
  managed fallback, unsupported capabilities, native verification, and input domains.
- Added adapter ownership, coalesced compilation, cancellation/deadline, eviction,
  timeout, byte-range, producer integration, and differential regression coverage.
- Added Windows/Ubuntu CI with locked restore, Release tests, isolated Release
  package consumers, and verification logs.

### Changed

- Removed the core package's `Lokad.Utf8Regex` dependency. The shipped core has no
  runtime NuGet dependencies. Regex verification defaults to .NET Regex, preferring
  `NonBacktracking` when supported; optional byte-native verification requires
  explicit host configuration. Tests and benchmarks retain the real producer.
- Shared bounded compiled-pattern caches across an index's snapshots and overlay
  generations. Active query leases protect handles until their users finish.
- New segment writes use version 4 without the unused byte-frequency histogram
  or its counting pass. Version 3 packs remain readable, including the older stats
  layout without non-ASCII totals. Obsolete bytes are ignored while retained
  statistics, bounds, and checksums remain validated. Updates may mix versions;
  normal compaction writes version 4. No index rebuild or migration is required.
- Removed orphan generation-lease probing and redundant regex helper entry points.
  Necessary cache/storage inspection is confined to `SIFT_INTERNAL_VERIFICATION`.
  Managed-heap sampling remains in the benchmark harness.

### Fixed

- Preserved sound byte prechecks during managed verification and prevented advisory
  branch selection from dropping dense alternatives when byte analysis exists.
- Preserved independent caller cancellation and deadlines during coalesced regex
  compilation, without cancelling or evicting a healthy caller's work.
- Prevented regex-provider failures from being reported as index corruption and
  preserved timeout policies, result limits, and byte-range validation.
- Protected snapshot lifetimes during host compilation and rejected reentrant
  disposal from search/explanation callbacks, including overlays.
- Removed checkout-name and Windows-path assumptions from smoke/package checks.

### Security

- Updated private SourceLink build tooling to 10.0.402, replacing vulnerable
  `Microsoft.Build.Tasks.Git` 8.0.0 (CVE-2026-62900). Its dependencies remain private.

### Breaking changes

- Removed `SiftOptions.MeasurePeakManagedMemory`, `BuildStats.PeakManagedBytes`,
  and `CompactStats.PeakManagedBytes`, including the library's background sampler.
  Consumers should measure memory outside the library.
- Applications requiring byte-native regex verification must now configure an
  engine explicitly and reference their chosen regex producer directly.

## 0.2.0 - 2026-08-07

### Added

- Added `ISiftIndex.ShouldCompact(CompactOptions)` as the authoritative hint for
  actionable compaction work, avoiding false recommendations based on aggregate
  segment count alone.
- Made compaction hints account for replacement eligibility and quota capacity,
  including `CompactStats.HasMoreWork` after a retained snapshot defers cleanup.
- Exposed total corpus usage, quota headroom, and exceptional reclamation
  failures through `StorageStats`.
- Added asynchronous full-index validation with progress and readiness telemetry
  so applications can verify checksums before serving queries.
- Added `DocumentContentLifetime.UntilOperationCompletes` so sources with stable
  buffers can avoid an intermediate body copy during build and update.
- Added pull-based `IAsyncEnumerable<SubmittedDocument>` build and update APIs
  for naturally backpressured asynchronous ingestion.
- Added bounded opaque commit metadata, stable corpus/content/storage identity,
  and both logical-revision and exact-manifest update preconditions for consumer
  checkpoints committed with index mutations.
- Added snapshot-captured identity and commit metadata plus live-document
  enumeration without body materialization.
- Added complete rejected-document details and committed identities to build and
  non-failing update results.
- Added matching-line materialization so callers can build excerpts without a
  second document lookup or duplicate line scan.

### Changed

- Reduced allocation and segment-handle churn in broad, sequential, and
  parallel searches, including repeated hit materialization from one snapshot.
- Bounded parallel hit buffering and propagated collector cancellation to
  in-flight segment producers instead of finishing the entire batch first.
- Avoided rescanning lexicons and legacy document tables during repeated work on
  an already-open snapshot.
- Kept normal search and update opens to structural validation instead of
  synchronously checksumming every byte of each segment after process restart;
  `Validate()` remains the explicit full-integrity pass.
- Kept clean `ShouldCompact()` and storage-stat calls from sweeping retired
  generation leases or durably rewriting the unchanged reclamation marker.
- Reduced update, indexing, and compaction memory pressure by avoiding decoded
  path copies, streaming compacted documents, and moving fixed index workspaces
  off the managed heap.
- Deferred path encoding and line-index construction until after unchanged
  upserts have been removed from an update partition.
- Released pooled native indexing workspaces when the last `SiftIndex` instance is
  disposed and exposed their current footprint through process metrics.
- Reduced regex-search overhead by preparing verifiers once per query,
  coalescing concurrent cache creation, and mapping Unicode offsets
  incrementally.
- Made small updates choose adaptively between indexed path lookups and segment
  scans instead of scanning the entire corpus unconditionally.
- Bounded query and path-filter pattern preparation, included preparation in
  search elapsed limits, and capped regex caches by retained pattern size.
- Bounded aggregate path-filter characters and stopped generating compiled
  regex code and intermediate strings for one-shot path matchers.
- Rejected regexes deeper than the configured structural limit and lowered the
  default pattern limit to 2,048 characters.
- Increased the default post-mutation reserve to preserve enough space for one
  default compaction pass.
- Made compaction preserve logical content revisions and commit metadata while
  advancing only the physical storage generation.
- Reclaimed retired on-disk generations automatically while preserving files
  pinned by active snapshots and durable recovery intent after failed deletion.
- Bounded overlay segments, tombstones, query preparation caches, path-filter
  caches, segment parallelism, and retained diagnostic state.
- Serialized all asynchronous mutation kinds through one coordinator while
  keeping asynchronous document sources asynchronous end to end.
- Reused parsed manifests and mutation-scoped capacity accounting, and stopped
  reopening segments when checking byte-identical upserts.
- Made native indexing workspace retention and managed-heap peak sampling
  explicit opt-ins instead of permanent process overhead.
- Added end-to-end snapshot lifecycle and sustained overlay-churn benchmark
  scenarios alongside steady-state search measurements.
- Routed explicit Release packages to `artifacts/nuget` and rejected non-Release
  packaging unless deliberately overridden for troubleshooting.

### Fixed

- Made explicit validation and mutation/reclamation decisions reread the
  manifest so unchanged file timestamps and lengths cannot conceal on-disk
  corruption behind the normal read cache.
- Prevented oversized regex quantifiers and lone surrogate escapes from crashing
  Sift's advisory analyzers with implementation exceptions.
- Treated supplementary-plane Unicode letters and digits as single word
  characters for whole-word literal and regex boundaries.
- Failed rejected updates at the first invalid document and included its
  canonical path, rejection reason, and submitted size in the exception.
- Preserved invariant Unicode case-folding semantics when case-insensitive regex
  searches use ASCII n-gram indexes.
- Prevented rejected-only and ineffective deletion updates from publishing
  redundant manifest generations while preserving legacy-manifest migration.
- Allowed callers to atomically delete stale versions of rejected replacements
  and report every rejected document in one non-failing update.
- Avoided publishing replacement segments for byte-identical upserts and
  reported skipped documents and bytes in `UpdateStats`.
- Made regex planning account for Unicode fallback document and byte density,
  and report mixed UTF-8/.NET verification accurately through `Explain`.
- Prevented stale manifest and in-flight cache entries from regressing published
  state or duplicating concurrent value construction.
- Rejected reentrant mutation callbacks and incomplete overlay publication, and
  preserved snapshot-backed hit and content lifetimes across concurrent updates.
- Validated persisted manifest, segment, and postings structures before first use
  and recovered safely when older generations were retired concurrently.

### Breaking changes

- Rejected upserts now fail the whole batch by default. Set
  `RejectedUpsertPolicy.PreserveExisting` to retain the former counter-based
  behavior explicitly.
- Replaced optional method and constructor parameters with explicit overloads and
  mandatory full signatures.
- Reworked `SearchQuery`, `SearchOptions`, `PathFilter`, and
  `SubmittedDocument` as explicit-constructor contracts with init-only
  properties. Callers using the former positional-record named arguments must
  use the new constructors or object initializers.
- Changed `CompactOptions` from a value type to a reference type and moved
  partial configuration to object initializers.
- Renamed `CompactOptions.SegmentCountTrigger` to
  `MinimumSegmentCountForSmallMerge` to clarify that it only gates clean
  small-segment merging.
- Replaced generation-only index identity with corpus id, logical content
  revision, and physical storage generation.
- Replaced raw index and hit identity primitives with `CorpusId`,
  `ContentRevision`, `StorageGeneration`, `SegmentId`, and `DocumentOrdinal`.
- Removed nullable `SiftOptions` entry points; callers either omit the options
  through the dedicated overload or provide a non-null instance.
- Made `SearchQuery` a reference contract and made default `DocumentKey` and
  `SubmittedDocument` values fail explicitly when their required path is read.
- Made large search, mutation, maintenance, and storage result contracts
  reference types to avoid copying oversized value types.
- Moved convenience operation overloads to `SiftIndexExtensions`; interfaces
  now expose only the canonical cancellation- and options-aware operations.
- Internalized storage formats, mapped segments, manifests, and codec helpers;
  the supported package surface is the root `Lokad.Sift` integration contract.
- Removed friend-assembly access from the shipped library; internal tests and
  benchmarks compile the implementation sources into their own assemblies.
