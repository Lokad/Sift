using Lokad.Sift;
using Lokad.Sift.Search;
using Lokad.Sift.Storage;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: sift <build|update|search|compact|stats|verify|explain> <corpus> [...]");
    return 1;
}

var command = args[0];
var corpusPath = args[1];
using var index = new Sift(corpusPath);

switch (command)
{
    case "build":
        {
            var root = args.Length >= 3 ? args[2] : Environment.CurrentDirectory;
            var buildStats = await index.Build(new FileSystemDocumentSource(root));
            Console.WriteLine($"submitted={buildStats.SubmittedDocuments} indexed={buildStats.IndexedDocuments} rejected={buildStats.RejectedDocuments} submittedBytes={buildStats.SubmittedBytes} bytes={buildStats.ContentBytes} rejectedBytes={buildStats.RejectedBytes} ms={buildStats.ElapsedMilliseconds}");
            return 0;
        }
    case "update":
        {
            var root = args.Length >= 3 ? args[2] : Environment.CurrentDirectory;
            var deletions = args.Skip(3).Select(static path => new DocumentKey(path)).ToArray();
            var update = await index.Update(new FileSystemDocumentSource(root), deletions);
            Console.WriteLine($"submittedUpserts={update.SubmittedUpserts} upserts={update.UpsertedDocuments} rejectedUpserts={update.RejectedUpserts} deletions={update.DeletedDocuments} segments={update.PublishedSegments} submittedBytes={update.SubmittedBytes} bytes={update.ContentBytes} rejectedBytes={update.RejectedBytes} ms={update.ElapsedMilliseconds}");
            return 0;
        }
    case "search":
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Usage: sift search <corpus> <pattern> [options]");
                return 1;
            }

            var options = ParseSearchOptions(args.AsSpan(2));
            using var snapshot = index.OpenSnapshot();
            var collector = new ConsoleCollector(snapshot, options.ContextBefore, options.ContextAfter);
            var stats = snapshot.Search(options.Query, options.Filter, collector);
            Console.WriteLine($"hits={stats.HitsReturned} docs={stats.ScopeDocuments} bytes={stats.BytesScanned} regex={stats.RegexInvocations} utf8Regex={stats.Utf8RegexInvocations} fallbackRegex={stats.FallbackRegexInvocations} nonBacktracking={stats.NonBacktrackingRegexInvocations} ms={stats.ElapsedMilliseconds}");
            return 0;
        }
    case "compact":
        {
            var compact = await index.Compact(new CompactOptions());
            var storage = index.GetStorageStats();
            Console.WriteLine($"inputSegments={compact.InputSegments} outputSegments={compact.OutputSegments} mergedDocuments={compact.MergedDocuments} bytes={compact.ContentBytes} hasMoreWork={compact.HasMoreWork} physicalSegmentBytes={storage.PhysicalSegmentBytes} retiredSegmentBytes={storage.RetiredSegmentBytes} ms={compact.ElapsedMilliseconds}");
            return 0;
        }
    case "stats":
        {
            var maintenance = index.GetMaintenanceStats();
            var storage = new CorpusStorage(corpusPath);
            var manifest = storage.ReadManifest();
            var physical = index.GetStorageStats();

            Console.WriteLine($"generation={maintenance.Generation} corpusId={manifest.CorpusId} segments={maintenance.SegmentCount} totalDocuments={maintenance.TotalDocuments} liveDocuments={maintenance.LiveDocuments} deadDocuments={maintenance.DeadDocuments} deadFraction={maintenance.DeadDocumentFraction:F3} deadContentFraction={maintenance.DeadContentFraction:F3} contentBytes={maintenance.ContentBytes} liveContentBytes={maintenance.EstimatedLiveContentBytes} reclaimableContentBytes={maintenance.EstimatedReclaimableContentBytes} deadOrdinalEntries={maintenance.DeadOrdinalEntries} segmentsWithDeletions={maintenance.SegmentsWithDeletions} smallSegments={maintenance.SmallSegmentCount} manifestSegmentBytes={physical.ManifestSegmentBytes} physicalSegmentBytes={physical.PhysicalSegmentBytes} retiredSegmentBytes={physical.RetiredSegmentBytes} retiredSegments={physical.RetiredSegmentCount} deferredSegmentBytes={physical.SnapshotDeferredSegmentBytes} pendingDeletionBytes={physical.PendingDeletionSegmentBytes} blockedReclamationBytes={physical.BlockedReclamationSegmentBytes} tempBytes={physical.TemporaryBytes} leasedGenerations={physical.LeasedGenerationCount} oldestLeasedGeneration={physical.OldestLeasedGeneration} reclamationBlocked={physical.IsReclamationBlocked} mappedBytes={MappedSegment.CurrentMappedBytes} openSegments={MappedSegment.CurrentOpenSegments} regexCacheEntries={SearchProcessMetrics.RegexCacheEntries} regexCacheHits={SearchProcessMetrics.RegexCacheHits} regexCacheMisses={SearchProcessMetrics.RegexCacheMisses} regexCacheHitRate={SearchProcessMetrics.RegexCacheHitRate:F3}");
            return 0;
        }
    case "verify":
        {
            var storage = new CorpusStorage(corpusPath);
            var manifest = storage.ReadManifest();
            var segmentCount = 0;
            var documentCount = 0;
            foreach (var entry in manifest.Segments)
            {
                using var segment = storage.OpenSegment(entry.SegmentId);
                segmentCount++;
                documentCount += segment.DocumentCount;
            }

            Console.WriteLine($"verifiedManifestGeneration={manifest.Generation} verifiedSegments={segmentCount} verifiedDocuments={documentCount}");
            return 0;
        }
    case "explain":
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Usage: sift explain <corpus> <pattern> [options]");
                return 1;
            }

            var options = ParseSearchOptions(args.AsSpan(2));
            using var snapshot = index.OpenSnapshot();
            var plan = snapshot.Explain(options.Query, options.Filter);
            Console.WriteLine($"pattern={plan.PatternKind} case={plan.ResolvedCaseMode} scopeDocs={plan.ScopeDocumentCount} scopeBytes={plan.ScopeByteCount} usesIndex={plan.UsesIndex} planFamily={plan.PlanFamily} candidate={plan.CandidateKind} verifier={plan.VerifierKind} timeout={plan.TimeoutPolicy} pathPrefix={plan.UsesPathPrefixFilter} pathRegex={plan.UsesPathRegexFilter} includeGlobs={plan.UsesIncludeGlobs} excludeGlobs={plan.UsesExcludeGlobs} literalVerifier={plan.CanUseLiteralVerifier} nonBacktracking={plan.CanUseNonBacktracking} decode={plan.NeedsUnicodeDecode} wholeDocument={plan.MayRequireWholeDocumentVerification}");
            Console.WriteLine($"fragments={string.Join(", ", plan.MandatoryFragments)}");
            Console.WriteLine($"ngrams={string.Join(", ", plan.SelectedNgrams.Select(static gram => $"{gram.Text}/{gram.DocFreq}"))}");
            return 0;
        }
    default:
        Console.Error.WriteLine($"Unsupported command: {command}");
        return 1;
}

