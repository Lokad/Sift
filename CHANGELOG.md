# Changelog

## 0.2.0 - Unreleased

### Added

- Added `ISift.ShouldCompact(CompactOptions)` as the authoritative hint for
  actionable compaction work, avoiding false recommendations based on aggregate
  segment count alone.

### Changed

- Reduced allocation and segment-handle churn in broad, sequential, and
  parallel searches, including repeated hit materialization from one snapshot.
- Reduced update, indexing, and compaction memory pressure by avoiding decoded
  path copies, streaming compacted documents, and pooling large index workspaces.

### Breaking changes

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
