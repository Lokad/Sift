# Lokad.Sift Specification - local indexed grep backend in pure C#/.NET

## 1. System goal

Build a **local-only**, **pure managed C#/.NET** backend for grep-like text/code search over corpora in the **10–50 million LOC** range.

The backend shall optimize for these workloads:

* fixed-string search
* regex search
* multiline regex search
* path/glob/path-regex filters
* low-allocation streaming of hits
* snapshot-consistent incremental updates

The backend is **not distributed**, **not remote-first**, and **not a general IR engine**. It is a local code-search storage/query engine.

The core Lokad.Sift library is filesystem-agnostic: it indexes caller-supplied document bytes plus logical path/name metadata; crawling directories is only an optional adapter layered above the core.

The core design principle is:

**Use immutable, memory-mapped index segments plus exact verification.**
The index is allowed to over-select candidates, but it must never miss a real match.

Sift is intended to remain **language agnostic** with respect to the indexed corpus. The planner, analyzer, and verifier may exploit structural properties that arise from the query itself, but they must not rely on heuristics or hard-coded plan families tied to any one programming language, framework, or codebase convention.

---

## 2. Hard constraints

1. **Target runtime**

   * `net10.0`
   * 64-bit only
   * Keep the implementation within portable .NET facilities when reasonable, but cross-platform acceptance is not a tracked goal of this spec

2. **Dependency policy**

   * No native dependencies
   * Core production code relies on the .NET runtime and base class libraries;
     the shipped package has no runtime NuGet dependencies
   * Optional regex engines implement Sift-owned contracts and are supplied by
     applications, which reference their chosen managed producer directly
   * Tests retain a direct pinned `Lokad.Utf8Regex` dependency and real-producer
     integration/differential coverage; it is not a core package dependency
   * Test and benchmark projects may use minimal purpose-specific dependencies such as `xUnit`
   * No SQLite/LMDB/Lucene
   * No external service
   * No query-time reads from the original submitted sources for indexed documents

3. **Correctness**

   * No false negatives for indexed, supported text documents
   * False positives are allowed only in the candidate stage; final verifier removes them
   * If the planner cannot prove an acceleration is sound, it must fall back to a less aggressive plan

4. **Canonical regex semantics**

   * Regex mode uses **`.NET regex semantics`**
   * “ripgrep-like” refers to query ergonomics and result model, not PCRE2 dialect parity

5. **Canonical corpus representation**

   * Indexed text is stored as **normalized UTF-8 text**
   * Supported source encodings: UTF-8 only
   * A UTF-8 BOM is tolerated on input and removed before storage
   * Preserve line endings as ingested

6. **Scope**

   * Local only
   * Single-machine
   * Single-writer, multi-reader

7. **Ingestion boundary**

   * The core library is **filesystem-agnostic**
   * The unit of ingestion is a **document**: logical name/path metadata plus source bytes
   * The core ingestion API accepts caller-supplied bytes (for example `ReadOnlyMemory<byte>`) rather than requiring filesystem paths
   * Filesystem enumeration is an optional adapter layered above the core indexing engine
   * Query-time behavior remains snapshot-based and does not depend on the original source location

---

## 3. Runtime and low-level rules

The query engine is built around **memory-mapped immutable segment files**. .NET memory-mapped files map file contents into the process address space, support extremely large files, and provide complete random access without seeking. Segment building and publication use `RandomAccess` for offset-based file reads/writes and `RandomAccess.FlushToDisk` before a manifest swap. Hot scanners use span-based runtime primitives and targeted unsafe pointer access where measurements justify it; temporary buffers come from `ArrayPool<T>`. Storage, indexing, and low-level implementation types remain internal to the single `Lokad.Sift` assembly. ([Microsoft Learn][1])

Regex execution shall be designed around a **byte-oriented UTF-8 compute path** for the supported fast subset. `.NET Regex` remains the semantic oracle and fallback authority for unsupported constructs, differential validation, and slow-path verification, but it is not the intended steady-state compute path for selective queries. When `.NET Regex` fallback is used, prefer `RegexOptions.NonBacktracking` whenever compatible because that mode guarantees linear-time processing. Patterns that require constructs such as lookarounds or backreferences must fall back to the classic engine, and all backtracking-mode executions must run with explicit timeouts. ([Microsoft Learn][2])

---

## 4. Canonical search semantics

### 4.1 Query model

A search query consists of:

* `Pattern`
* `PatternMode`: `Literal | Regex`
* `CaseMode`: `Sensitive | Insensitive | Smart`
* `WholeWord`
* `WholeLine`
* `Multiline`
* `DotAll`
* `MaxHits`
* `MaxMatchedDocuments`
* `MaxBytesScanned`
* `MaxHitsPerDocument`
* `MaxElapsedMilliseconds`
* `TimeoutPolicy`

A path filter consists of `PathPrefix`, `IncludeGlobs[]`, `ExcludeGlobs[]`, and
`PathRegex`.

### 4.2 Path semantics

* Every document has one canonical logical path
* Stored path = canonical logical path
* `Path` must be non-empty and relative, never absolute
* Canonical separator = `/`
* `\` is normalized to `/` on ingestion
* `.` and `..` path segments are forbidden
* repeated separators are forbidden
* path matching is ordinal and case-sensitive
* two submitted documents with the same canonical path identify the same logical document
* Document rows are sorted by stored path
* Prefix filters operate on canonical logical paths
* Path regex operates on canonical logical paths
* Globs are path-separator aware
* Include/exclude globs match the complete path, including a literal trailing LF
* `*`/`?` stay within a segment; `**` may consume separators and newlines
* `**/` retains its literal slash requirement (at least one directory separator)
* Generated glob regexes use strict whole-input anchors; user path regexes retain
  .NET regex anchor and newline semantics

### 4.3 Case semantics

* `Sensitive`: exact case
* `Insensitive`: full requested case-insensitive semantics in the verifier
* `Smart`: mimic ripgrep smart-case; treat the query as case-insensitive unless the pattern contains at least one uppercase letter, in which case treat it as case-sensitive

### 4.4 Search semantics

* Literal mode is exact substring search, including overlapping occurrences
* Regex mode follows `.NET regex` semantics, including non-overlapping enumeration
  and progression after zero-length matches
* A truly empty query lists one hit per document without scanning content or
  preparing a content regex, regardless of case or boundary flags;
  nonempty empty-group regex syntax still enumerates .NET's zero-length matches
* `WholeWord` and `WholeLine` are verifier semantics, not index semantics
* `Multiline` applies to verification against the entire document content
* The index is document-granular; the verifier is line/match granular

### 4.5 Correctness contract

* The index stage may only use conditions that are **mandatory for all true matches**
* Any uncertain optimization is forbidden
* Unsupported accelerations degrade to:

  * weaker candidate generation, or
  * direct scan within the current scope

---

## 5. Physical layout

Corpus root:

```text
<corpus>/
  manifest.bin
  segments/
    <segment-id>/
      segment.pack
  temp/
  lock/
    generations/
      <generation-id>.lease
    reclamation.state