static SearchCliOptions ParseSearchOptions(ReadOnlySpan<string> arguments, PatternMode defaultPatternMode = PatternMode.Regex)
{
    var pattern = arguments[0];
    var patternMode = defaultPatternMode;
    var caseMode = CaseMode.Smart;
    var wholeWord = false;
    var wholeLine = false;
    var multiline = false;
    var dotAll = false;
    var contextBefore = 0;
    var contextAfter = 0;
    var maxHits = 10_000;
    var maxDocuments = int.MaxValue;
    long maxBytesScanned = long.MaxValue;
    var timeoutPolicy = TimeoutPolicy.StopSearch;
    var includeGlobs = new List<string>();
    var excludeGlobs = new List<string>();
    string? pathPrefix = null;
    string? pathRegex = null;

    for (var i = 1; i < arguments.Length; i++)
    {
        var arg = arguments[i];
        if (arg.Equals("literal", StringComparison.OrdinalIgnoreCase) || arg.Equals("--literal", StringComparison.OrdinalIgnoreCase))
        {
            patternMode = PatternMode.Literal;
            continue;
        }

        if (arg.Equals("regex", StringComparison.OrdinalIgnoreCase) || arg.Equals("--regex", StringComparison.OrdinalIgnoreCase))
        {
            patternMode = PatternMode.Regex;
            continue;
        }

        if (arg.Equals("--case-sensitive", StringComparison.OrdinalIgnoreCase))
        {
            caseMode = CaseMode.Sensitive;
            continue;
        }

        if (arg.Equals("--case-insensitive", StringComparison.OrdinalIgnoreCase))
        {
            caseMode = CaseMode.Insensitive;
            continue;
        }

        if (arg.Equals("--smart-case", StringComparison.OrdinalIgnoreCase))
        {
            caseMode = CaseMode.Smart;
            continue;
        }

        if (arg.Equals("--whole-word", StringComparison.OrdinalIgnoreCase))
        {
            wholeWord = true;
            continue;
        }

        if (arg.Equals("--whole-line", StringComparison.OrdinalIgnoreCase))
        {
            wholeLine = true;
            continue;
        }

        if (arg.Equals("--multiline", StringComparison.OrdinalIgnoreCase))
        {
            multiline = true;
            continue;
        }

        if (arg.Equals("--dotall", StringComparison.OrdinalIgnoreCase))
        {
            dotAll = true;
            continue;
        }

        if (arg.Equals("--timeout-throw", StringComparison.OrdinalIgnoreCase))
        {
            timeoutPolicy = TimeoutPolicy.Throw;
            continue;
        }

        if (arg.Equals("--timeout-stop", StringComparison.OrdinalIgnoreCase))
        {
            timeoutPolicy = TimeoutPolicy.StopSearch;
            continue;
        }

        if (arg.StartsWith("--include=", StringComparison.OrdinalIgnoreCase))
        {
            includeGlobs.Add(arg["--include=".Length..]);
            continue;
        }

        if (arg.StartsWith("--exclude=", StringComparison.OrdinalIgnoreCase))
        {
            excludeGlobs.Add(arg["--exclude=".Length..]);
            continue;
        }

        if (arg.StartsWith("--path-regex=", StringComparison.OrdinalIgnoreCase))
        {
            pathRegex = arg["--path-regex=".Length..];
            continue;
        }

        if (arg.StartsWith("--path-prefix=", StringComparison.OrdinalIgnoreCase))
        {
            pathPrefix = arg["--path-prefix=".Length..];
            continue;
        }

        if (arg.StartsWith("--before=", StringComparison.OrdinalIgnoreCase))
        {
            contextBefore = int.Parse(arg["--before=".Length..]);
            continue;
        }

        if (arg.StartsWith("--after=", StringComparison.OrdinalIgnoreCase))
        {
            contextAfter = int.Parse(arg["--after=".Length..]);
            continue;
        }

        if (arg.StartsWith("--max-hits=", StringComparison.OrdinalIgnoreCase))
        {
            maxHits = int.Parse(arg["--max-hits=".Length..]);
            continue;
        }

        if (arg.StartsWith("--max-docs=", StringComparison.OrdinalIgnoreCase))
        {
            maxDocuments = int.Parse(arg["--max-docs=".Length..]);
            continue;
        }

        if (arg.StartsWith("--max-bytes=", StringComparison.OrdinalIgnoreCase))
        {
            maxBytesScanned = long.Parse(arg["--max-bytes=".Length..]);
            continue;
        }
    }

    return new SearchCliOptions(
        new SearchQuery(pattern, patternMode, caseMode, wholeWord, wholeLine, multiline, dotAll, maxHits, maxDocuments, maxBytesScanned, timeoutPolicy),
        new PathFilter(pathPrefix, includeGlobs.Count == 0 ? null : [.. includeGlobs], excludeGlobs.Count == 0 ? null : [.. excludeGlobs], pathRegex),
        contextBefore,
        contextAfter);
}

