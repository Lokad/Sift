using System.Diagnostics;
using System.Text;
using Lokad.Sift;
using Lokad.Sift.LowLevel;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Indexer;

internal sealed class IndexBuilder
{
    private delegate ValueTask<StreamedDocuments> BuildDocumentStreamer(
        Action<List<StoredDocument>> preparePartition,
        Action<IReadOnlyList<StoredDocument>> publishPartition);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal const int DefaultMaxDocumentBytes = 32 * 1024 * 1024;
    internal const int DefaultTargetSegmentDocumentCount = 50_000;
    internal const long DefaultTargetSegmentContentBytes = 64L * 1024 * 1024;
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
        RejectedDocument[] rejectedDocuments;
        try
        {
            load = await streamDocuments(
                static _ => { },
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

            rejectedDocuments = load.RejectedDocuments.ToArray();
            var pendingStats = new BuildStats(
                submittedDocuments: load.Submitted,
                indexedDocuments: load.Accepted,
                rejectedDocuments: load.Rejected,
                segmentCount: entries.Count,
                submittedBytes: load.SubmittedBytes,
                contentBytes: load.AcceptedBytes,
                rejectedBytes: load.RejectedBytes,
                elapsedMilliseconds: watch.ElapsedMilliseconds,
                peakManagedBytes: 0,
                rejectedDocumentDetails: rejectedDocuments,
                committedIdentity: default);
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
            submittedDocuments: load.Submitted,
            indexedDocuments: load.Accepted,
            rejectedDocuments: load.Rejected,
            segmentCount: entries.Count,
            submittedBytes: load.SubmittedBytes,
            contentBytes: load.AcceptedBytes,
            rejectedBytes: load.RejectedBytes,
            elapsedMilliseconds: watch.ElapsedMilliseconds,
            peakManagedBytes: 0,
            rejectedDocumentDetails: rejectedDocuments,
            committedIdentity: new IndexIdentity(
                corpusId,
                new ContentRevision(1),
                generation));
    }

    public static StreamedDocuments StreamDocuments(
        IDocumentSource source,
        CancellationToken cancellationToken,
        SiftOptions options,
        string operationName,
        IReadOnlySet<string>? forbiddenPaths,
        RejectedUpsertPolicy rejectedUpsertPolicy,
        bool collectAcceptedPaths,
        Action<List<StoredDocument>> preparePartition,
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
        Action<List<StoredDocument>> preparePartition,
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
        Action<List<StoredDocument>> preparePartition,
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
        Action<List<StoredDocument>> preparePartition,
        Action<IReadOnlyList<StoredDocument>> publishPartition)
    {
        private readonly HashSet<string> _submittedPaths = new(StringComparer.Ordinal);
        private readonly StoredDocumentPartitioner _partitioner = new(
            options,
            collectAcceptedPaths,
            materializeDocuments: true,
            preparePartition,
            publishPartition);
        private readonly List<RejectedDocument> _rejectedDocuments = [];
        private int _submitted;
        private long _submittedBytes;
        private int _rejected;
        private long _rejectedBytes;

        public void Add(SubmittedDocument submittedDocument)
        {
            _submitted++;
            _submittedBytes = checked(_submittedBytes + submittedDocument.Content.Length);

            var document = Normalize(
                submittedDocument,
                options,
                retainContentBeyondOperation: false,
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

            if (document is null)
            {
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

                _rejected++;
                _rejectedBytes = checked(_rejectedBytes + submittedDocument.Content.Length);
                _rejectedDocuments.Add(rejection);
                return;
            }

            _partitioner.Add(document);
        }

        public StreamedDocuments Complete()
        {
            var partitioning = _partitioner.Complete();
            return new StreamedDocuments(
                _submitted,
                partitioning.Accepted,
                partitioning.Skipped,
                _rejected,
                _submittedBytes,
                partitioning.AcceptedBytes,
                partitioning.SkippedBytes,
                _rejectedBytes,
                partitioning.AcceptedPaths,
                _rejectedDocuments);
        }
    }

    private sealed class StoredDocumentPartitioner(
        SiftOptions options,
        bool collectAcceptedPaths,
        bool materializeDocuments,
        Action<List<StoredDocument>> preparePartition,
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
            preparePartition(_partition);
            _partitionBytes = _partition.Sum(static document => (long)document.Content.Length);
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
        RejectedUpsertPolicy rejectedUpsertPolicy,
        bool retainContentBeyondOperation)
    {
        var submitted = 0;
        long submittedBytes = 0;
        var rejected = 0;
        long rejectedBytes = 0;
        var rejectedDocuments = new List<RejectedDocument>();
        var documents = new List<StoredDocument>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);

        while (source.TryReadNext(out var submittedDocument))
        {
            cancellationToken.ThrowIfCancellationRequested();
            submitted++;
            submittedBytes += submittedDocument.Content.Length;

            var storedDocument = Normalize(
                submittedDocument,
                options,
                retainContentBeyondOperation,
                out var normalizedPath,
                out var rejectionReason);
            if (!seenPaths.Add(normalizedPath))
            {
                throw new InvalidOperationException($"Duplicate canonical path in build batch: '{normalizedPath}'.");
            }

            if (storedDocument is null)
            {
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

                rejected++;
                rejectedBytes += submittedDocument.Content.Length;
                rejectedDocuments.Add(rejection);
                continue;
            }

            Materialize(storedDocument);
            documents.Add(storedDocument);
        }

        return new LoadedDocuments(
            submitted,
            submittedBytes,
            rejected,
            rejectedBytes,
            documents,
            rejectedDocuments);
    }