```

### 5.1 Manifest

`manifest.bin` contains:

* format version
* corpus id
* logical content revision
* physical storage generation id
* bounded opaque consumer commit metadata
* root table
* active segment table
* for each active segment:

  * segment id
  * document count
  * content bytes
  * dead/superseded document ordinals (compressed)
  * checksum / integrity metadata

The manifest is the **only mutable authoritative file** in steady state.

### 5.2 Segment model

Each segment is immutable and independently queryable.

Segment size target:

* **64–128 MiB normalized text**, or
* **~50k documents**, whichever comes first

Reason: bounded mmap footprint, bounded posting-list sizes, parallel query execution, cheap compaction.

---

## 6. `segment.pack` format

All integers are little-endian.

### 6.1 Segment header

Header fields:

* magic
* format version
* segment id
* build timestamp
* document count
* content bytes
* region count
* region directory
* checksum table

The current segment format version is **4**. Its `Stats` region is exactly 20
bytes: document count (`i32`), content byte count (`i32`), non-ASCII document count
(`i32`), and non-ASCII content byte count (`i64`), all little-endian. Non-ASCII
statistics count whole documents whose content has non-ASCII bytes. Search uses
these counters for conservative Unicode planning. No byte-frequency histogram
is stored or computed.

Readers also accept segment version **3**, whose stats region is either 1032 or
1044 bytes. Both begin with the same document/content totals; bytes 8 through
1031 are obsolete and ignored. The 1044-byte form stores non-ASCII totals at
byte offsets 1032 (`i32`) and 1036 (`i64`). The 1032-byte form has no such totals
and conservatively estimates all documents/content as non-ASCII, as the former
reader did. Both retain region bounds, checksums, and applicable total validation;
no histogram is reconstructed or semantically validated.

Version/layout combinations must match exactly. Other segment versions are
rejected through the existing `RequiresRebuild` mechanism. Reading or validating
version 3 does not rewrite files. Updates may publish version-4 packs alongside
version-3 packs; normal compaction writes version 4. No migration layer or rebuild
is required for these supported older packs. Manifest version 4 and existing
manifest compatibility are unchanged.

### 6.2 Regions

Required regions:

1. `DocumentTable`
2. `PathBlob`
3. `ContentBlob`
4. `LineIndexBlob`
5. `ContentTriCsLex`
6. `ContentTriCsPost`
7. `ContentTriIcLex`
8. `ContentTriIcPost`
9. `ContentBiCsLex`
10. `ContentBiCsPost`
11. `ContentBiIcLex`
12. `ContentBiIcPost`
13. `PathTriCsLex`
14. `PathTriCsPost`
15. `PathTriIcLex`
16. `PathTriIcPost`
17. `Stats`

The reader requires exactly these 17 regions. Other region IDs are rejected;
future extensions require an explicit format policy when implemented.

### 6.3 Document table

One fixed-size row per document, segment-local ordinal = row index.

Required fields:

* `PathOffset : u64`
* `PathLength : u32`
* `ContentOffset : u64`
* `ContentLength : u32`
* `LineIndexOffset : u64`
* `LineIndexLength : u32`
* `LineCount : u32`
* `Flags : u16`
* `MTimeUtcTicks : i64`
* `ContentHash64 : u64`

`MTimeUtcTicks` stores UTC ticks when the source provides `LastModifiedUtc`; otherwise it stores `0` as the sentinel for “unknown”.

`Flags` must include at least:

* `Text`
* `Ascii`
* `HasBom`

### 6.4 Path blob

* UTF-8
* canonical `/` separators
* canonical logical path
* preserve original case
* no global string interning requirement

### 6.5 Content blob

* concatenation of normalized UTF-8 document texts
* preserve line endings
* no compression
* no query-time dependency on original submitted sources

This is a deliberate design choice: query-time access is fully snapshot-based.

### 6.6 Line index blob

Per document, store newline-derived line starts using:

* anchor every `128` lines
* absolute byte offset for anchor lines
* delta-encoded line-start offsets between anchors

Line index rules:

* line 1 starts at byte `0`
* a new line starts after every `\n`
* `\r` before `\n` belongs to the previous line
* missing terminal newline is allowed

### 6.7 Content n-gram indexes

Use **document-level** n-gram postings, not block-level postings.

Base indexes:

* content trigrams, case-sensitive
* content trigrams, ASCII-folded case-insensitive
* content bigrams, case-sensitive
* content bigrams, ASCII-folded case-insensitive

No unigram index.

Trigram key space:

* index key = 3 bytes over normalized UTF-8 content
* partition by first byte into `256` buckets
* within a bucket, lexicon is sorted by 16-bit suffix

Lexicon entry:

* `Suffix : u16`
* `PostingsOffset : u32`
* `PostingsBytes : u32`
* `DocFreq : u32`

Postings payload:

* ascending segment-local document ordinals
* delta-encoded
* compressed with a **branch-light document-ordinal codec** suitable for low-allocation mmap-backed access
* for large lists (`DocFreq >= 4096`), include skip checkpoints every `256` ordinals

Guidance:

* the exact codec may evolve, but it must preserve strong query-time locality and low branch cost
* for large postings, prefer traversal strategies that keep branch cost low and exploit blockwise locality
* **SIMD block-merge** is an acceptable optimization for sufficiently large decoded ordinal arrays, but the spec does not require any one fixed large-postings traversal design

Bigram lexicon is identical except suffix width is one byte.

### 6.8 Path trigram indexes

Provide:

* path trigrams, case-sensitive
* path trigrams, ASCII-folded case-insensitive

No path bigram index is required.

Rationale: prefix/path-range filters come from sort order; arbitrary path substring/regex gets path trigrams.

### 6.9 Locality rule

Hot query-time structures shall be designed for cache locality and low branch cost.

Guidelines:

* prefer contiguous scans over pointer-heavy structures
* prefer layouts that minimize random memory access during postings intersection
* prefer simple branch-light codecs over denser but slower codecs on hot paths
* compression is beneficial only when it reduces total query cost, not merely storage size

---

## 7. Build and update pipeline

### 7.1 Document submission

The builder receives submitted documents and computes for every document:

* logical path
* content byte length
* mtime

Documents rejected by ingestion policy are excluded from content indexing.

Default rejection policy:

* payloads classified as binary
* non-UTF-8 payloads
* oversized text documents over configurable threshold, default `32 MiB`

### 7.2 Text detection and normalization

Accepted text encoding:

1. optionally strip a UTF-8 BOM if present
2. validate the remaining payload as UTF-8
3. otherwise treat the payload as binary / unsupported for text indexing

Normalized stored representation:

* UTF-8
* UTF-8 BOM removed if present on input
* line endings preserved

### 7.3 Segment assignment

After submission, documents are assigned to segments by:

* lexicographic logical path
* respecting target segment size / document-count thresholds

### 7.4 Per-document ingestion

For each submitted text document:

1. read source bytes
2. decode and normalize to UTF-8
3. append to `ContentBlob`
4. build line index
5. compute `ContentHash64`
6. extract unique content bigrams/trigrams
7. extract unique path trigrams
8. emit `(bucket, suffix, documentOrdinal)` tuples into temporary bucket streams

Required dedup rule:

* n-grams are unique **per document** before final postings generation

### 7.5 Final postings generation

For each temporary bucket stream:

1. sort by `(gramSuffix, documentOrdinal)`
2. unique duplicates
3. emit postings list
4. emit lexicon entry with `DocFreq`, `PostingsOffset`, `PostingsBytes`

The in-memory postings builder sizes each list once for the combined allocation,
then encodes into the remaining destination capacity. Encoding returns the byte
count for the lexicon entry; it does not repeat the sizing traversal. The final
written length must match the allocation. Plain and checkpointed wire layouts
remain unchanged.

### 7.6 Publication

Publication must be crash-safe:

1. write `segment.pack` under `temp`
2. flush segment file
3. validate checksum
4. move into `segments/<segment-id>/`
5. write and flush the immutable, checksummed generation lease
6. write new manifest to temp file
7. flush manifest
8. atomic manifest swap
9. delete packs unreachable from both the new manifest and live generation leases

Startup performs the same unreachable-pack sweep so a crash after segment
publication but before manifest publication cannot leak a complete orphan pack.
If the manifest or an active lease cannot be validated, reclamation fails closed.

### 7.7 Incremental update

Incremental updates produce **new segments only**.

Updated document:

* old document ordinal becomes dead in manifest
* new version is written into a new segment
* if the replacement is rejected by ingestion policy, the old ordinal remains live
  or is deleted according to the operation's explicit rejection policy

Deleted document:

* old document ordinal becomes dead in manifest
* no tombstone segment row is required

Update batch rules:

* each canonical path may appear at most once in `upserts`
* each canonical path may appear at most once in `deletions`
* a path must not appear in both `upserts` and `deletions` within the same update batch
* violating these rules is an error
* update application is atomic at the batch level
* optional consumer metadata is copied into the same manifest publication
* an optimistic update expectation compares corpus id and logical content
  revision; content-preserving compaction does not invalidate it

### 7.8 Compaction

Compaction merges active live documents from selected segments into new segments.

`ShouldCompact(options)` exposes the authoritative hint for the current
generation. With the default policy, compaction is actionable when any is true:

* enough mergeable small segments exist after segment count exceeds `16`
* dead fraction in any segment > `20%`
* manifest dead-ordinal metadata becomes materially large

Aggregate segment count is not a trigger by itself: a corpus may legitimately
need more than 16 target-sized segments. The hint and `Compact(options)` use the
same segment-selection policy. Because another mutation can publish between the
two calls, callers must tolerate a subsequent no-op compaction.

Compaction must not block queries.
Compaction owns reclamation of the segments it replaces. A replaced pack is
deleted automatically after the last generation lease that names it is released;
callers do not run a separate cleanup phase.
To bound physical growth, another replacement build or compaction is refused
while a previously retired generation remains leased. Compaction requires
temporary headroom for its output before reclamation can occur. Each pass limits
selected physical pack bytes through `CompactOptions.MaxInputSegmentBytes`; one
debt-bearing segment may exceed the limit so that an oversized segment does not
become permanently ineligible. Segment-count compaction requires at least two
clean inputs whose documents and content fit within one target output.

---

## 8. Query planning

Query planning is performed **per manifest snapshot**, then **per segment**.

### 8.1 Snapshot rules

* open manifest snapshot
* map all active segments in that snapshot
* snapshot is immutable for the lifetime of the query
* no query observes partial publication

### 8.2 Scope construction

For each segment, build the initial live scope as:

`LiveSet(segment)`

Then intersect, in this order:

1. prefix path ranges
2. include/exclude globs
3. path regex / path substring

Guidelines:

* path prefix filters use binary search over sorted path rows
* path substring/regex should use path trigram index if literal extraction succeeds; otherwise verify paths directly over current scope

If a query has no content term, the backend returns document-level path matches without touching `ContentBlob`.

### 8.2.1 Path-first narrowing

Path constraints are first-class performance primitives.

The planner should exploit path filters as early as possible because path narrowing can reduce both postings work and verification cost materially.

### 8.3 Pattern analysis

The planner must analyze the content pattern into:

* `PatternKind`
* `CaseModeResolved`
* `RequiredFragments`
* `SelectedNgrams`
* `CanUseLiteralVerifier`
* `CanUseUtf8RegexVerifier`
* `CanUseNonBacktracking`
* `NeedsUnicodeDecode`
* `MayRequireWholeDocumentVerification`

#### 8.3.1 Required-fragment extraction

Implement a conservative regex analyzer.

For every parsed node, compute a capped set of **mandatory literal fragments**.

The analyzer publishes deduplicated fragments with contained fragments removed.
Content and path planning reuse this canonical result without a second pruning pass.

Transfer rules:

* **literal node**: fragment = literal text
* **concatenation**: union child mandatory fragments; synthesize cross-boundary fragments where both sides are non-optional
* **alternation**: intersection of mandatory fragments across branches
* **quantifier with min >= 1**: child fragments survive
* **optional quantifier (`?`, `*`, `{0,n}`)**: child fragments are dropped
* **singleton character class**: treat as literal
* **anchors/groups**: transparent
* **lookaround/backreference/conditional/balancing/unknown inline-scope constructs**: mark analyzer-limited and stop deriving through that node

Analyzer limits are acceptable. Unsound extraction is forbidden.

Analysis follows .NET's UTF-16 code-unit semantics, including quantifiers.
Only well-formed Unicode fragments may become UTF-8 byte constraints. Shared
prefixes/suffixes and quantified atoms can split surrogate pairs; discard unsafe
deductions before content or path pruning rather than replacement-encoding them.
An unsafe deduction can reduce acceleration, never the set of returned matches.

Scoped whitespace options (`x`/`X`) and character classes beginning with a literal
`]` conservatively disable fallback analysis. These patterns still use .NET
matching; analysis must not turn their syntax into literal text or mandatory
fragments. Scoped ignore-case options are recognized in either letter case and
cannot establish case-sensitive mandatory fragments or an exact-literal route.

Named-group header scanning is structural, not a .NET capture-name validator.
Named capture bodies may contribute mandatory fragments, but cannot establish an
exact-literal route: managed preparation validates their syntax before path/content
filtering. Unconsumed quantifier/braces tokens also disable fallback deductions;
.NET distinguishes malformed syntax from valid literal braces. Escaped literals
and the validated simple syntax subset retain literal acceleration.

Language-specific parsing or language-specific semantic recognition is out of scope for the planner. Pattern analysis must remain query-driven and corpus-language agnostic; it may classify regex structure, literals, alternations, anchors, and repetition, but it must not depend on hard-coded knowledge such as C# declarations, Go receivers, Java annotations, or framework-specific API names.

#### 8.3.2 Encoding rule

For case-sensitive planning, mandatory fragments are encoded to normalized UTF-8 and then converted to bigrams/trigrams.

For case-insensitive planning:

* ASCII-only mandatory fragments may use ASCII-folded n-gram indexes
* non-ASCII case-insensitive fragments do **not** use folded n-gram acceleration unless proven sound
* fallback is direct scan within scope or weaker ASCII-only acceleration

### 8.4 Plan selection

Plan selection must be cost-aware.

Default planning limits:

* minimum indexed fragment length: 2 bytes (internal constant)
* `MaxSelectedNgrams = 24`
* `DirectScanScopeBytesThreshold = 1 MiB`
* `DirectScanScopeDocumentsThreshold = 256`

Plan choice:

1. If current scope is already small, direct scan the scope
2. Else if required fragments produce trigrams, use trigram index
3. Else if they produce only bigrams, use bigram index
4. Else direct scan the scope

The planner shall use available statistics, including at least:

* scoped document count
* scoped byte count
* n-gram `DocFreq`
* estimated candidate density
* estimated verification cost
* estimated decode requirement

The goal is an explicit planner architecture with explicit candidate enumeration and centralized scoring based on measured heuristics. It is not a requirement to build a globally predictive optimizer-style cost model.

The current selectivity estimate multiplies n-gram frequencies under an
independence assumption. Correlated grams can make it underestimate cardinality;
it is a scoring heuristic, not an upper bound or a correctness filter. Exact
candidate generation and verification remain authoritative.

A plan that produces materially fewer candidates is preferred even if its index-side work is slightly higher.
A plan that is expected to trigger substantial UTF-16 decoding is discouraged unless it materially reduces total work.

The planner may introduce explicit structural plan families, but those families must be defined in a language-agnostic way. Acceptable examples are plan families based on query shape such as literal union, anchored concatenation, bounded alternation, path-first narrowing, or future positional constraints. Unacceptable examples are plan families that are intrinsically tied to one programming language or framework, such as `CSharpDeclarationPlan`, `AspNetRegistrationPlan`, or similar corpus-specific heuristics.

N-gram selection rule:

* for intersections, select useful grams from mandatory fragments; omitting a
  conjunctive constraint only broadens candidates
* for unions, cover every alternative, including in a union of intersections;
  if the budget cannot cover them all, decline that candidate and use a safe plan
* then add additional n-grams by increasing `DocFreq` until:

  * estimated candidate set is small enough, or
  * `MaxSelectedNgrams` is reached

Planning budgets must not change the match set. Advisory seeds are lossy
diagnostics, not exhaustive branch coverage, and must not exclude candidates.
Fallback patterns without a proven indexable constraint scan their scope.

### 8.5 Candidate generation

Candidate generation is segment-local.

Adaptive representation:

* if current candidate density is high, use pooled dense bitset
* otherwise use pooled sorted ordinal arrays

Intersection algorithm:

* order posting lists by increasing `DocFreq`
* for list-size ratios above `8x`, use galloping intersection
* otherwise use merge-based intersection, with blockwise and/or SIMD variants when they improve the measured hot path
* when dense mode becomes cheaper, switch to bitset AND

Missing n-gram in any selected mandatory fragment => empty result for that segment.

### 8.6 Candidate precheck

Before full verification, candidate documents must pass a **mandatory-fragment precheck**.

Precheck implementation:

* fixed-string queries: direct literal verifier only
* regex queries: check proven mandatory fragments in UTF-8 before invoking the selected regex verifier

Insensitive byte prechecks require ASCII byte analysis. Patterns without that
analysis have no insensitive byte anchors, and non-ASCII documents requiring
Unicode folding bypass these prechecks. Prechecking never decodes a document;
the selected verifier handles Unicode semantics.

This is critical: the index enforces presence of selected grams, not full fragment ordering or multiplicity.

---

## 9. Verification engines

### 9.1 Literal verifier

Use a custom byte-oriented literal verifier.

Fast path:

* operate directly on normalized UTF-8 content bytes
* case-sensitive ASCII literals use span-based substring search
* ASCII-insensitive literals use folded bytes and a prepared prefix table
* check whole-word and whole-line constraints on each candidate match

Case-insensitive literal mode:

* ASCII-only fast path uses branch-light ASCII folding
* otherwise decode and use Unicode-aware verifier

Whole-word literal mode:

* ASCII path: byte-oriented boundary checks
* Unicode path: decoded-text boundary checks

Whole-line literal mode:

* compare match bounds with the surrounding line bounds, excluding a trailing CR

### 9.2 Regex verifier

The exact-literal shortcut applies to nonempty case-sensitive, well-formed Unicode
literals without `WholeWord`/`WholeLine`. It advances past the entire accepted
match. Empty groups, boundary-constrained exact-literal forms and lone-surrogate
regexes use managed verification, preserving regex enumeration, word filtering,
UTF-8 boundary projection and anchor semantics (including CRLF behavior).
Named capture forms also use managed preparation/verification, retaining sound
mandatory-fragment indexing. Invalid syntax must fail independently of case mode,
corpus contents, overlay visibility or an empty path scope.
Truly empty queries use the path-listing shortcut; their plans require neither
Unicode decoding nor whole-document content verification.

The preferred exact verifier is a **custom byte-oriented UTF-8 regex engine** for the supported fast subset. `.NET Regex` remains the semantic oracle and fallback verifier for constructs outside the supported subset.

The byte verifier is supplied through `SiftOptions.RegexEngine` and Sift's
`ISiftRegexEngine`/`ISiftCompiledRegex` contract. Without an engine, .NET Regex
implements regex search, including path filtering. A compiled handle declares its
input domain and guarantees .NET semantics with byte-oriented execution; an
internal producer fallback must not be reported as a native verifier. Initially
native execution is restricted to ASCII documents and Sift's conservative syntax
subset. The contract admits future qualified Unicode input domains. Pattern
analysis and candidate soundness remain under Sift's control, including byte
prechecks for managed verification where sound and Unicode supplementation.

Bound plans and compiled handles use a bounded index-owned execution context
shared by snapshots and generations, with fixed engine binding/configuration.
Snapshot turnover preserves cached compilation. Active query leases protect
their own handles from eviction/disposal; snapshots can outlive the originating
index. The application owns the engine; Sift owns compiled handles and releases
them after the last applicable lease. Only backend-independent analysis and
fully keyed BCL regex state may be cached process-wide.

Sift verifies its adapter boundary independently of producer internals. Native
execution must meet the declared adapter contract; a producer that cannot give
that guarantee declines matching and the managed fallback remains available.
Producer-internal engine selection and backend acceleration qualification are
outside Sift's delivery scope. Timeouts, cancellation,
invalid provider ranges, and unexpected failures do not request fallback after
partial hit publication. Preparation observes the query deadline and timeout policy.

Literal and managed regex document scans check the effective cancellation token
between matches, including matches rejected by boundary checks. Caller cancellation
throws with the caller's token; expiry of the search deadline returns
`MaxElapsedTime`. An in-progress BCL match uses the existing regex safety timeout;
cooperative checkpoints run before and after each match step. Regex nesting limits
count structural parentheses while respecting comments, inline whitespace-option
scopes, escapes and character-class boundaries. Syntax validation still belongs
to the verifier. Unsupported class subtraction is excluded from fallback fragment
extraction so that path acceleration cannot manufacture mandatory delimiters.

Rules:

* the planner must classify patterns into:
  * byte-regex fast path
  * byte-regex whole-document path
  * fallback/oracle path
* the byte-regex engine operates directly on normalized UTF-8 content bytes and reports byte offsets natively
* `.NET Regex` fallback remains available for unsupported constructs and semantic validation
* fallback regex instances are cached by normalized `(pattern, options)`
* fallback uses `NonBacktracking` if compatible
* otherwise fallback uses backtracking with explicit timeout
* fallback runs over the **entire document text** in multiline mode
* fallback runs over the **entire document text** in single-line mode as well; result projection remains line-oriented

Only sound mandatory-fragment constraints may eliminate candidates before verification.

### 9.3 Decode policy

Do not globally cache decoded document strings.

UTF-16 decoding is a last-resort operation.

Per candidate document:

* if the selected byte-oriented verifier is sufficient, do not decode
* decode within the literal verifier when Unicode literal/boundary semantics require it
* managed regex verification decodes into a pooled character buffer at verification time

Rules:

* candidate elimination should happen on UTF-8 bytes whenever sound
* run sound mandatory-fragment byte prechecks before regex execution
* decoded text must not be materialized unless required by the selected verifier path
* materialization of display/context text must happen only for accepted hits

`SearchPlan.NeedsUnicodeDecode` describes possible verifier decoding for the
physical path scope, before content candidate pruning, overlay visibility and
search limits. It shares the literal verifier's byte/Unicode routing decision
and scoped non-ASCII statistics. Empty queries/scopes return false. It excludes
display/context materialization; actual verification work is reported by
`SearchStats.DecodedChars`.

### 9.4 Materialization

Search must expose both:

* **raw hits**: document ordinal + byte offsets + optional char offsets
* **materialized hits**: path, line/column, context lines, display text

Raw hit fields must include at least:

* `SegmentId`
* `DocumentOrdinal`
* `StartByte`
* `EndByte`
* `StartLine`
* `EndLine`

Materialized hit fields must include at least:

* path
* start line / column
* end line / column
* matching text
* before/after context lines

Columns in materialized hits are UTF-16 code-unit columns.

Context byte budgets count source bytes, including line endings, and are shared
across before/after context. Display terminators are identified from the complete
line before clipping its display prefix, so an embedded CR at the clipping
boundary is preserved. UTF-8 scalar boundaries and truncation flags are retained.
Matching-line materialization still returns the complete display line.

---

## 10. Public API shape

Provide an in-process, library-first, filesystem-agnostic API.

The supported consumer surface is the root `Lokad.Sift` namespace. Storage
formats, mapped segments, manifests, and codecs are implementation details.

```csharp
public enum PatternMode { Literal, Regex }
public enum CaseMode { Sensitive, Insensitive, Smart }
public enum TimeoutPolicy { StopSearch, Throw }
public enum DocumentContentLifetime { UntilNextRead, UntilOperationCompletes }

