using System.Diagnostics;
using System.Text;
using Lokad.Sift;
using Lokad.Sift.LowLevel;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Indexer;

internal delegate long PrepareDocumentPartition(
    List<StoredDocument> documents,
    long contentBytes);

internal sealed class IndexBuilder
{
    private delegate ValueTask<StreamedDocuments> BuildDocumentStreamer(
        PrepareDocumentPartition preparePartition,
        Action<IReadOnlyList<StoredDocument>> publishPartition);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ISiftStorage _storage;
    private readonly SiftOptions _options;

    public IndexBuilder(ISiftStorage storage, SiftOptions options)
    {
        _storage = storage;
        _options = options;
    }

    public ValueTask<BuildStats> BuildAsync(
        IDocumentSource source,
        BuildOptions options,
        bool discardCorruptState,
        CancellationToken cancellationToken)
    {
        return BuildCoreAsync(
            (preparePartition, publishPartition) => new ValueTask<StreamedDocuments>(StreamDocuments(
                    source,
                    cancellationToken,
                    _options,
                    operationName: "build",
                    forbiddenPaths: null,
                    rejectedUpsertPolicy: RejectedUpsertPolicy.PreserveExisting,
                    collectAcceptedPaths: false,
                    preparePartition,
                    publishPartition)),
            discardCorruptState,
            options,
            cancellationToken);
    }

    public ValueTask<BuildStats> BuildAsync(
        IAsyncEnumerable<SubmittedDocument> source,
        BuildOptions options,
        bool discardCorruptState,
        CancellationToken cancellationToken)
    {
        return BuildCoreAsync(
            (preparePartition, publishPartition) => StreamDocumentsAsync(
                source,
                cancellationToken,
                _options,
                operationName: "build",
                forbiddenPaths: null,
                rejectedUpsertPolicy: RejectedUpsertPolicy.PreserveExisting,
                collectAcceptedPaths: false,
                preparePartition,
                publishPartition),
            discardCorruptState,
            options,
            cancellationToken);
    }

    private async ValueTask<BuildStats> BuildCoreAsync(
        BuildDocumentStreamer streamDocuments,
        bool discardCorruptState,
        BuildOptions options,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        using var writeSession = _storage.AcquireWriteSession(cancellationToken);
        writeSession.PrepareFullBuild(discardCorruptState);

        var existing = writeSession.CurrentManifest;
        var corpusId = CorpusId.New();
        var generation = existing?.Generation.Next() ?? new StorageGeneration(1);
        var segmentSeed = generation.Value ^ ((ulong)DateTime.UtcNow.Ticks << 1);
        var entries = new List<SegmentManifestEntry>();
        var attemptedSegmentIds = new List<SegmentId>();
        StreamedDocuments load;
        BuildCommitStats pendingStats;
        try
        {
            load = await streamDocuments(
                static (_, contentBytes) => contentBytes,
                partition =>
                {
                    var segmentId = new SegmentId(segmentSeed + (ulong)entries.Count);
                    attemptedSegmentIds.Add(segmentId);
                    entries.Add(writeSession.PublishSegment(
                        partition,
                        segmentId,
                        _options.MinimumFreeBytesAfterMutation,
                        cancellationToken));
                }).ConfigureAwait(false);

            var rejectedDocuments = load.RejectedDocuments.ToArray();
            pendingStats = new PendingBuildStats(
                submittedDocuments: load.Submitted,
                indexedDocuments: load.Accepted,
                rejectedDocuments: load.Rejected,
                segmentCount: entries.Count,
                submittedBytes: load.SubmittedBytes,
                contentBytes: load.AcceptedBytes,
                rejectedBytes: load.RejectedBytes,
                rejectedDocumentDetails: rejectedDocuments);
            var commitMetadata = options.CommitMetadataProvider?.Invoke(pendingStats).ToArray() ?? [];
            if (commitMetadata.Length > _options.MaxCommitMetadataBytes)
            {
                throw new InvalidOperationException(
                    $"Sift commit metadata exceeds the configured {_options.MaxCommitMetadataBytes} byte limit.");
            }

            writeSession.PublishManifest(new CorpusManifest(
                CorpusId: corpusId,
                ContentRevision: new ContentRevision(1),
                Generation: generation,
                CommitMetadata: commitMetadata,
                Segments: entries));
        }
        catch
        {
            writeSession.CleanupFailedMutation(attemptedSegmentIds);
            throw;
        }

        watch.Stop();

        return new BuildStats(
            pendingStats,
            watch.ElapsedMilliseconds,
            new IndexIdentity(
                corpusId,
                new ContentRevision(1),
                generation));
    }