internal readonly record struct SearchCliOptions(
    SearchQuery Query,
    PathFilter Filter,
    int ContextBefore,
    int ContextAfter);

internal sealed class FileSystemDocumentSource : IDocumentSource
{
    private readonly IEnumerator<string> _files;
    private readonly string _root;

    public FileSystemDocumentSource(string root)
    {
        _root = Path.GetFullPath(root);
        _files = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).GetEnumerator();
    }

    public bool TryReadNext(out SubmittedDocument document)
    {
        if (!_files.MoveNext())
        {
            document = default;
            return false;
        }

        var file = _files.Current;
        document = new SubmittedDocument(
            Path.GetRelativePath(_root, file).Replace('\\', '/'),
            File.ReadAllBytes(file),
            File.GetLastWriteTimeUtc(file));
        return true;
    }
}

internal sealed class ConsoleCollector : IHitCollector
{
    private readonly IIndexSnapshot _snapshot;
    private readonly int _contextBefore;
    private readonly int _contextAfter;

    public ConsoleCollector(IIndexSnapshot snapshot, int contextBefore, int contextAfter)
    {
        _snapshot = snapshot;
        _contextBefore = contextBefore;
        _contextAfter = contextAfter;
    }

    public bool OnHit(in RawHit hit)
    {
        var materialized = _snapshot.Materialize(hit, _contextBefore, _contextAfter);
        Console.WriteLine($"{materialized.Path}:{materialized.StartLine}:{materialized.StartColumn}: {materialized.MatchText}");
        return true;
    }
}