public sealed record SearchQuery
{
    public SearchQuery(string pattern)
        : this(pattern, PatternMode.Regex, CaseMode.Smart) { }

    public SearchQuery(string pattern, PatternMode patternMode, CaseMode caseMode)
        : this(
            pattern,
            patternMode,
            caseMode,
            wholeWord: false,
            wholeLine: false,
            multiline: false,
            dotAll: false,
            maxHits: 10_000,
            maxMatchedDocuments: int.MaxValue,
            maxBytesScanned: long.MaxValue,
            timeoutPolicy: TimeoutPolicy.StopSearch,
            maxHitsPerDocument: int.MaxValue,
            maxElapsedMilliseconds: 30_000) { }

    public SearchQuery(
        string pattern,
        PatternMode patternMode,
        CaseMode caseMode,
        bool wholeWord,
        bool wholeLine,
        bool multiline,
        bool dotAll,
        int maxHits,
        int maxMatchedDocuments,
        long maxBytesScanned,
        TimeoutPolicy timeoutPolicy,
        int maxHitsPerDocument,
        long maxElapsedMilliseconds)
    {
        Pattern = pattern;
        PatternMode = patternMode;
        CaseMode = caseMode;
        WholeWord = wholeWord;
        WholeLine = wholeLine;
        Multiline = multiline;
        DotAll = dotAll;
        MaxHits = maxHits;
        MaxMatchedDocuments = maxMatchedDocuments;
        MaxBytesScanned = maxBytesScanned;
        TimeoutPolicy = timeoutPolicy;
        MaxHitsPerDocument = maxHitsPerDocument;
        MaxElapsedMilliseconds = maxElapsedMilliseconds;
    }