    private sealed class PendingBuildStats(
        int submittedDocuments,
        int indexedDocuments,
        int rejectedDocuments,
        int segmentCount,
        long submittedBytes,
        long contentBytes,
        long rejectedBytes,
        RejectedDocument[] rejectedDocumentDetails)
        : BuildCommitStats(
            submittedDocuments,
            indexedDocuments,
            rejectedDocuments,
            segmentCount,
            submittedBytes,
            contentBytes,
            rejectedBytes,
            rejectedDocumentDetails);

    public static StreamedDocuments StreamDocuments(
        IDocumentSource source,
        CancellationToken cancellationToken,
        SiftOptions options,
        string operationName,
        IReadOnlySet<string>? forbiddenPaths,
        RejectedUpsertPolicy rejectedUpsertPolicy,
        bool collectAcceptedPaths,
        PrepareDocumentPartition preparePartition,
        Action<IReadOnlyList<StoredDocument>> publishPartition)
    {
        var accumulator = new StreamedDocumentAccumulator(
            options,
            operationName,
            forbiddenPaths,
            rejectedUpsertPolicy,
            collectAcceptedPaths,
            preparePartition,
            publishPartition);

        while (source.TryReadNext(out var submittedDocument))
        {
            cancellationToken.ThrowIfCancellationRequested();
            accumulator.Add(submittedDocument);
        }

        return accumulator.Complete();
    }

    public static async ValueTask<StreamedDocuments> StreamDocumentsAsync(
        IAsyncEnumerable<SubmittedDocument> source,
        CancellationToken cancellationToken,
        SiftOptions options,
        string operationName,
        IReadOnlySet<string>? forbiddenPaths,
        RejectedUpsertPolicy rejectedUpsertPolicy,
        bool collectAcceptedPaths,
        PrepareDocumentPartition preparePartition,
        Action<IReadOnlyList<StoredDocument>> publishPartition)
    {
        var accumulator = new StreamedDocumentAccumulator(
            options,
            operationName,
            forbiddenPaths,
            rejectedUpsertPolicy,
            collectAcceptedPaths,
            preparePartition,
            publishPartition);

        await foreach (var submittedDocument in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            accumulator.Add(submittedDocument);
        }

        return accumulator.Complete();
    }

    public static StreamedDocuments StreamLoadedDocuments(
        LoadedDocuments load,
        CancellationToken cancellationToken,
        SiftOptions options,
        IReadOnlySet<string> forbiddenPaths,
        bool collectAcceptedPaths,
        PrepareDocumentPartition preparePartition,
        Action<IReadOnlyList<StoredDocument>> publishPartition)
    {
        var partitioner = new StoredDocumentPartitioner(
            options,
            collectAcceptedPaths,
            materializeDocuments: false,
            preparePartition,
            publishPartition);
        for (var i = 0; i < load.Documents.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = load.Documents[i];
            if (forbiddenPaths.Contains(document.Path))
            {
                throw new InvalidOperationException(
                    $"Path '{document.Path}' cannot appear in both upserts and deletions.");
            }

            partitioner.Add(document);
        }

        for (var i = 0; i < load.RejectedDocuments.Count; i++)
        {
            var rejected = load.RejectedDocuments[i];
            if (forbiddenPaths.Contains(rejected.Path))
            {
                throw new InvalidOperationException(
                    $"Path '{rejected.Path}' cannot appear in both upserts and deletions.");
            }
        }

        var partitioning = partitioner.Complete();
        return new StreamedDocuments(
            load.Submitted,
            partitioning.Accepted,
            partitioning.Skipped,
            load.Rejected,
            load.SubmittedBytes,
            partitioning.AcceptedBytes,
            partitioning.SkippedBytes,
            load.RejectedBytes,
            partitioning.AcceptedPaths,
            load.RejectedDocuments);
    }

