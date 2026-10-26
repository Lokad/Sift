using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Running;
using Lokad.Sift;
using Lokad.Sift.LowLevel;
using Lokad.Sift.Storage;


internal sealed class BenchmarkOptions
{
    public BenchmarkMode Mode { get; init; } = BenchmarkMode.Synthetic;
    public int DocumentCount { get; init; } = 50_000;
    public int WarmupIterations { get; init; } = 2;
    public int MeasurementIterations { get; init; } = 7;
    public string? JsonOutputPath { get; init; }
    public Dictionary<string, double> MedianThresholds { get; init; } = new(StringComparer.Ordinal);
    public string? ProfileJsonPath { get; init; }
    public string? RealCorpusRoot { get; init; }
    public string? IndexCacheRoot { get; init; }
    public string? RealQueryPackJsonPath { get; init; }
    public string? QueryId { get; init; }
    public string[]? CorpusIds { get; init; }
    public int? MaxFiles { get; init; }
    public bool UseIndexCache { get; init; }
    public bool QueryOnly { get; init; }
    public bool RefreshIndex { get; init; }
    public bool PrepareIndexOnly { get; init; }
    public bool MaintenanceSteadyState { get; init; }
    public int? QueryTimeoutMilliseconds { get; init; }
    public string[]? PostingsBdnFilter { get; init; }
    public int PostingsInnerRepeat { get; init; } = 1;

