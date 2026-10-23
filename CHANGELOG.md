# Changelog

## Unreleased

### Added

- `IIndexSnapshot.GetDocumentBytes(in RawHit)` reads a hit's complete document
  directly in built-in snapshots. Returned memory belongs to the snapshot and
  remains valid across overlay updates until snapshot disposal. Hit ownership,
  generation leases and existing custom snapshot implementations are preserved.
- `IIndexSnapshot.GetDocumentKey(in RawHit)` retrieves a hit's document key without
  materializing its match or context text in built-in snapshots. Hit ownership and
  snapshot/overlay lifetime checks are preserved. A default implementation keeps
  existing custom snapshot implementations compatible.

### Changed

- Managed verification uses the document's existing ASCII flag to size its decoded
  character buffer without a counting pass. Unicode decoding, buffer sizes and
  decoded-character statistics are unchanged.
- Snapshot document lookups traverse segment metadata without allocating an
  enumerator. Segment ordering, deletion visibility and snapshot lifetimes are
  unchanged.
- Literal searches that require Unicode decoding reuse the managed regex path's
  pooled character buffer instead of allocating a decoded string per document.
  Case folding, overlapping matches and boundary semantics are unchanged.
- UTF-16 match offsets use the runtime's UTF-8 byte counter on consecutive spans,
  retaining monotonic projection and split-surrogate boundary behavior.
- Well-formed case-sensitive Unicode literals use the existing UTF-8 byte verifier
  without decoding whole documents. Unicode case folding, word boundaries and
  lone-surrogate literals retain their existing verification semantics.
- Removed an unused prepared-text ASCII classification and private statistics/
  snapshot forwarding types. Public statistics and snapshot lifetimes are unchanged.
- Generation leases decode directly into their final typed ID array, removing an
  intermediate allocation and conversion pass. Lease semantics are unchanged.
- Stopped generating the unused ASCII-folded path trigram index and removed its
  dedicated counting/filling passes. Existing v3/v4 files remain readable; new
  v4 files retain empty compatible regions for older readers. Case-sensitive
  path indexes, case-insensitive content indexes and validation are preserved.
- Removed the optional ordered-anchor precheck and its separator analysis,
  character-class bitmaps, prepared metadata and filtering thresholds. Proven
  mandatory fragments and exhaustive leading literals still narrow candidates;
  ordering and separator semantics are enforced by exact regex verification.
  Some queries may send more candidates to the verifier.
- Removed the unused internal ingestion retention flag and its forwarding plumbing.
  Public `DocumentContentLifetime` copying/borrowing behavior is unchanged.
- Removed unused internal regex-verifier return values/result bookkeeping and
  four private extension forwarding helpers. Public overloads and search counters,
  cancellation, timeout and hit-limit behavior are unchanged.
- Removed unreachable Unicode precheck decoding, its optional decoded-text
  plumbing and unused fragment fields. Content/path planning now reuse the
  analyzer's canonical fragments without a second containment-pruning pass.
- Truly empty queries skip content regex analysis/compilation. Plan diagnostics
  reflect path listing without decoding or whole-document content verification;
  nonempty empty-group regex enumeration is unchanged.
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
- Removed the unused prepared-pattern byte copy and advisory seed wrapper/state.
  Postings-statistics inspection now resides entirely in the benchmark harness.
- Corrected documented direct-scan defaults and literal-verifier behavior.
- Removed redundant gram-trimming guards and the unreachable empty-literal
  document branch. Planning costs and n-gram budgets retain their existing policy.

### Fixed

- Index disposal finishes draining mutations and releasing owned resources before
  rethrowing failures from caller cancellation callbacks. Retained snapshots and
  active queries keep their compiled regex handles until their own lifetimes end.
- Full-pack validation now rejects ASCII flags and exact non-ASCII totals that
  disagree with document content, even when region checksums are valid. The oldest
  version-3 statistics layout retains its conservative Unicode estimate.
- Manifest readers validate the fixed versioned header and exact layout before
  file-sized allocation, rejecting unchecked trailing bytes. Valid manifest
  versions 2/3/4 and existing segment compatibility are preserved.
- Failures enumerating caller-owned deletion collections no longer mark healthy
  indexes as corrupt. Updates preserve the original exception, committed state,
  snapshot visibility and subsequent usability, including enumerator disposal.
- CLI filesystem reads enforce the document-size bound before whole-file loading.
  Oversized files, including those beyond managed-array capacity, are reported
  and remove stale indexed versions; byte totals retain observed file sizes.
- CLI traversal skips file/directory links and junctions, preventing aliases from
  reentering the corpus or forming traversal cycles. Linked input roots are rejected
  before opening storage.
- Disk manifest publication now retains the configured free-space reserve for
  builds, pack-producing updates/compaction and non-shrinking metadata commits.
  It accounts for leases and manifest replacement while retaining temporary-file
  quota checks and below-reserve cleanup paths.
- CLI context flags now print bounded, labelled context per hit and mark truncation.
  CLI output is UTF-8; search/explain reject unknown arguments and invalid numeric
  options before opening the corpus, with a diagnostic and exit code 1.
- Parallel search can execute pending segments on its caller when pool workers are
  unavailable. Ordered result-queue waits observe elapsed deadlines independently
  of timer callbacks; collectors, bounded buffering and worker cleanup are preserved.
- Failures accessing caller-owned document memory no longer mark healthy indexes
  as corrupt. Build, update and overlay paths preserve the original exception and
  committed state, including later reads of borrowed content.