    private sealed class StreamedDocumentAccumulator(
        SiftOptions options,
        string operationName,
        IReadOnlySet<string>? forbiddenPaths,
        RejectedUpsertPolicy rejectedUpsertPolicy,
        bool collectAcceptedPaths,
        PrepareDocumentPartition preparePartition,
        Action<IReadOnlyList<StoredDocument>> publishPartition)
    {
        private readonly DocumentIngestionAccumulator _ingestion = new(
            options,
            operationName,
            forbiddenPaths,
            rejectedUpsertPolicy);
        private readonly StoredDocumentPartitioner _partitioner = new(
            options,
            collectAcceptedPaths,
            materializeDocuments: true,
            preparePartition,
            publishPartition);

        public void Add(SubmittedDocument submittedDocument)
        {
            var document = _ingestion.Add(submittedDocument);
            if (document is not null)
            {
                _partitioner.Add(document);
            }
        }

        public StreamedDocuments Complete()
        {
            var partitioning = _partitioner.Complete();
            return new StreamedDocuments(
                _ingestion.Submitted,
                partitioning.Accepted,
                partitioning.Skipped,
                _ingestion.Rejected,
                _ingestion.SubmittedBytes,
                partitioning.AcceptedBytes,
                partitioning.SkippedBytes,
                _ingestion.RejectedBytes,
                partitioning.AcceptedPaths,
                _ingestion.RejectedDocuments);
        }
    }

    private sealed class DocumentIngestionAccumulator(
        SiftOptions options,
        string operationName,
        IReadOnlySet<string>? forbiddenPaths,
        RejectedUpsertPolicy rejectedUpsertPolicy)
    {
        private readonly HashSet<string> _submittedPaths = new(StringComparer.Ordinal);
        private readonly List<RejectedDocument> _rejectedDocuments = [];

        public int Submitted { get; private set; }
        public long SubmittedBytes { get; private set; }
        public int Rejected { get; private set; }
        public long RejectedBytes { get; private set; }
        public IReadOnlyList<RejectedDocument> RejectedDocuments => _rejectedDocuments;

        public StoredDocument? Add(SubmittedDocument submittedDocument)
        {
            Submitted = checked(Submitted + 1);
            SubmittedBytes = checked(SubmittedBytes + submittedDocument.Content.Length);
            var document = Normalize(
                submittedDocument,
                options,
                out var normalizedPath,
                out var rejectionReason);
            if (!_submittedPaths.Add(normalizedPath))
            {
                throw new InvalidOperationException(
                    $"Duplicate canonical path in {operationName} batch: '{normalizedPath}'.");
            }

            if (forbiddenPaths?.Contains(normalizedPath) == true)
            {
                throw new InvalidOperationException(
                    $"Path '{normalizedPath}' cannot appear in both upserts and deletions.");
            }

            if (document is not null)
            {
                return document;
            }

            var rejection = new RejectedDocument(
                normalizedPath,
                rejectionReason,
                submittedDocument.Content.Length);
            if (rejectedUpsertPolicy == RejectedUpsertPolicy.FailUpdate)
            {
                throw new RejectedUpsertException(
                    rejectedUpserts: 1,
                    rejectedBytes: submittedDocument.Content.Length,
                    [rejection]);
            }

            Rejected = checked(Rejected + 1);
            RejectedBytes = checked(RejectedBytes + submittedDocument.Content.Length);
            _rejectedDocuments.Add(rejection);
            return null;
        }
    }