    public static List<List<StoredDocument>> PartitionDocuments(IReadOnlyList<StoredDocument> documents)
    {
        return PartitionDocuments(documents, new SiftOptions());
    }

    public static List<List<StoredDocument>> PartitionDocuments(
        IReadOnlyList<StoredDocument> documents,
        SiftOptions options)
    {
        var partitions = new List<List<StoredDocument>>();
        if (documents.Count == 0)
        {
            return partitions;
        }

        var current = new List<StoredDocument>();
        long currentBytes = 0;
        foreach (var document in documents)
        {
            var nextBytes = currentBytes + document.Content.Length;
            if (current.Count > 0 &&
                (current.Count >= options.TargetSegmentDocumentCount || nextBytes > options.TargetSegmentContentBytes))
            {
                partitions.Add(current);
                current = new List<StoredDocument>();
                currentBytes = 0;
            }

            current.Add(document);
            currentBytes += document.Content.Length;
        }

        if (current.Count > 0)
        {
            partitions.Add(current);
        }

        return partitions;
    }

    private static StoredDocument? Normalize(
        SubmittedDocument document,
        SiftOptions options,
        bool retainContentBeyondOperation,
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
        if (LooksBinary(span))
        {
            rejectionReason = DocumentRejectionReason.BinaryContent;
            return null;
        }

        var hasBom = span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF;
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
        if (span.IndexOfAnyExceptInRange((byte)0x00, (byte)0x7F) < 0)
        {
            flags |= SegmentDocumentFlags.Ascii;
        }

        if (hasBom)
        {
            flags |= SegmentDocumentFlags.HadUtf8Bom;
        }

        // Buffers borrowed until the next read always require a copy. Whole-operation
        // buffers can flow through unless the caller will retain them afterward.
        var ownedContent = document.ContentLifetime == DocumentContentLifetime.UntilOperationCompletes &&
            !retainContentBeyondOperation
            ? content
            : content.ToArray();
        // Index-only metadata stays empty until unchanged update candidates are removed.
        return new StoredDocument
        {
            Path = normalizedPath,
            PathUtf8 = [],
            Content = ownedContent,
            LineCount = 0,
            EncodedLineIndex = [],
            Flags = flags,
            MTimeUtcTicks = document.LastModifiedUtc?.UtcTicks ?? 0,
            ContentHash64 = Hashing.Fnv1A64(span)
        };

        static bool LooksBinary(ReadOnlySpan<byte> content)
        {
            if (content.IndexOf((byte)0) >= 0)
            {
                return true;
            }

            var sampleLength = Math.Min(content.Length, 4096);
            if (sampleLength == 0)
            {
                return false;
            }

            var suspicious = 0;
            for (var i = 0; i < sampleLength; i++)
            {
                var current = content[i];
                if (current < 0x20 && current is not (byte)'\r' and not (byte)'\n' and not (byte)'\t' and not (byte)'\f')
                {
                    suspicious++;
                }
            }

            return suspicious >= 16 && suspicious * 5 >= sampleLength;
        }
    }

    private static void Materialize(StoredDocument document)
    {
        var encodedLineIndex = LineMap.EncodeLineIndex(document.Content.Span, out var lineCount);
        document.SetIndexMetadata(
            StrictUtf8.GetBytes(document.Path),
            lineCount,
            encodedLineIndex);
    }

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

