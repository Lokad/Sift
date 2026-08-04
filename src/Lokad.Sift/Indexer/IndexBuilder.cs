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
        using var _ = _storage.AcquireWriterLock(cancellationToken);
        _storage.PrepareFullBuildUnderWriterLock(discardCorruptState);

        var existing = _storage.HasManifest ? _storage.ReadManifest() : null;
        var corpusId = Guid.NewGuid();
        var generation = (existing?.Generation ?? 0UL) + 1UL;
        var segmentSeed = generation ^ ((ulong)DateTime.UtcNow.Ticks << 1);
        var entries = new List<SegmentManifestEntry>();
        var attemptedSegmentIds = new List<ulong>();
        StreamedDocuments load;
        try
        {
            load = await streamDocuments(
                static _ => { },
                partition =>
                {
                    var segmentId = segmentSeed + (ulong)entries.Count;
                    attemptedSegmentIds.Add(segmentId);
                    entries.Add(_storage.PublishSegment(
                        partition,
                        segmentId,
                        _options.MinimumFreeBytesAfterMutation,
                        cancellationToken));
                }).ConfigureAwait(false);

            var pendingStats = new BuildStats(
                SubmittedDocuments: load.Submitted,
                IndexedDocuments: load.Accepted,
                RejectedDocuments: load.Rejected,
                SegmentCount: entries.Count,
                SubmittedBytes: load.SubmittedBytes,
                ContentBytes: load.AcceptedBytes,
                RejectedBytes: load.RejectedBytes,
                ElapsedMilliseconds: watch.ElapsedMilliseconds,
                PeakManagedBytes: 0)
            {
                RejectedDocumentDetails = load.RejectedDocuments
            };
            var commitMetadata = options.CommitMetadataProvider?.Invoke(pendingStats).ToArray() ?? [];
            if (commitMetadata.Length > _options.MaxCommitMetadataBytes)
            {
                throw new InvalidOperationException(
                    $"Sift commit metadata exceeds the configured {_options.MaxCommitMetadataBytes} byte limit.");
            }

            _storage.PublishManifest(new CorpusManifest(
                CorpusId: corpusId,
                ContentRevision: 1,
                Generation: generation,
                CommitMetadata: commitMetadata,
                Segments: entries));
        }
        catch
        {
            _storage.CleanupFailedMutationUnderWriterLock(attemptedSegmentIds);
            throw;
        }

        watch.Stop();

        return new BuildStats(
            SubmittedDocuments: load.Submitted,
            IndexedDocuments: load.Accepted,
            RejectedDocuments: load.Rejected,
            SegmentCount: entries.Count,
            SubmittedBytes: load.SubmittedBytes,
            ContentBytes: load.AcceptedBytes,
            RejectedBytes: load.RejectedBytes,
            ElapsedMilliseconds: watch.ElapsedMilliseconds,
            PeakManagedBytes: 0)
        {
            RejectedDocumentDetails = load.RejectedDocuments,
            CommittedIdentity = new IndexIdentity(
                corpusId,
                ContentRevision: 1,
                StorageGeneration: generation)
        };
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
        private readonly HashSet<string>? _acceptedPaths = collectAcceptedPaths
            ? new HashSet<string>(StringComparer.Ordinal)
            : null;
        private readonly List<RejectedDocument> _rejectedDocuments = [];
        private List<StoredDocument> _partition = [];
        private int _submitted;
        private long _submittedBytes;
        private int _accepted;
        private long _acceptedBytes;
        private int _skipped;
        private long _skippedBytes;
        private int _rejected;
        private long _rejectedBytes;
        private long _partitionBytes;

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

        public StreamedDocuments Complete()
        {
            FlushPartition();
            return new StreamedDocuments(
                _submitted,
                _accepted,
                _skipped,
                _rejected,
                _submittedBytes,
                _acceptedBytes,
                _skippedBytes,
                _rejectedBytes,
                _acceptedPaths ?? [],
                _rejectedDocuments);
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
                for (var i = 0; i < _partition.Count; i++)
                {
                    Materialize(_partition[i]);
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
        long acceptedBytes = 0;
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
            acceptedBytes += storedDocument.Content.Length;
        }

        return new LoadedDocuments(
            submitted,
            submittedBytes,
            rejected,
            rejectedBytes,
            acceptedBytes,
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
        normalizedPath = CanonicalizePath(document.Path);
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

        var flags = (ushort)1;
        if (span.IndexOfAnyExceptInRange((byte)0x00, (byte)0x7F) < 0)
        {
            flags |= 1 << 1;
        }

        if (hasBom)
        {
            flags |= 1 << 2;
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

    public static string CanonicalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        path = path.Replace('\\', '/');
        if (path[0] == '/' ||
            path.Length >= 2 &&
            path[1] == ':' &&
            path[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
        {
            throw new ArgumentException("Document path must be relative.", nameof(path));
        }

        var segmentStart = 0;
        for (var i = 0; i <= path.Length; i++)
        {
            if (i < path.Length && path[i] != '/')
            {
                continue;
            }

            var segment = path.AsSpan(segmentStart, i - segmentStart);
            if (segment.Length == 0 || segment.SequenceEqual(".") || segment.SequenceEqual(".."))
            {
                throw new ArgumentException("Document path contains invalid segments.", nameof(path));
            }

            segmentStart = i + 1;
        }

        return path;
    }
}

internal readonly record struct LoadedDocuments(
    int Submitted,
    long SubmittedBytes,
    int Rejected,
    long RejectedBytes,
    long AcceptedBytes,
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