    public string Pattern { get; }
    public PatternMode PatternMode { get; init; }
    public CaseMode CaseMode { get; init; }
    public bool WholeWord { get; init; }
    public bool WholeLine { get; init; }
    public bool Multiline { get; init; }
    public bool DotAll { get; init; }
    public int MaxHits { get; init; }
    public int MaxMatchedDocuments { get; init; }
    public long MaxBytesScanned { get; init; }
    public TimeoutPolicy TimeoutPolicy { get; init; }
    public int MaxHitsPerDocument { get; init; }
    public long MaxElapsedMilliseconds { get; init; }
}

public sealed class PathFilter
{
    public PathFilter()
        : this(pathPrefix: null, includeGlobs: null, excludeGlobs: null, pathRegex: null) { }

    public PathFilter(
        string? pathPrefix,
        IReadOnlyList<string>? includeGlobs,
        IReadOnlyList<string>? excludeGlobs,
        string? pathRegex)
    {
        PathPrefix = pathPrefix;
        IncludeGlobs = includeGlobs ?? Array.Empty<string>();
        ExcludeGlobs = excludeGlobs ?? Array.Empty<string>();
        PathRegex = pathRegex;
    }

    public string? PathPrefix { get; init; }
    public IReadOnlyList<string> IncludeGlobs { get; init; }
    public IReadOnlyList<string> ExcludeGlobs { get; init; }
    public string? PathRegex { get; init; }
}