    private sealed class StoredDocumentPartitioner(
        SiftOptions options,
        bool collectAcceptedPaths,
        bool materializeDocuments,
        PrepareDocumentPartition preparePartition,
        Action<IReadOnlyList<StoredDocument>> publishPartition)
    {
        private readonly HashSet<string>? _acceptedPaths = collectAcceptedPaths
            ? new HashSet<string>(StringComparer.Ordinal)
            : null;
        private List<StoredDocument> _partition = [];
        private int _accepted;
        private long _acceptedBytes;
        private int _skipped;
        private long _skippedBytes;
        private long _partitionBytes;

        public void Add(StoredDocument document)
        {
            var nextPartitionBytes = checked(_partitionBytes + document.Content.Length);
            if (_partition.Count > 0 &&
                (_partition.Count >= options.TargetSegmentDocumentCount ||
                 nextPartitionBytes > options.TargetSegmentContentBytes))
            {
                FlushPartition();
            }

            _partition.Add(document);
            _partitionBytes += document.Content.Length;
        }

        public PartitioningStats Complete()
        {
            FlushPartition();
            return new PartitioningStats(
                _accepted,
                _skipped,
                _acceptedBytes,
                _skippedBytes,
                _acceptedPaths ?? []);
        }

        private void FlushPartition()
        {
            if (_partition.Count == 0)
            {
                return;
            }

            _partition.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
            var candidateCount = _partition.Count;
            var candidateBytes = _partitionBytes;
            _partitionBytes = preparePartition(_partition, _partitionBytes);
            _skipped += candidateCount - _partition.Count;
            _skippedBytes = checked(_skippedBytes + candidateBytes - _partitionBytes);
            _accepted += _partition.Count;
            _acceptedBytes = checked(_acceptedBytes + _partitionBytes);
            if (_acceptedPaths is not null)
            {
                foreach (var document in _partition)
                {
                    _acceptedPaths.Add(document.Path);
                }
            }

            if (_partition.Count > 0)
            {
                if (materializeDocuments)
                {
                    for (var i = 0; i < _partition.Count; i++)
                    {
                        Materialize(_partition[i]);
                    }
                }

                publishPartition(_partition);
            }

            _partition = [];
            _partitionBytes = 0;
        }
    }

    public static LoadedDocuments LoadDocuments(
        IDocumentSource source,
        CancellationToken cancellationToken,
        SiftOptions options,
        string operationName,
        RejectedUpsertPolicy rejectedUpsertPolicy)
    {
        var ingestion = new DocumentIngestionAccumulator(
            options,
            operationName,
            forbiddenPaths: null,
            rejectedUpsertPolicy);
        var documents = new List<StoredDocument>();

        while (source.TryReadNext(out var submittedDocument))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = ingestion.Add(submittedDocument);
            if (document is not null)
            {
                Materialize(document);
                documents.Add(document);
            }
        }

