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


internal enum BenchmarkMode
{
    Synthetic,
    RealCorpus,
    UpdateBench,
    CompactBench,
    PostingsMicro,
    PostingsStats,
    PostingsBdn
}

internal readonly record struct BenchmarkResult(
    string Name,
    double MinMilliseconds,
    double MedianMilliseconds,
    double P95Milliseconds,
    int Iterations);

internal readonly record struct BenchmarkRun(
    int DocumentCount,
    int WarmupIterations,
    int MeasurementIterations,
    BenchmarkResult[] Results);

internal readonly record struct PostingsStatsRun(
    string ToolVersion,
    DateTimeOffset TimestampUtc,
    PostingsStatsCorpusResult[] Corpora);

internal readonly record struct MachineInfo(
    string Os,
    string Architecture,
    string Cpu,
    int LogicalCores,
    string Runtime);

internal readonly record struct RealCorpusBenchmarkRun(
    string ToolVersion,
    DateTimeOffset TimestampUtc,
    MachineInfo Machine,
    string QueryPackId,
    int? MaxFiles,
    RealCorpusBenchmarkResult[] Corpora);

internal readonly record struct UpdateBenchmarkRun(
    string ToolVersion,
    DateTimeOffset TimestampUtc,
    MachineInfo Machine,
    int? MaxFiles,
    UpdateBenchmarkCorpusResult[] Corpora);

internal readonly record struct CompactBenchmarkRun(
    string ToolVersion,
    DateTimeOffset TimestampUtc,
    MachineInfo Machine,
    int? MaxFiles,
    CompactBenchmarkCorpusResult[] Corpora);

internal readonly record struct RealCorpusBenchmarkResult(
    string Id,
    string Path,
    RealBuildMetrics Build,
    RealSnapshotMetrics Snapshot,
    RealQueryResult[] Queries,
    RealCorpusPlannerSummary PlannerSummary);

internal readonly record struct UpdateBenchmarkCorpusResult(
    string Id,
    string Path,
    RealBuildMetrics Build,
    UpdateScenarioResult[] Scenarios);

internal readonly record struct CompactBenchmarkCorpusResult(
    string Id,
    string Path,
    RealBuildMetrics Build,
    CompactScenarioResult[] Scenarios);

internal readonly record struct UpdateScenarioResult(
    string Id,
    int UpsertCount,
    int DeleteCount,
    QueryLatencyMetrics Latency,
    UpdateScenarioMetrics Metrics);

internal readonly record struct CompactScenarioResult(
    string Id,
    int ChurnRounds,
    int UpsertCountPerRound,
    int DeleteCountPerRound,
    QueryLatencyMetrics Latency,
    CompactScenarioMetrics Metrics);

internal readonly record struct UpdateScenarioMetrics(
    int UpsertedDocuments,
    int DeletedDocuments,
    int PublishedSegments,
    long ContentBytes,
    double ContentBytesPerSecond,
    long DeadOrdinalsBefore,
    long DeadOrdinalsAfter,
    int SegmentCountDelta,
    long ManifestBytesBefore,
    long ManifestBytesAfter);

internal readonly record struct CompactScenarioMetrics(
    int InputSegments,
    int OutputSegments,
    int MergedDocuments,
    long ContentBytes,
    double ContentBytesPerSecond,
    double LiveDocumentsPerSecond,
    long DeadOrdinalsBefore,
    long DeadOrdinalsAfter,
    long ManifestBytesBefore,
    long ManifestBytesAfter,
    long PeakManagedBytes,
    long BytesReadFromOldSegments,
    long BytesWrittenToNewSegments);

internal readonly record struct CompactionMeasurement(
    CompactStats Stats,
    long PeakManagedBytes);

internal readonly record struct BuildMeasurement(
    BuildStats Stats,
    long PeakManagedBytes);

internal readonly record struct CachedIndexMetadata(
    string CorpusId,
    string CorpusPath,
    int? MaxFiles,
    DateTimeOffset BuiltAtUtc,
    RealBuildMetrics Build);

internal readonly record struct RealBuildMetrics(
    int SubmittedDocuments,
    int IndexedDocuments,
    int RejectedDocuments,
    long SubmittedBytes,
    long ContentBytesOriginal,
    long IndexBytesOnDisk,
    double IndexInflationRatio,
    double IndexInflationPercent,
    double BuildElapsedMilliseconds,
    double BuildDocsPerSecond,
    double BuildBytesPerSecond,
    long PeakManagedBytes,
    int? MaxFiles,
    int SegmentCount);

internal readonly record struct RealSnapshotMetrics(
    double OpenElapsedMilliseconds,
    long MappedBytesDelta,
    int SegmentCount);

internal readonly record struct PostingsStatsCorpusResult(
    string Id,
    string Path,
    int SegmentCount,
    long TotalPostingLists,
    long TotalEncodedBytes,
    long TotalBp128Bytes,
    long TotalStreamVByteBytes,
    long CheckpointedPostingLists,
    long CheckpointedEncodedBytes,
    double TotalBp128InflationPercent,
    double TotalStreamVByteInflationPercent,
    PostingsStatsBucket[] EncodedByteBuckets,
    PostingsStatsBucket[] DocumentFrequencyBuckets,
    PostingsStatsThresholdSummary[] ThresholdSummaries,
    PostingsStatsRegionSummary[] Regions);