    public static BenchmarkOptions Parse(string[] args)
    {
        string? profileJsonPath = null;
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        var mode = BenchmarkMode.Synthetic;
        string? realCorpusRoot = null;
        string? indexCacheRoot = null;
        string? realQueryPackJsonPath = null;
        string? queryId = null;
        string[]? corpusIds = null;
        int? maxFiles = null;
        var useIndexCache = false;
        var queryOnly = false;
        var refreshIndex = false;
        var prepareIndexOnly = false;
        var maintenanceSteadyState = false;
        int? queryTimeoutMilliseconds = null;
        string[]? postingsBdnFilter = null;
        var postingsInnerRepeat = 1;

        foreach (var arg in args)
        {
            if (arg.StartsWith("--profile-json=", StringComparison.Ordinal))
            {
                profileJsonPath = arg["--profile-json=".Length..];
                continue;
            }

            if (arg.StartsWith("--json=", StringComparison.Ordinal))
            {
                overrides[arg] = string.Empty;
                continue;
            }

            if (arg.StartsWith("--assert-median-ms=", StringComparison.Ordinal))
            {
                overrides[arg] = string.Empty;
                continue;
            }

            if (arg.StartsWith("--documents=", StringComparison.Ordinal) ||
                arg.StartsWith("--warmup=", StringComparison.Ordinal) ||
                arg.StartsWith("--iterations=", StringComparison.Ordinal))
            {
                overrides[arg] = string.Empty;
                continue;
            }

            if (arg.Equals("--real-corpus", StringComparison.Ordinal))
            {
                mode = BenchmarkMode.RealCorpus;
                continue;
            }

            if (arg.Equals("--update-bench", StringComparison.Ordinal))
            {
                mode = BenchmarkMode.UpdateBench;
                continue;
            }

            if (arg.Equals("--compact-bench", StringComparison.Ordinal))
            {
                mode = BenchmarkMode.CompactBench;
                continue;
            }

            if (arg.Equals("--postings-micro", StringComparison.Ordinal))
            {
                mode = BenchmarkMode.PostingsMicro;
                continue;
            }

            if (arg.Equals("--postings-stats", StringComparison.Ordinal))
            {
                mode = BenchmarkMode.PostingsStats;
                continue;
            }

            if (arg.Equals("--postings-bdn", StringComparison.Ordinal))
            {
                mode = BenchmarkMode.PostingsBdn;
                continue;
            }

            if (arg.StartsWith("--bdn-filter=", StringComparison.Ordinal))
            {
                mode = BenchmarkMode.PostingsBdn;
                postingsBdnFilter =
                    [.. arg["--bdn-filter=".Length..]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                continue;
            }

            if (arg.StartsWith("--real-corpus-root=", StringComparison.Ordinal))
            {
                if (mode == BenchmarkMode.Synthetic)
                {
                    mode = BenchmarkMode.RealCorpus;
                }
                realCorpusRoot = arg["--real-corpus-root=".Length..];
                continue;
            }

            if (arg.StartsWith("--real-query-pack-json=", StringComparison.Ordinal))
            {
                mode = BenchmarkMode.RealCorpus;
                realQueryPackJsonPath = arg["--real-query-pack-json=".Length..];
                continue;
            }

            if (arg.StartsWith("--query-id=", StringComparison.Ordinal))
            {
                mode = BenchmarkMode.RealCorpus;
                queryId = arg["--query-id=".Length..];
                continue;
            }

            if (arg.StartsWith("--index-cache-root=", StringComparison.Ordinal))
            {
                if (mode == BenchmarkMode.Synthetic)
                {
                    mode = BenchmarkMode.RealCorpus;
                }
                indexCacheRoot = arg["--index-cache-root=".Length..];
                continue;
            }

            if (arg.Equals("--use-index-cache", StringComparison.Ordinal))
            {
                if (mode == BenchmarkMode.Synthetic)
                {
                    mode = BenchmarkMode.RealCorpus;
                }
                useIndexCache = true;
                continue;
            }

            if (arg.Equals("--query-only", StringComparison.Ordinal))
            {
                if (mode == BenchmarkMode.Synthetic)
                {
                    mode = BenchmarkMode.RealCorpus;
                }
                useIndexCache = true;
                queryOnly = true;
                continue;
            }

            if (arg.Equals("--refresh-index", StringComparison.Ordinal))
            {
                if (mode == BenchmarkMode.Synthetic)
                {
                    mode = BenchmarkMode.RealCorpus;
                }
                useIndexCache = true;
                refreshIndex = true;
                continue;
            }

            if (arg.Equals("--prepare-index-only", StringComparison.Ordinal))
            {
                if (mode == BenchmarkMode.Synthetic)
                {
                    mode = BenchmarkMode.RealCorpus;
                }
                useIndexCache = true;
                prepareIndexOnly = true;
                continue;
            }

            if (arg.Equals("--maintenance-steady-state", StringComparison.Ordinal))
            {
                maintenanceSteadyState = true;
                continue;
            }

            if (arg.StartsWith("--corpora=", StringComparison.Ordinal))
            {
                corpusIds = arg["--corpora=".Length..]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                continue;
            }

            if (TryReadInt32(arg, "--query-timeout-ms=", out var parsedQueryTimeoutMilliseconds))
            {
                if (parsedQueryTimeoutMilliseconds <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(args), "Query timeout must be positive.");
                }

                queryTimeoutMilliseconds = parsedQueryTimeoutMilliseconds;
                continue;
            }

            if (TryReadInt32(arg, "--max-files=", out var parsedMaxFiles))
            {
                if (parsedMaxFiles <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(args), "Max files must be positive.");
                }

                maxFiles = parsedMaxFiles;
                continue;
            }

            if (TryReadInt32(arg, "--postings-inner-repeat=", out var parsedPostingsInnerRepeat))
            {
                if (parsedPostingsInnerRepeat <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(args), "Postings inner repeat must be positive.");
                }

                postingsInnerRepeat = parsedPostingsInnerRepeat;
                continue;
            }

