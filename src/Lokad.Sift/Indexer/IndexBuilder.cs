using System.Diagnostics;
using System.Text;
using Lokad.Sift;
using Lokad.Sift.LowLevel;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Indexer;

internal sealed class IndexBuilder
{
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

    public BuildStats Build(
        IDocumentSource source,
        bool discardCorruptState,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        using var _ = _storage.AcquireWriterLock(cancellationToken);
        _storage.PrepareFullBuildUnderWriterLock(discardCorruptState);

        var existing = _storage.HasManifest ? _storage.ReadManifest() : null;
        var corpusId = existing?.CorpusId ?? Guid.NewGuid();
        var generation = (existing?.Generation ?? 0UL) + 1UL;
        var segmentSeed = generation ^ ((ulong)DateTime.UtcNow.Ticks << 1);
        var entries = new List<SegmentManifestEntry>();
        var attemptedSegmentIds = new List<ulong>();
        StreamedDocuments load;
        try
        {
            load = StreamDocuments(
                source,
                cancellationToken,
                _options,
                operationName: "build",
                forbiddenPaths: null,
                partition =>
                {
                    var segmentId = segmentSeed + (ulong)entries.Count;
                    attemptedSegmentIds.Add(segmentId);
                    entries.Add(_storage.PublishSegment(
                        partition,
                        segmentId,
                        _options.MinimumFreeBytesAfterMutation,
                        cancellationToken));
                });

            _storage.PublishManifest(new CorpusManifest(corpusId, generation, entries));
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
            PeakManagedBytes: 0);
    }

    public static StreamedDocuments StreamDocuments(
        IDocumentSource source,
        CancellationToken cancellationToken,
        SiftOptions options,
        string operationName,
        IReadOnlySet<string>? forbiddenPaths,
        Action<IReadOnlyList<StoredDocument>> publishPartition)
    {
        var submitted = 0;
        long submittedBytes = 0;
        var accepted = 0;
        long acceptedBytes = 0;
        var rejected = 0;
        long rejectedBytes = 0;
        var submittedPaths = new HashSet<string>(StringComparer.Ordinal);
        var acceptedPaths = new HashSet<string>(StringComparer.Ordinal);
        var partition = new List<StoredDocument>();
        long partitionBytes = 0;

        void FlushPartition()
        {
            if (partition.Count == 0)
            {
                return;
            }

            partition.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
            publishPartition(partition);
            partition = [];
            partitionBytes = 0;
        }

        while (source.TryReadNext(out var submittedDocument))
        {
            cancellationToken.ThrowIfCancellationRequested();
            submitted++;
            submittedBytes = checked(submittedBytes + submittedDocument.Content.Length);

            var document = Normalize(
                submittedDocument,
                options,
                out var normalizedPath);
            if (!submittedPaths.Add(normalizedPath))
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
                rejected++;
                rejectedBytes = checked(rejectedBytes + submittedDocument.Content.Length);
                continue;
            }

            var nextPartitionBytes = checked(partitionBytes + document.Content.Length);
            if (partition.Count > 0 &&
                (partition.Count >= options.TargetSegmentDocumentCount ||
                 nextPartitionBytes > options.TargetSegmentContentBytes))
            {
                FlushPartition();
            }

            partition.Add(document);
            partitionBytes += document.Content.Length;
            acceptedPaths.Add(normalizedPath);
            accepted++;
            acceptedBytes = checked(acceptedBytes + document.Content.Length);
        }

        FlushPartition();
        return new StreamedDocuments(
            submitted,
            accepted,
            rejected,
            submittedBytes,
            acceptedBytes,
            rejectedBytes,
            acceptedPaths);
    }

    public static LoadedDocuments LoadDocuments(
        IDocumentSource source,
        CancellationToken cancellationToken,
        SiftOptions options)
    {
        var submitted = 0;
        long submittedBytes = 0;
        var rejected = 0;
        long rejectedBytes = 0;
        long acceptedBytes = 0;
        var documents = new List<StoredDocument>();
        var rejectedPaths = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);

        while (source.TryReadNext(out var submittedDocument))
        {
            cancellationToken.ThrowIfCancellationRequested();
            submitted++;
            submittedBytes += submittedDocument.Content.Length;

            var storedDocument = Normalize(submittedDocument, options, out var normalizedPath);
            if (!seenPaths.Add(normalizedPath))
            {
                throw new InvalidOperationException($"Duplicate canonical path in build batch: '{normalizedPath}'.");
            }

            if (storedDocument is null)
            {
                rejected++;
                rejectedBytes += submittedDocument.Content.Length;
                rejectedPaths.Add(normalizedPath);
                continue;
            }

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
            rejectedPaths);
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
        out string normalizedPath)
    {
        normalizedPath = CanonicalizePath(document.Path);
        var content = document.Content;
        if (content.Length > options.MaxDocumentBytes)
        {
            return null;
        }

        var span = content.Span;
        if (LooksBinary(span))
        {
            return null;
        }

        var hasBom = span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF });
        if (hasBom)
        {
            content = content[3..];
            span = content.Span;
        }

        try
        {
            _ = new UTF8Encoding(false, true).GetCharCount(span);
        }
        catch (DecoderFallbackException)
        {
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

        var lineStarts = LineMap.BuildLineStarts(span);
        var ownedContent = content.ToArray();
        return new StoredDocument
        {
            Path = normalizedPath,
            PathUtf8 = Encoding.UTF8.GetBytes(normalizedPath),
            Content = ownedContent,
            LineCount = lineStarts.Length,
            EncodedLineIndex = LineMap.EncodeLineIndex(lineStarts),
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

    public static string CanonicalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        path = path.Replace('\\', '/');
        if (Path.IsPathRooted(path))
        {
            throw new ArgumentException("Document path must be relative.", nameof(path));
        }

        if (path.Contains("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("Document path contains repeated separators.", nameof(path));
        }

        var segments = path.Split('/');
        if (segments.Any(static segment => segment.Length == 0 || segment == "." || segment == ".."))
        {
            throw new ArgumentException("Document path contains invalid segments.", nameof(path));
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
    List<string> RejectedPaths);

internal readonly record struct StreamedDocuments(
    int Submitted,
    int Accepted,
    int Rejected,
    long SubmittedBytes,
    long AcceptedBytes,
    long RejectedBytes,
    HashSet<string> AcceptedPaths);

