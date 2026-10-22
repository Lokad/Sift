using Lokad.Sift;
using System.Globalization;

Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: sift <build|update|search|compact|stats|verify|explain> <corpus> [...]");
    return 1;
}

var command = args[0];
SearchCliOptions parsedOptions = default;
if (command is "search" or "explain")
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine($"Usage: sift {command} <corpus> <pattern> [options]");
        return 1;
    }
    try { parsedOptions = ParseSearchOptions(args.AsSpan(2)); }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
}
var corpusPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1]));
var inputRoot = command is "build" or "update"
    ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(args.Length >= 3 ? args[2] : Environment.CurrentDirectory))
    : null;
if (inputRoot is not null && (File.GetAttributes(inputRoot) & FileAttributes.ReparsePoint) != 0)
{
    Console.Error.WriteLine("The input directory must not be a symbolic link or junction.");
    return 1;
}
if (string.Equals(corpusPath, inputRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
{
    Console.Error.WriteLine("The corpus directory must differ from the input directory.");
    return 1;
}
var siftOptions = new SiftOptions();
using var index = new SiftIndex(corpusPath, siftOptions);

switch (command)
{
    case "build":
        {
            using var source = new FileSystemDocumentSource(inputRoot!, corpusPath, siftOptions.MaxDocumentBytes);
            var buildStats = await index.Build(source);
            Console.WriteLine($"submitted={buildStats.SubmittedDocuments} indexed={buildStats.IndexedDocuments} rejected={buildStats.RejectedDocuments} submittedBytes={buildStats.SubmittedBytes + source.UnmaterializedRejectedBytes} bytes={buildStats.ContentBytes} rejectedBytes={buildStats.RejectedBytes + source.UnmaterializedRejectedBytes} ms={buildStats.ElapsedMilliseconds}");
            return 0;
        }
    case "update":
        {
            var deletions = args.Skip(3).Select(static path => new DocumentKey(path)).ToArray();
            using var source = new FileSystemDocumentSource(inputRoot!, corpusPath, siftOptions.MaxDocumentBytes);
            var update = await index.Update(source, deletions, new UpdateOptions(RejectedUpsertPolicy.DeleteExisting));
            Console.WriteLine($"submittedUpserts={update.SubmittedUpserts} upserts={update.UpsertedDocuments} skippedUpserts={update.SkippedUpserts} rejectedUpserts={update.RejectedUpserts} deletions={update.DeletedDocuments} segments={update.PublishedSegments} submittedBytes={update.SubmittedBytes + source.UnmaterializedRejectedBytes} bytes={update.ContentBytes} skippedBytes={update.SkippedBytes} rejectedBytes={update.RejectedBytes + source.UnmaterializedRejectedBytes} ms={update.ElapsedMilliseconds}");
            return 0;
        }
    case "search":
        {
            var options = parsedOptions;
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
            var state = index.GetIndexState();
            var physical = index.GetStorageStats();

            Console.WriteLine($"generation={maintenance.Generation} corpusId={state.Identity.CorpusId} segments={maintenance.SegmentCount} totalDocuments={maintenance.TotalDocuments} liveDocuments={maintenance.LiveDocuments} deadDocuments={maintenance.DeadDocuments} deadFraction={maintenance.DeadDocumentFraction:F3} deadContentFraction={maintenance.DeadContentFraction:F3} contentBytes={maintenance.ContentBytes} liveContentBytes={maintenance.EstimatedLiveContentBytes} reclaimableContentBytes={maintenance.EstimatedReclaimableContentBytes} deadOrdinalEntries={maintenance.DeadOrdinalEntries} segmentsWithDeletions={maintenance.SegmentsWithDeletions} smallSegments={maintenance.SmallSegmentCount} manifestSegmentBytes={physical.ManifestSegmentBytes} physicalSegmentBytes={physical.PhysicalSegmentBytes} corpusPhysicalBytes={physical.CorpusPhysicalBytes} maximumPhysicalBytes={physical.MaximumPhysicalBytes} availableBytes={physical.AvailableBytes} retiredSegmentBytes={physical.RetiredSegmentBytes} retiredSegments={physical.RetiredSegmentCount} deferredSegmentBytes={physical.SnapshotDeferredSegmentBytes} pendingDeletionBytes={physical.PendingDeletionSegmentBytes} blockedReclamationBytes={physical.BlockedReclamationSegmentBytes} tempBytes={physical.TemporaryBytes} leasedGenerations={physical.LeasedGenerationCount} oldestLeasedGeneration={physical.OldestLeasedGeneration} reclamationBlocked={physical.IsReclamationBlocked} reclamationFailures={physical.ReclamationFailureCount} lastReclamationFailure={physical.LastReclamationFailure} mappedBytes={SiftProcessMetrics.MappedSegmentBytes} openSegments={SiftProcessMetrics.OpenSegments} nativeIndexWorkspaceBytes={SiftProcessMetrics.NativeIndexWorkspaceBytes} regexCacheEntries={SiftProcessMetrics.RegexCacheEntries} regexCachePatternCharacters={SiftProcessMetrics.RegexCachePatternCharacters} utf8RegexCacheEntries={SiftProcessMetrics.Utf8RegexCacheEntries} utf8RegexCachePatternCharacters={SiftProcessMetrics.Utf8RegexCachePatternCharacters} regexCacheHits={SiftProcessMetrics.RegexCacheHits} regexCacheMisses={SiftProcessMetrics.RegexCacheMisses} regexCacheHitRate={SiftProcessMetrics.RegexCacheHitRate:F3}");
            return 0;
        }
    case "verify":
        {
            var validation = await index.Validate();
            var maintenance = index.GetMaintenanceStats();
            Console.WriteLine($"state={validation.State} generation={validation.Generation} verifiedSegments={validation.ValidatedSegments} verifiedDocuments={maintenance.TotalDocuments} totalSegments={validation.TotalSegments} validatedBytes={validation.ValidatedBytes} totalBytes={validation.TotalBytes} ms={validation.ElapsedMilliseconds}");
            return 0;
        }
    case "explain":
        {
            var options = parsedOptions;
            using var snapshot = index.OpenSnapshot();
            var plan = snapshot.Explain(options.Query, options.Filter);
            Console.WriteLine($"pattern={plan.PatternKind} case={plan.ResolvedCaseMode} scopeDocs={plan.ScopeDocumentCount} scopeBytes={plan.ScopeByteCount} usesIndex={plan.UsesIndex} candidate={plan.CandidateKind} combination={plan.CandidateCombination} verifier={plan.VerifierKind} regexRoute={plan.RegexRouteReason} regexTimeout={plan.RegexTimeoutPolicy} pathPrefix={plan.UsesPathPrefixFilter} pathRegex={plan.UsesPathRegexFilter} includeGlobs={plan.UsesIncludeGlobs} excludeGlobs={plan.UsesExcludeGlobs} literalVerifier={plan.CanUseLiteralVerifier} nonBacktracking={plan.CanUseNonBacktracking} decode={plan.NeedsUnicodeDecode} wholeDocument={plan.MayRequireWholeDocumentVerification}");
            Console.WriteLine($"fragments={string.Join(", ", plan.MandatoryFragments)}");
            Console.WriteLine($"ngrams={string.Join(", ", plan.SelectedNgrams.Select(static gram => $"{gram.Text}[{gram.Hex}]/{gram.DocumentFrequency}"))}");
            return 0;
        }
    default:
        Console.Error.WriteLine($"Unsupported command: {command}");
        return 1;
}

static SearchCliOptions ParseSearchOptions(ReadOnlySpan<string> arguments)
{
    var pattern = arguments[0];
    var patternMode = PatternMode.Regex;
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
    var regexTimeoutPolicy = RegexTimeoutPolicy.StopSearch;
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
            regexTimeoutPolicy = RegexTimeoutPolicy.Throw;
            continue;
        }

        if (arg.Equals("--timeout-stop", StringComparison.OrdinalIgnoreCase))
        {
            regexTimeoutPolicy = RegexTimeoutPolicy.StopSearch;
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
            contextBefore = (int)ParseNumber(arg, "--before=", 0, int.MaxValue);
            continue;
        }

        if (arg.StartsWith("--after=", StringComparison.OrdinalIgnoreCase))
        {
            contextAfter = (int)ParseNumber(arg, "--after=", 0, int.MaxValue);
            continue;
        }

        if (arg.StartsWith("--max-hits=", StringComparison.OrdinalIgnoreCase))
        {
            maxHits = (int)ParseNumber(arg, "--max-hits=", 1, int.MaxValue);
            continue;
        }

        if (arg.StartsWith("--max-docs=", StringComparison.OrdinalIgnoreCase))
        {
            maxDocuments = (int)ParseNumber(arg, "--max-docs=", 1, int.MaxValue);
            continue;
        }

        if (arg.StartsWith("--max-bytes=", StringComparison.OrdinalIgnoreCase))
        {
            maxBytesScanned = ParseNumber(arg, "--max-bytes=", 1, long.MaxValue);
            continue;
        }
        throw new ArgumentException($"Unknown option or argument: '{arg}'.");
    }

    return new SearchCliOptions(
        new SearchQuery(pattern, patternMode, caseMode)
        {
            WholeWord = wholeWord,
            WholeLine = wholeLine,
            Multiline = multiline,
            DotAll = dotAll,
            MaxHits = maxHits,
            MaxMatchedDocuments = maxDocuments,
            MaxBytesScanned = maxBytesScanned,
            RegexTimeoutPolicy = regexTimeoutPolicy
        },
        new PathFilter
        {
            PathPrefix = pathPrefix,
            IncludeGlobs = [.. includeGlobs],
            ExcludeGlobs = [.. excludeGlobs],
            PathRegex = pathRegex
        },
        contextBefore,
        contextAfter);

    static long ParseNumber(string argument, string prefix, long minimum, long maximum)
    {
        if (!long.TryParse(argument.AsSpan(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
            value < minimum || value > maximum)
            throw new ArgumentException($"Invalid value for {prefix.TrimEnd('=')}: expected an integer from {minimum} to {maximum}.");
        return value;
    }
}

internal readonly record struct SearchCliOptions(
    SearchQuery Query,
    PathFilter Filter,
    int ContextBefore,
    int ContextAfter);

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
        var beforeStart = Math.Max(1L, (long)materialized.StartLine - _contextBefore);
        for (var i = 0; i < materialized.BeforeContext.Count; i++)
            Console.WriteLine($"{materialized.Path}-{beforeStart + i}- {materialized.BeforeContext[i]}");
        Console.WriteLine($"{materialized.Path}:{materialized.StartLine}:{materialized.StartColumn}: {materialized.MatchText}");
        for (var i = 0; i < materialized.AfterContext.Count; i++)
            Console.WriteLine($"{materialized.Path}-{(long)materialized.EndLine + 1 + i}- {materialized.AfterContext[i]}");
        if (materialized.IsContextTruncated) Console.WriteLine($"{materialized.Path}: [context truncated]");
        return true;
    }
}