            throw new ArgumentException($"Unknown benchmark argument '{arg}'.");
        }

        var profile = string.IsNullOrWhiteSpace(profileJsonPath)
            ? new BenchmarkProfile()
            : LoadProfile(profileJsonPath);
        var documentCount = profile.DocumentCount ?? 50_000;
        var warmupIterations = profile.WarmupIterations ?? 2;
        var measurementIterations = profile.MeasurementIterations ?? 7;
        var jsonOutputPath = profile.JsonOutputPath;
        var thresholds = profile.MedianThresholds is null
            ? new Dictionary<string, double>(StringComparer.Ordinal)
            : new Dictionary<string, double>(profile.MedianThresholds, StringComparer.Ordinal);

        foreach (var key in overrides.Keys)
        {
            if (TryReadInt32(key, "--documents=", out var parsedDocumentCount))
            {
                documentCount = parsedDocumentCount;
                continue;
            }

            if (TryReadInt32(key, "--warmup=", out var parsedWarmup))
            {
                warmupIterations = parsedWarmup;
                continue;
            }

            if (TryReadInt32(key, "--iterations=", out var parsedIterations))
            {
                measurementIterations = parsedIterations;
                continue;
            }

            if (key.StartsWith("--json=", StringComparison.Ordinal))
            {
                jsonOutputPath = key["--json=".Length..];
                continue;
            }

            if (key.StartsWith("--assert-median-ms=", StringComparison.Ordinal))
            {
                var payload = key["--assert-median-ms=".Length..];
                var separator = payload.IndexOf(':');
                if (separator <= 0 ||
                    separator == payload.Length - 1 ||
                    !double.TryParse(payload[(separator + 1)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var threshold) ||
                    threshold < 0)
                {
                    throw new ArgumentException($"Invalid benchmark threshold argument '{key}'.");
                }

                thresholds[payload[..separator]] = threshold;
            }
        }

        if (documentCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Document count must be positive.");
        }

        if (warmupIterations < 0 || measurementIterations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Warmup must be non-negative and iterations must be positive.");
        }

        return new BenchmarkOptions
        {
            Mode = mode,
            DocumentCount = documentCount,
            WarmupIterations = warmupIterations,
            MeasurementIterations = measurementIterations,
            JsonOutputPath = string.IsNullOrWhiteSpace(jsonOutputPath) ? null : jsonOutputPath,
            MedianThresholds = thresholds,
            ProfileJsonPath = string.IsNullOrWhiteSpace(profileJsonPath) ? null : profileJsonPath,
            RealCorpusRoot = realCorpusRoot,
            IndexCacheRoot = indexCacheRoot,
            RealQueryPackJsonPath = realQueryPackJsonPath,
            QueryId = string.IsNullOrWhiteSpace(queryId) ? null : queryId,
            CorpusIds = corpusIds,
            MaxFiles = maxFiles,
            UseIndexCache = useIndexCache,
            QueryOnly = queryOnly,
            RefreshIndex = refreshIndex,
            PrepareIndexOnly = prepareIndexOnly,
            MaintenanceSteadyState = maintenanceSteadyState
            ,
            QueryTimeoutMilliseconds = queryTimeoutMilliseconds
            ,
            PostingsBdnFilter = postingsBdnFilter
            ,
            PostingsInnerRepeat = postingsInnerRepeat
        };
    }

    private static BenchmarkProfile LoadProfile(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BenchmarkProfile>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new BenchmarkProfile();
    }

    private static bool TryReadInt32(string arg, string prefix, out int value)
    {
        if (!arg.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = default;
            return false;
        }

        if (!int.TryParse(arg[prefix.Length..], out value))
        {
            throw new ArgumentException($"Invalid integer argument '{arg}'.");
        }

        return true;
    }

}

internal sealed class BenchmarkProfile
{
    public int? DocumentCount { get; init; }
    public int? WarmupIterations { get; init; }
    public int? MeasurementIterations { get; init; }
    public string? JsonOutputPath { get; init; }
    public Dictionary<string, double>? MedianThresholds { get; init; }
}

internal sealed class BenchmarkSource : IDocumentSource
{
    private readonly int _count;
    private int _index;

