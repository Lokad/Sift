# Changelog

## 0.2.0 - Unreleased

### Added

- Added `ISift.ShouldCompact(CompactOptions)` as the authoritative hint for
  actionable compaction work, avoiding false recommendations based on aggregate
  segment count alone.
- Made compaction hints account for replacement eligibility and quota capacity,
  including `CompactStats.HasMoreWork` after a retained snapshot defers cleanup.
- Exposed total corpus usage, quota headroom, and exceptional reclamation
  failures through `StorageStats`.
- Added asynchronous full-index validation with progress and readiness telemetry
  so applications can warm checksum verification before serving queries.
- Added `DocumentContentLifetime.UntilOperationCompletes` so sources with stable
  buffers can avoid an intermediate body copy during build and update.

### Changed

- Reduced allocation and segment-handle churn in broad, sequential, and
  parallel searches, including repeated hit materialization from one snapshot.
- Avoided rescanning lexicons and legacy document tables whenever a previously
  validated segment is mapped by a new snapshot.
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
- Released pooled native indexing workspaces when the last `Sift` instance is
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

### Fixed

- Prevented oversized regex quantifiers and lone surrogate escapes from crashing
  Sift's advisory analyzers with implementation exceptions.
- Treated supplementary-plane Unicode letters and digits as single word
  characters for whole-word literal boundaries.
- Failed rejected updates at the first invalid document and included its
  canonical path, rejection reason, and submitted size in the exception.
- Preserved invariant Unicode case-folding semantics when case-insensitive regex
  searches use ASCII n-gram indexes.
- Prevented rejected-only and ineffective deletion updates from publishing
  redundant manifest generations while preserving legacy-manifest migration.
- Avoided publishing replacement segments for byte-identical upserts and
  reported skipped documents and bytes in `UpdateStats`.
- Made regex planning account for Unicode fallback document and byte density,
  and report mixed UTF-8/.NET verification accurately through `Explain`.

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
  `SmallSegmentMergeThreshold` to clarify that it only gates clean small-segment
  merging.
- Removed nullable `SiftOptions` entry points; callers either omit the options
  through the dedicated overload or provide a non-null instance.