public readonly record struct RawHit(
    SegmentId SegmentId,
    DocumentOrdinal DocumentOrdinal,
    int StartByte,
    int EndByte,
    int StartLine,
    int EndLine);

public readonly record struct CorpusId(Guid Value);
public readonly record struct ContentRevision(ulong Value);
public readonly record struct StorageGeneration(ulong Value);
public readonly record struct SegmentId(ulong Value);
public readonly record struct DocumentOrdinal(uint Value);

public readonly record struct IndexIdentity(
    CorpusId CorpusId,
    ContentRevision ContentRevision,
    StorageGeneration StorageGeneration);

public readonly record struct DocumentKey
{
    private readonly string? _path;

    public DocumentKey(string path)
    {
        _path = DocumentPath.Normalize(path);
    }

    public string Path => _path ?? throw new InvalidOperationException("The document key is uninitialized.");
}

public readonly record struct SubmittedDocument
{
    private readonly string? _path;

    public SubmittedDocument(string path, ReadOnlyMemory<byte> content)
        : this(path, content, lastModifiedUtc: null) { }

    public SubmittedDocument(
        string path,
        ReadOnlyMemory<byte> content,
        DateTimeOffset? lastModifiedUtc)
        : this(
            path,
            content,
            lastModifiedUtc,
            DocumentContentLifetime.UntilNextRead) { }

    public SubmittedDocument(
        string path,
        ReadOnlyMemory<byte> content,
        DateTimeOffset? lastModifiedUtc,
        DocumentContentLifetime contentLifetime)
    {
        _path = DocumentPath.Normalize(path);
        Content = content;
        LastModifiedUtc = lastModifiedUtc;
        ContentLifetime = contentLifetime;
    }

    public string Path => _path ?? throw new InvalidOperationException("The submitted document is uninitialized.");
    public ReadOnlyMemory<byte> Content { get; }
    public DateTimeOffset? LastModifiedUtc { get; }
    public DocumentContentLifetime ContentLifetime { get; }
}

