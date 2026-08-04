namespace Lokad.Sift;

public enum RejectedUpsertPolicy
{
    FailUpdate,
    PreserveExisting,
    DeleteExisting
}

public enum DocumentRejectionReason
{
    MaximumDocumentBytesExceeded,
    BinaryContent,
    InvalidUtf8
}

public enum DocumentContentLifetime
{
    /// <summary>The source may reuse or mutate the buffer on its next read.</summary>
    UntilNextRead,

    /// <summary>The bytes remain immutable until the build or update operation completes.</summary>
    UntilOperationCompletes
}

public readonly record struct DocumentKey
{
    private readonly string? _path;

    public DocumentKey(string path)
    {
        _path = DocumentPath.Normalize(path);
    }

    /// <summary>Gets the normalized logical path.</summary>
    public string Path => _path ?? throw new InvalidOperationException("The document key is uninitialized.");
}

public readonly record struct SubmittedDocument
{
    private readonly string? _path;

    public SubmittedDocument(
        string path,
        ReadOnlyMemory<byte> content)
        : this(path, content, lastModifiedUtc: null)
    {
    }

    public SubmittedDocument(
        string path,
        ReadOnlyMemory<byte> content,
        DateTimeOffset? lastModifiedUtc)
        : this(
            path,
            content,
            lastModifiedUtc,
            DocumentContentLifetime.UntilNextRead)
    {
    }

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

    /// <summary>Gets the normalized logical path.</summary>
    public string Path => _path ?? throw new InvalidOperationException("The submitted document is uninitialized.");

    /// <summary>Gets the UTF-8 document body.</summary>
    public ReadOnlyMemory<byte> Content { get; }
    public DateTimeOffset? LastModifiedUtc { get; }

    /// <summary>Gets how long the source guarantees that <see cref="Content"/> remains immutable.</summary>
    public DocumentContentLifetime ContentLifetime { get; }
}

public sealed class RejectedUpsertException : Exception
{
    public RejectedUpsertException(
        int rejectedUpserts,
        long rejectedBytes,
        IReadOnlyList<RejectedDocument> rejectedDocuments)
        : base(CreateMessage(rejectedUpserts, rejectedBytes, rejectedDocuments))
    {
        RejectedUpserts = rejectedUpserts;
        RejectedBytes = rejectedBytes;
        RejectedDocuments = PublicResultCollection.Freeze(rejectedDocuments);
    }

    public int RejectedUpserts { get; }
    public long RejectedBytes { get; }
    public IReadOnlyList<RejectedDocument> RejectedDocuments { get; }

    private static string CreateMessage(
        int rejectedUpserts,
        long rejectedBytes,
        IReadOnlyList<RejectedDocument> rejectedDocuments)
    {
        if (rejectedDocuments.Count == 0)
        {
            return $"Sift rejected {rejectedUpserts} upsert(s) containing {rejectedBytes} byte(s); the update was not committed.";
        }

        var first = rejectedDocuments[0];
        return $"Sift rejected upsert '{first.Path}' ({first.Reason}, {first.SubmittedBytes} byte(s)); the update was not committed.";
    }
}

public readonly record struct RejectedDocument(
    string Path,
    DocumentRejectionReason Reason,
    int SubmittedBytes);

public interface IDocumentKeyCollector
{
    bool OnDocument(DocumentKey key);
}

public readonly record struct DocumentVisitStats(
    int VisitedDocuments,
    bool Completed);

public interface IDocumentSource
{
    /// <summary>Reads the next document, or returns <see langword="false"/> at the end of the source.</summary>
    /// <remarks>
    ///     The returned content must remain immutable for the lifetime declared by
    ///     <see cref="SubmittedDocument.ContentLifetime"/>.
    /// </remarks>
    bool TryReadNext(out SubmittedDocument document);
}