- ASCII-insensitive literal scans and mandatory-fragment prechecks now observe
  elapsed deadlines even when thread-pool saturation delays timer callbacks.
  No-hit segment scans that overrun their deadline report `MaxElapsedTime`.
- CLI builds and updates exclude their corpus subtree from filesystem input and
  reject using the same input/corpus directory. Directory updates report rejected
  files and remove previously indexed versions that become non-indexable; the
  library's default `FailUpdate` policy is unchanged.
- Commit metadata length is validated before accessing or copying callback memory.
  Accepted metadata remains defensively copied, and retained metadata still obeys
  the configured limit when an update has no metadata provider.
- Regex candidate planning no longer joins following text onto a group's partial
  leading prefix, which could hide matches such as `fooXbarqux` for
  `(?:foo.*bar|baz)qux`. Extraction now stops after groups and may retain shorter
  prefixes; mandatory-fragment planning and exact verification remain available.
- Recursive path globs now require the slash immediately before their literal
  suffix. Earlier wildcard prefixes use general glob verification. Includes and
  excludes preserve strict whole-path matching and ordinal case sensitivity.
- Overlay `ApplyDeltaTo(...)` and `CommitToBase(...)` now reject calls from that
  overlay's active callbacks with `InvalidOperationException`, preventing
  deadlocks in source/metadata callbacks and lock-order cycles in collectors.
  Safe reads and unrelated concurrent callers remain usable; export atomicity
  and revision checks are unchanged.
- Regex candidate filtering no longer discards matches with Unicode separators
  such as `foo.bar` on `fooébar`, or overlapping literal occurrences such as
  `aa.*aa` on `aaaaa`. Managed/native verifier semantics are unchanged.
- Unicode whole-line literals now ignore at most one trailing CR before LF or EOF,
  matching the existing ASCII rule. Additional CRs remain part of the line body;
  regex line anchors and materialized display trimming are unchanged.
- `GetStorageStats()` and `ShouldCompact(...)` now reject calls from the same
  index's active mutation callbacks with `InvalidOperationException`, preventing
  writer-lock deadlocks. Safe reads and unrelated concurrent callers remain usable;
  storage consistency and compaction policy are unchanged.
- Path prefixes and include/exclude globs containing lone UTF-16 surrogates no
  longer lose matches or match replacement-character filenames through UTF-8
  conversion. Combined filters retain the same meaning across verification routes;
  unsafe index fragments are omitted while valid-Unicode acceleration is preserved.
- Document source and commit metadata callback failures during build/update no
  longer falsely mark healthy indexes as requiring a rebuild. Original exceptions,
  committed state and snapshots are preserved, including async enumeration failures;
  genuine corruption detection and failed-recovery handling remain in place.
- Collector `RegexMatchTimeoutException` failures now propagate unchanged from
  ordinary and parallel searches, including literal queries. Preparation timeout
  handling no longer swallows callback failures; genuine regex timeout policies
  and overlay behavior are preserved.
- Literal queries containing lone UTF-16 surrogates no longer produce partial
  UTF-8 hit ranges or lose matches to replacement-encoded index constraints.
  They retain UTF-16 matching with the existing scalar-boundary projection;
  overlapping literal matches and valid-Unicode index acceleration are preserved.
- Context clipping preserves embedded carriage returns at byte-budget boundaries.
  Display terminators are determined from the complete line; shared source-byte
  budgets, UTF-8 boundaries and full matching-line materialization are unchanged.
- `SearchPlan.NeedsUnicodeDecode` follows literal verifier routing and the physical
  path scope, including Unicode literals and whole-word checks. Literal-only
  `Multiline`/`DotAll` flags do not imply decoding; empty queries/scopes return false.
- Invalid capture names and malformed bare/stacked quantifiers no longer bypass
  .NET syntax validation through case-sensitive literal acceleration. Named
  capture forms use managed matching while retaining sound index hints; ordinary
  and escaped literals keep their supported fast paths.
- Include/exclude globs now match complete paths consistently: generated regexes
  reject unwanted trailing newlines and recursive wildcards can consume embedded
  newlines. User-supplied path regex semantics are unchanged.
- Regex content and path pruning discard fragments that split UTF-16 surrogate
  pairs, preventing missed supplementary-character alternatives and quantified
  matches. Raw lone-surrogate regexes use managed matching for correct UTF-8
  byte-boundary projection.
- Exact-literal regex verification now returns non-overlapping matches, including
  ASCII and Unicode literals; literal search retains overlapping occurrences.
- Empty-group regexes enumerate every .NET zero-length match. Whole-word and
  whole-line exact-literal regexes use managed verification to retain filtering
  and anchor behavior, including CRLF. The truly empty-query path shortcut is unchanged.
- Scoped whitespace regex options now use managed matching instead of unsafe
  literal promotion or mandatory-fragment filtering. Uppercase scoped ignore-case
  options retain ignore-case semantics.
- Character classes starting with a literal `]` no longer produce unsound
  mandatory fragments that omit content or path matches.
- N-gram budgets no longer omit regex alternatives. An indexed union is used only
  when every branch is covered; insufficient budgets fall back to a safe plan.
- Fallback regex matching no longer loses branches when advisory seeds are capped,
  short, or dense.
- Context materialization accepts large byte budgets, including `int.MaxValue`,
  without integer overflow. Shared context limits and UTF-8 boundaries are retained.
- Hit delivery checks elapsed time synchronously after callbacks so delayed
  cancellation timers cannot allow further hits past a search deadline.
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