public interface IHitCollector
{
    bool OnHit(in RawHit hit);
}

public interface IDocumentKeyCollector
{
    bool OnDocument(DocumentKey key);
}

public interface IDocumentSource
{
    bool TryReadNext(out SubmittedDocument document);
}

public interface IIndexSnapshot : IDisposable
{
    DocumentVisitStats VisitDocuments(IDocumentKeyCollector collector, CancellationToken cancellationToken);
    SearchPlan Explain(SearchQuery query, PathFilter pathFilter);
    SearchStats Search(SearchQuery query, PathFilter pathFilter, IHitCollector collector, CancellationToken cancellationToken);
    MaterializedHit Materialize(in RawHit hit, int contextBefore, int contextAfter);
    bool TryGetDocumentBytes(DocumentKey key, out ReadOnlyMemory<byte> content);
}

public interface IOverlaySnapshot : IIndexSnapshot
{
    Task<UpdateStats> Update(IDocumentSource upserts, IReadOnlyList<DocumentKey> deletions, CancellationToken cancellationToken);
    Task<UpdateStats> Update(
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions,
        OverlayUpdateOptions options,
        CancellationToken cancellationToken);
    Task<UpdateStats> CommitToBase(ISiftIndex baseIndex, UpdateOptions options, CancellationToken cancellationToken);
    Task<UpdateStats> ApplyDeltaTo(ISiftIndex target, UpdateOptions options, CancellationToken cancellationToken);
}