        return new LoadedDocuments(
            ingestion.Submitted,
            ingestion.SubmittedBytes,
            ingestion.Rejected,
            ingestion.RejectedBytes,
            documents,
            ingestion.RejectedDocuments);
    }

    private static StoredDocument? Normalize(
        SubmittedDocument document,
        SiftOptions options,
        out string normalizedPath,
        out DocumentRejectionReason rejectionReason)
    {
        normalizedPath = document.Path;
        rejectionReason = default;
        var content = document.Content;
        if (content.Length > options.MaxDocumentBytes)
        {
            rejectionReason = DocumentRejectionReason.MaximumDocumentBytesExceeded;
            return null;
        }

        var span = content.Span;
        var hasBom = span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF;
        var analysis = AnalyzeDocument(span, hasBom ? 3 : 0);
        if (analysis.LooksBinary)
        {
            rejectionReason = DocumentRejectionReason.BinaryContent;
            return null;
        }

        if (hasBom)
        {
            content = content[3..];
            span = content.Span;
        }

        try
        {
            _ = StrictUtf8.GetCharCount(span);
        }
        catch (DecoderFallbackException)
        {
            rejectionReason = DocumentRejectionReason.InvalidUtf8;
            return null;
        }

        var flags = SegmentDocumentFlags.Utf8Text;
        if (analysis.IsAscii)
        {
            flags |= SegmentDocumentFlags.Ascii;
        }

        if (hasBom)
        {
            flags |= SegmentDocumentFlags.HadUtf8Bom;
        }

        // Buffers borrowed until the next read require a copy. Whole-operation
        // buffers remain borrowed only until publication completes.
        var ownedContent = document.ContentLifetime == DocumentContentLifetime.UntilOperationCompletes
            ? content
            : content.ToArray();
        // Index-only metadata stays empty until unchanged update candidates are removed.
        return new StoredDocument
        {
            Path = normalizedPath,
            PathUtf8 = [],
            Content = ownedContent,
            LineCount = analysis.LineCount,
            EncodedLineIndex = [],
            Flags = flags,
            MTimeUtcTicks = document.LastModifiedUtc?.UtcTicks ?? 0,
            ContentHash64 = Hashing.Fnv1A64(span)
        };
    }

    private static DocumentAnalysis AnalyzeDocument(
        ReadOnlySpan<byte> submittedContent,
        int contentOffset)
    {
        // Binary detection, ASCII classification, and line counting share one byte pass.
        var sampleLength = Math.Min(submittedContent.Length, 4096);
        var hasNull = false;
        var isAscii = true;
        var suspicious = 0;
        var lineCount = 1;
        for (var i = 0; i < submittedContent.Length; i++)
        {
            var current = submittedContent[i];
            hasNull |= current == 0;
            if (i < sampleLength &&
                current < 0x20 &&
                current is not (byte)'\r' and not (byte)'\n' and not (byte)'\t' and not (byte)'\f')
            {
                suspicious++;
            }

            if (i < contentOffset)
            {
                continue;
            }

            isAscii &= current <= 0x7F;
            if (current == (byte)'\n')
            {
                lineCount++;
            }
        }

        var looksBinary = hasNull ||
            suspicious >= 16 && suspicious * 5 >= sampleLength;
        return new DocumentAnalysis(looksBinary, isAscii, lineCount);
    }

    private static void Materialize(StoredDocument document)
    {
        var encodedLineIndex = LineMap.EncodeLineIndex(
            document.Content.Span,
            document.LineCount);
        document.SetIndexMetadata(
            StrictUtf8.GetBytes(document.Path),
            document.LineCount,
            encodedLineIndex);
    }

    private readonly record struct DocumentAnalysis(
        bool LooksBinary,
        bool IsAscii,
        int LineCount);

    private readonly record struct PartitioningStats(
        int Accepted,
        int Skipped,
        long AcceptedBytes,
        long SkippedBytes,
        HashSet<string> AcceptedPaths);

}

internal readonly record struct LoadedDocuments(
    int Submitted,
    long SubmittedBytes,
    int Rejected,
    long RejectedBytes,
    List<StoredDocument> Documents,
    IReadOnlyList<RejectedDocument> RejectedDocuments);

internal readonly record struct StreamedDocuments(
    int Submitted,
    int Accepted,
    int Skipped,
    int Rejected,
    long SubmittedBytes,
    long AcceptedBytes,
    long SkippedBytes,
    long RejectedBytes,
    HashSet<string> AcceptedPaths,
    IReadOnlyList<RejectedDocument> RejectedDocuments);

