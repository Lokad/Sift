# Application-side UTF-8 regex integration

Compile `Utf8RegexAdapter.cs` into the application that references both
`Lokad.Sift` and its chosen `Lokad.Utf8Regex` version. This is source glue rather
than a separate adapter package. The Sift library itself references neither the
producer nor these example sources.

Assign an adapter to `new SiftOptions { RegexEngine = adapter }` when creating a
disk or in-memory index. Keep the engine/configuration alive and stable until
associated snapshots finish. Sift owns accepted compiled patterns and safely
reuses them across snapshots. Inputs, sinks, and checkpoints stay borrowed and
synchronous; all returned whole-match ranges use UTF-8 byte offsets.

The adapter accepts an optional capability callback. Supply it only when a
supported producer API guarantees native execution with .NET semantics for the
pattern/options and returned input domain. Do not use reflection, sample-input
probes, or a hand-maintained pattern whitelist as production capability detection.
The pinned `0.1.0` public API has no native compile-time capability query. Without
such a guarantee, the adapter constructs the real producer to verify compilation
integration, then returns unsupported and Sift performs safe managed matching.
The callback is an integration seam for a qualified producer; its existence does
not qualify current Utf8Regex acceleration.

Tests compile this same source against their source-linked Sift contracts, retain
their direct producer dependency, and compare real producer results with .NET.
`tools/verify-regex-package.ps1` additionally checks injection against the actual
Release package and a clean core-only consumer, avoiding source-linked/binary
contract type confusion.