    public BenchmarkSource(int count)
    {
        _count = count;
    }

    public bool TryReadNext(out SubmittedDocument document)
    {
        if (_index >= _count)
        {
            document = default;
            return false;
        }

        var i = _index++;
        var text = $"alpha beta gamma needle {i}\nalpha beta gamma delta\nALPHA beta gamma NEEDLE {i}\n";
        document = new SubmittedDocument(
            $"src/{i:D6}.txt",
            Encoding.UTF8.GetBytes(text),
            lastModifiedUtc: null,
            contentLifetime: DocumentContentLifetime.UntilOperationCompletes);
        return true;
    }
}

internal sealed class MemoryDocumentSource : IDocumentSource
{
    private readonly SubmittedDocument[] _documents;
    private int _index;

    public MemoryDocumentSource(SubmittedDocument[] documents)
    {
        _documents = documents;
    }

    public bool TryReadNext(out SubmittedDocument document)
    {
        if (_index >= _documents.Length)
        {
            document = default;
            return false;
        }

        var next = _documents[_index++];
        document = new SubmittedDocument(
            next.Path,
            next.Content,
            next.LastModifiedUtc,
            DocumentContentLifetime.UntilOperationCompletes);
        return true;
    }
}

internal sealed class NullCollector : IHitCollector
{
    public bool OnHit(in RawHit hit) => true;
}

internal sealed class FirstHitCollector : IHitCollector
{
    public bool HasHit { get; private set; }
    public RawHit Hit { get; private set; }

    public bool OnHit(in RawHit hit)
    {
        if (!HasHit)
        {
            Hit = hit;
            HasHit = true;
        }

        return true;
    }
}

internal sealed class RealQueryPack
{
    public string Id { get; init; } = "real-default";
    public RealQueryDefinition[] Queries { get; init; } = [];

    public RealQueryPack Filter(string queryId)
    {
        var filtered = Queries.Where(query => string.Equals(query.Id, queryId, StringComparison.Ordinal)).ToArray();
        if (filtered.Length == 0)
        {
            throw new ArgumentException($"Unknown real query id '{queryId}'.");
        }

        return new RealQueryPack
        {
            Id = $"{Id}:{queryId}",
            Queries = filtered
        };
    }
}

internal sealed class RealQueryDefinition
{
    public string Id { get; init; } = string.Empty;
    public string Pattern { get; init; } = string.Empty;
    public string PatternMode { get; init; } = "Literal";
    public string CaseMode { get; init; } = "Smart";
    public bool WholeWord { get; init; }
    public bool WholeLine { get; init; }
    public bool Multiline { get; init; }
    public bool DotAll { get; init; }
    public string? PathPrefix { get; init; }
    public string[]? IncludeGlobs { get; init; }
    public string[]? ExcludeGlobs { get; init; }
    public string? PathRegex { get; init; }
    public string Intent { get; init; } = string.Empty;

    public SearchQuery ToSearchQuery()
    {
        return new SearchQuery(
            Pattern,
            Enum.Parse<PatternMode>(PatternMode, ignoreCase: true),
            Enum.Parse<CaseMode>(CaseMode, ignoreCase: true))
        {
            WholeWord = WholeWord,
            WholeLine = WholeLine,
            Multiline = Multiline,
            DotAll = DotAll,
            MaxHits = int.MaxValue,
            MaxMatchedDocuments = int.MaxValue,
            MaxBytesScanned = long.MaxValue
        };
    }

    public PathFilter ToPathFilter()
    {
        return new PathFilter
        {
            PathPrefix = PathPrefix,
            IncludeGlobs = IncludeGlobs ?? [],
            ExcludeGlobs = ExcludeGlobs ?? [],
            PathRegex = PathRegex
        };
    }
}

internal readonly record struct DiscoveredCorpus(string Id, string Path);
internal readonly record struct CachedCorpusIndex(string CorpusId, string CachePath);
