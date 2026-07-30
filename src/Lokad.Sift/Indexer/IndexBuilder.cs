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
    internal const long DefaultTargetSegmentContentBytes = 256L * 1024 * 1024;
    private readonly ISiftStorage _storage;
    private readonly SiftOptions _options;

    public IndexBuilder(ISiftStorage storage, SiftOptions? options = null)
    {
        _storage = storage;
        _options = options ?? new SiftOptions();
    }

    public BuildStats Build(IDocumentSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var watch = Stopwatch.StartNew();
        using var _ = _storage.AcquireWriterLock(cancellationToken);
        // Refuse before materializing the source; PublishFullBuild repeats this for direct callers.
        _storage.EnsureReplacementAllowedUnderWriterLock();

        var load = LoadDocuments(source, cancellationToken, _options);
        var documents = load.Documents;

        cancellationToken.ThrowIfCancellationRequested();
        documents.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
        var publication = _storage.PublishFullBuild(PartitionDocuments(documents, _options), cancellationToken);
        watch.Stop();

        return new BuildStats(
            SubmittedDocuments: load.Submitted,
            IndexedDocuments: publication.IndexedDocuments,
            RejectedDocuments: load.Rejected,
            SegmentCount: publication.SegmentCount,
            SubmittedBytes: load.SubmittedBytes,
            ContentBytes: publication.ContentBytes,
            RejectedBytes: load.RejectedBytes,
            ElapsedMilliseconds: watch.ElapsedMilliseconds,
            PeakManagedBytes: 0);
    }

    public static LoadedDocuments LoadDocuments(IDocumentSource source, CancellationToken cancellationToken, SiftOptions? options = null)
    {
        options ??= new SiftOptions();
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

            var accepted = TryNormalize(submittedDocument, options, out var normalizedPath, out var storedDocument);
            if (!seenPaths.Add(normalizedPath))
            {
                throw new InvalidOperationException($"Duplicate canonical path in build batch: '{normalizedPath}'.");
            }

            if (!accepted)
            {
                rejected++;
                rejectedBytes += submittedDocument.Content.Length;
                rejectedPaths.Add(normalizedPath);
                continue;
            }

            documents.Add(storedDocument!);
            acceptedBytes += storedDocument!.Content.Length;
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

    public static List<List<StoredDocument>> PartitionDocuments(IReadOnlyList<StoredDocument> documents, SiftOptions? options = null)
    {
        options ??= new SiftOptions();
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

    private static bool TryNormalize(SubmittedDocument document, SiftOptions options, out string normalizedPath, out StoredDocument? storedDocument)
    {
        normalizedPath = CanonicalizePath(document.Path);
        var content = document.Content;
        if (content.Length > options.MaxDocumentBytes)
        {
            storedDocument = null;
            return false;
        }

        var span = content.Span;
        if (LooksBinary(span))
        {
            storedDocument = null;
            return false;
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
            storedDocument = null;
            return false;
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
        storedDocument = new StoredDocument
        {
            Path = normalizedPath,
            PathUtf8 = Encoding.UTF8.GetBytes(normalizedPath),
            Content = content,
            LineCount = lineStarts.Length,
            EncodedLineIndex = LineMap.EncodeLineIndex(lineStarts),
            Flags = flags,
            MTimeUtcTicks = document.LastModifiedUtc?.UtcTicks ?? 0,
            ContentHash64 = Hashing.Fnv1A64(span)
        };

        return true;
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

    private static bool LooksBinary(ReadOnlySpan<byte> content)
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

internal readonly record struct LoadedDocuments(
    int Submitted,
    long SubmittedBytes,
    int Rejected,
    long RejectedBytes,
    long AcceptedBytes,
    List<StoredDocument> Documents,
    List<string> RejectedPaths);