public interface ISiftIndex : IDisposable
{
    bool RequiresRebuild { get; }
    IndexIdentity GetIndexIdentity();
    IndexState GetIndexState();
    IIndexSnapshot OpenSnapshot(SearchOptions options);
    IOverlaySnapshot OpenOverlaySnapshot(SearchOptions options);
    WriteActivityStats GetWriteActivityStats();
    IndexValidationStats GetValidationStats();
    MaintenanceStats GetMaintenanceStats();
    StorageStats GetStorageStats();
    bool ShouldCompact(CompactOptions options);
    Task<IndexValidationStats> Validate(CancellationToken cancellationToken);
    Task<BuildStats> Build(IDocumentSource documents, BuildOptions options, CancellationToken cancellationToken);
    Task<BuildStats> Build(
        IAsyncEnumerable<SubmittedDocument> documents,
        BuildOptions options,
        CancellationToken cancellationToken);
    Task<UpdateStats> Update(
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions,
        CancellationToken cancellationToken);
    Task<UpdateStats> Update(
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions,
        UpdateOptions options,
        CancellationToken cancellationToken);
    Task<UpdateStats> Update(
        IAsyncEnumerable<SubmittedDocument> upserts,
        IReadOnlyList<DocumentKey> deletions,
        CancellationToken cancellationToken);
    Task<UpdateStats> Update(
        IAsyncEnumerable<SubmittedDocument> upserts,
        IReadOnlyList<DocumentKey> deletions,
        UpdateOptions options,
        CancellationToken cancellationToken);
    Task<CompactStats> Compact(CompactOptions options, CancellationToken cancellationToken);
}
```

Convenience overloads that supply default options or cancellation tokens are
extension methods in `SiftIndexExtensions`; the interfaces keep their core
contracts explicit.

Build/compaction telemetry includes elapsed time and document/byte/segment
counts. Managed-heap sampling is outside the library and belongs in the benchmark
harness. The public `MeasurePeakManagedMemory` option and `PeakManagedBytes`
properties on `BuildStats`/`CompactStats` have been removed.

Memory ownership rule:

* `SubmittedDocument.Content` is borrowed memory whose declared lifetime is set by `ContentLifetime`
* for `UntilNextRead`, Sift fully consumes or copies `Content` before calling `TryReadNext` again, and the source may reuse the buffer in that next call
* for `UntilOperationCompletes`, the source keeps `Content` immutable until the enclosing build or update task completes
* the core library must not retain caller-owned buffers beyond the declared lifetime
* deletion lists passed to update remain unchanged until the returned task completes

API rules:

* core search path is synchronous and CPU-bound
* async wrappers are allowed outside the hot path
* results must stream through `IHitCollector`
* no mandatory “load all hits into list” API in the core layer
* `null` path-filter constructor inputs mean “no filter”; exposed include/exclude collections are never `null`
* document ingestion is caller-driven; filesystem crawling belongs in an adapter above the core library

---

## 11. Project structure

A solution `Lokad.Sift.slnx` with these projects:

* `Lokad.Sift`

  * root public contracts
  * low-level codecs and scanners
  * internal storage formats and mapped segments
  * indexing, publication, and compaction
  * planning, verification, and hit materialization

* `Lokad.Sift.Cli`

  * `build`
  * `update`
  * `search`
  * `compact`
  * `stats`
  * `verify`
  * `explain`

The CLI is an adapter over the core library; it may source documents from the filesystem, but the core API does not require a filesystem-backed corpus.

* `Lokad.Sift.Benchmarks`

  * microbenchmarks
  * corpus benchmarks

* `Lokad.Sift.Tests`

  * unit
  * integration
  * fuzz
  * differential correctness

* `Lokad.Sift.TestHost`

  * subprocess fixtures for lifecycle and recovery tests

---

## 12. Concurrency and lifecycle

### 12.1 Reader model

* many concurrent readers
* no reader locks in hot path
* snapshot lifetime keeps required segment maps alive
* writers create one immutable, checksummed lease per manifest generation
* snapshots open that generation lease read-only without taking the writer lock
  or writing reader-lifecycle files
* readers never observe dead entries resurrecting or partial compaction

### 12.2 Writer model

* single writer guarded by file lock
* updates are batched
* publication is manifest-swap only
* old segments remain readable until last snapshot releases them
* unreachable segment packs are reclaimed automatically on publication,
  snapshot release, and corpus startup

### 12.3 Query parallelism

Parallelize in this order:

1. across segments
2. within a segment across candidate-document partitions, only when candidate count justifies it

Do **not** spawn a task per document.

Use static partitioning over candidate ordinal arrays / bitsets.

### 12.4 Allocation rules

* no LINQ in hot search path
* no boxing in hot search path
* all temporary arrays from `ArrayPool<T>`
* no per-match string allocation unless materialization requested
* exceptions are not control flow

---

## 13. Explainability and metrics

Every query must be explainable.

`SearchPlan` must report:

* resolved case mode
* scope document count / byte count
* selected path filters
* selected mandatory fragments
* selected n-grams and their `DocFreq`
* direct scan vs indexed plan
* expected verifier kind
* timeout policy

`SearchStats` must report:

* elapsed ms
* scope documents / bytes
* postings bytes read
* candidate documents after index
* candidate documents after fragment precheck
* verified documents
* bytes scanned
* decoded chars
* byte regex invocations
* fallback regex invocations
* hits returned
* early stop reason

Also expose process-level counters/events for:

* build throughput
* update throughput
* segment count
* dead fraction
* mmap bytes
* cache hit ratio for actual managed regex compilation-cache lookups; prepared
  verifier reuse does not synthesize additional cache hits

---

## 14. Performance requirements

These are engineering targets, not semantic requirements.

Sift shall pursue performance primarily through:

* minimizing the number of candidate documents that reach exact verification
* avoiding UTF-16 decoding unless exact semantics require it
* keeping the hot path byte-oriented and allocation-free
* preferring cache-friendly layouts and sequential memory access over maximal compression
* making plan selection explicit, scope-aware, and benchmark-tuned rather than relying on scattered fixed heuristics alone

The main performance risk is not index lookup itself, but over-verification, over-decoding, and poor memory locality.

Sift is optimized primarily for steady-state use in long-lived local processes. One-time cold startup costs such as corpus open, recovery, and integrity validation are acceptable secondary overheads as long as they do not materially degrade steady-state query/update workflows.
Normal segment opens validate identity and structural bounds only. Applications run
explicit full-pack checksum validation outside latency-sensitive search and update
paths; a successful publication has already checksum-validated every new pack.

1. Indexed selective literal queries on warm cache should complete in the low tens of milliseconds on commodity desktop/server CPUs for a 10–50M LOC corpus.
2. Indexed regex queries with extractable mandatory fragments should usually verify only a tiny fraction of scoped documents, preferably through the byte-oriented regex engine rather than fallback `.NET Regex`.
3. Direct-scan fallback must remain acceptable because it operates over the memory-mapped content store, not the original submitted sources.
4. Hot indexed literal path must allocate **zero** managed heap objects per document scanned.
5. Opening a snapshot must not deserialize the corpus into managed objects; only headers, tiny directories, and necessary metadata may be loaded eagerly.
6. Query-time throughput should scale with available cores until memory bandwidth becomes the limiting factor.
7. Selective queries should spend most total time in candidate generation and cheap byte-level verification, not in fallback regex execution, UTF-16 decoding, or hit materialization.

### 14.1 Future optimization candidates

These are promising advanced directions, not mandatory first-iteration requirements:

* packed SIMD multi-literal prechecks such as `Teddy`-style matching, especially for mandatory-fragment verification
* faster skip-friendly postings encodings such as Partitioned Elias-Fano for large sorted document-ordinal lists
* Roaring-style containerized dense/sparse set representations for in-memory candidate scopes when density is highly non-uniform

Such techniques are worth adopting only when measurement shows that they reduce total query cost on real corpora and workloads.

---

## 15. Testing and acceptance criteria

### 15.1 Differential correctness

For every supported query mode, compare engine output against a brute-force baseline over the same normalized corpus store.

Required differential suites:

* fixed string
* regex
* multiline regex
* whole word
* whole line
* path regex
* include/exclude globs
* case-sensitive / insensitive / smart

### 15.2 Regex analyzer soundness

Fuzz the regex analyzer.

Acceptance condition:

* analyzer may miss opportunities
* analyzer must never derive a mandatory fragment that is not actually mandatory

### 15.3 Storage integrity

Required tests:

* crash before segment flush
* crash after segment flush but before manifest swap
* crash during compaction
* corrupted segment header
* corrupted region checksum
* stale temp directory recovery
* stale generation-lease and unreachable-pack recovery
* deferred deletion while an old snapshot is live
* cross-process lease release after a reader crash
* reader snapshot opening while the writer lock is held

### 15.4 Performance regression suite

Benchmarks required for:

* trigram extraction
* postings intersection
* bitset AND
* path prefix search
* SIMD literal scan
* ASCII case-insensitive literal scan
* mandatory-fragment precheck
* byte regex execution path
* regex fallback invocation path
* line/column materialization

---

## 16. Explicit design decisions

These are intentional and shall not be “simplified away” by the coding agent:

* **Use immutable segments**
* **Use memory mapping for query-time access**
* **Store content inside the index**
* **Do not read original submitted sources in the query hot path**
* **Use document-level postings, not universal block-level postings**
* **Use bigram + trigram indexes**
* **Use `.NET Regex` as semantic oracle and fallback authority, not as the intended compute path**
* **Use conservative mandatory-fragment extraction**
* **Use adaptive candidate representations**
* **Keep the on-disk format versioned and explicit**
* **Keep the backend library-first and streaming-first**

And these are explicitly out of scope:

* distributed search
* remote sharding
* semantic symbol indexing
* tokenization/stemming
* Lucene-style inverted-token search
* PCRE2 compatibility guarantees
* native dependencies

---

## 17. Summary architecture sentence

Build a **snapshot-based, immutable-segment, memory-mapped local code-search engine** whose primary acceleration is **document-level bigram/trigram postings over normalized UTF-8 content and paths**, whose correctness depends on **conservative mandatory-fragment extraction plus exact verification**, and whose hot path is written as **low-allocation SIMD-aware managed code**.

---

## 18. Implementation roadmap

Implement the system in the following phases. Later phases may refine earlier phases, but each phase must leave the codebase in a working, testable state.

### 18.1 Phase 1: storage and exact literal search

Required scope:

* public contracts for document submission, snapshots, and raw hit streaming
* manifest and segment storage with immutable publication
* path storage, path filtering, and line index storage
* exact literal search over stored UTF-8 content
* materialization of path, line, column, and context text

Required tests:

* storage integrity for manifest swap and segment publication
* differential correctness for fixed-string search
* path filter correctness for prefix, glob, and path-regex filters

Non-goals:

* regex acceleration
* incremental update
* compaction
* advanced SIMD or postings optimizations beyond what is required for a clean baseline

### 18.2 Phase 2: indexed candidate generation

Required scope:

* content bigram/trigram index build
* path trigram index build
* cost-aware candidate generation over document ordinals
* mandatory-fragment precheck for literal and regex paths
* adaptive dense/sparse candidate representations

Required tests:

* differential correctness for indexed literal queries
* missing-gram and empty-candidate behavior
* postings intersection and path-trigram correctness tests

Non-goals:

* full regex analyzer sophistication beyond conservative mandatory fragments
* incremental update
* compaction

### 18.3 Phase 3: regex and query planning

Required scope:

* byte-oriented regex verification for the supported fast subset, plus `.NET Regex` fallback/oracle handling
* conservative mandatory-fragment extraction
* smart case handling
* multiline, whole-word, and whole-line semantics
* explainability and query statistics
* decode-minimizing verifier paths
* explicit planner candidates and centralized benchmark-tuned scoring

Required tests:

* differential correctness for regex, multiline regex, whole-word, and whole-line queries
* regex analyzer soundness fuzzing
* timeout and fallback-path tests

Non-goals:

* persistent planner telemetry
* speculative advanced optimizations not yet justified by measurement

### 18.4 Phase 4: update, compaction, and performance tuning

Required scope:

* incremental update with document upserts and deletions
* dead-ordinal tracking in manifests
* compaction over live documents
* benchmark suite and performance regression gates
* targeted low-level optimizations justified by benchmark evidence

Required tests:

* update-batch rule enforcement
* crash safety across update and compaction
* performance regression benchmarks for hot-path primitives

Non-goals:

* distributed execution
* semantic indexing
* speculative complexity that is not backed by measured wins on real corpora

[1]: https://learn.microsoft.com/en-us/dotnet/api/system.io.memorymappedfiles.memorymappedfile?view=net-10.0 "https://learn.microsoft.com/en-us/dotnet/api/system.io.memorymappedfiles.memorymappedfile?view=net-10.0"
[2]: https://learn.microsoft.com/en-us/dotnet/standard/base-types/regular-expression-options "https://learn.microsoft.com/en-us/dotnet/standard/base-types/regular-expression-options"