internal readonly record struct PostingsStatsBucket(
    string Key,
    long PostingLists,
    long EncodedBytes,
    double PostingListPercent,
    double EncodedBytesPercent);

internal readonly record struct PostingsStatsThresholdSummary(
    int ThresholdBytes,
    long PostingLists,
    long EncodedBytes,
    long Bp128Bytes,
    long StreamVByteBytes,
    double PostingListPercent,
    double EncodedBytesPercent,
    double Bp128InflationPercent,
    double StreamVByteInflationPercent);

internal readonly record struct PostingsStatsRegionSummary(
    string Region,
    long PostingLists,
    long EncodedBytes,
    long Bp128Bytes,
    long StreamVByteBytes,
    long CheckpointedPostingLists,
    long CheckpointedEncodedBytes,
    double Bp128InflationPercent,
    double StreamVByteInflationPercent);

internal readonly record struct RealQueryResult(
    string Id,
    string Intent,
    QueryLatencyMetrics Latency,
    string PlanFamily,
    string CandidateKind,
    string VerifierKind,
    double EstimatedPathFilterCost,
    double EstimatedDirectScanCost,
    double? EstimatedIndexedCost,
    double EstimatedCandidateDocuments,
    double EstimatedCost,
    string? RunnerUpPlanFamily,
    string? RunnerUpCandidateKind,
    string? RunnerUpVerifierKind,
    double? RunnerUpEstimatedCostDelta,
    RealQueryPlannerAlternative[] PlannerAlternatives,
    int ScopeDocuments,
    long ScopeBytes,
    long PostingsBytesRead,
    int CandidateDocumentsAfterIndex,
    int CandidateDocumentsAfterPrecheck,
    int VerifiedDocuments,
    long BytesScanned,
    long DecodedChars,
    int RegexInvocations,
    int Utf8RegexInvocations,
    int FallbackRegexInvocations,
    int Utf8RegexMatchedDocuments,
    long Utf8RegexMatchesEnumerated,
    long Utf8RegexCacheHits,
    long Utf8RegexCacheMisses,
    int HitsReturned,
    string StopReason,
    bool TimedOut);

internal readonly record struct RealQueryPlannerAlternative(
    string PlanFamily,
    string CandidateKind,
    string VerifierKind,
    bool UsesIndex,
    double EstimatedDirectScanCost,
    double? EstimatedIndexedCost,
    double EstimatedCandidateDocuments,
    double EstimatedCost,
    int SegmentCount);

internal readonly record struct QueryLatencyMetrics(
    double MinMilliseconds,
    double MedianMilliseconds,
    double P95Milliseconds,
    int Iterations);

internal readonly record struct CachedIndexState(
    string CachePath,
    CachedIndexMetadata Metadata);

internal readonly record struct UpdateScenarioDefinition(
    string Id,
    int UpsertCount,
    int DeleteCount,
    int StartOffset);

internal readonly record struct CompactScenarioDefinition(
    string Id,
    int UpsertCountPerRound,
    int DeleteCountPerRound,
    int ChurnRounds,
    int StartOffset);

internal readonly record struct CorpusFileEntry(
    string RelativePath,
    byte[] Content,
    DateTimeOffset LastModifiedUtc);

internal readonly record struct ScenarioFiles(
    SubmittedDocument[] Upserts,
    DocumentKey[] Deletions);

internal readonly record struct RealCorpusPlannerSummary(
    PlannerCountEntry[] PlanFamilies,
    PlannerCountEntry[] CandidateKinds,
    PlannerCountEntry[] VerifierKinds,
    PlannerSlowQuerySummary[] SlowQueries,
    PlannerRunnerUpSummary[] RunnerUpLosses,
    PlannerMispredictionSummary[] WorstUnderestimates,
    PlannerMispredictionSummary[] WorstOverestimates,
    PlannerFamilyLatencySummary[] FamilyLatency);

internal readonly record struct PlannerCountEntry(
    string Key,
    int Count);

internal readonly record struct PlannerSlowQuerySummary(
    string Id,
    double MedianMilliseconds,
    string PlanFamily,
    string CandidateKind,
    string VerifierKind,
    int ScopeDocuments,
    int VerifiedDocuments,
    long BytesScanned);

internal readonly record struct PlannerRunnerUpSummary(
    string PlanFamily,
    int Count,
    double AverageEstimatedCostDelta);

internal readonly record struct PlannerMispredictionSummary(
    string Id,
    double MedianMilliseconds,
    double EstimatedCost,
    double Ratio,
    string PlanFamily,
    string CandidateKind);

internal readonly record struct PlannerFamilyLatencySummary(
    string PlanFamily,
    int QueryCount,
    double MedianLatencyMilliseconds,
    double P95LatencyMilliseconds,
    long TotalBytesScanned,
    int TotalVerifiedDocuments);

internal readonly record struct PlannerRunnerUpCandidate(
    string PlanFamily,
    double EstimatedCostDelta);
