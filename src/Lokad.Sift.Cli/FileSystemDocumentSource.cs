using Lokad.Sift;

internal sealed class FileSystemDocumentSource : IDocumentSource, IDisposable
{
    private readonly IEnumerator<string> _files;
    private readonly string _root;
    private readonly int _maximumBytes;
    private byte[]? _oversizedContent;

    public FileSystemDocumentSource(string root, string corpusPath, int maximumBytes)
    {
        _root = root;
        _maximumBytes = maximumBytes;
        _files = EnumerateFiles(root, corpusPath).GetEnumerator();
    }

    // The core's content length is int-sized. Rejection bodies are bounded overflow
    // buffers, so restore observed file sizes when reporting CLI byte totals.
    public long UnmaterializedRejectedBytes { get; private set; }

    private static IEnumerable<string> EnumerateFiles(string root, string corpusPath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory))
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) yield return file;
            foreach (var child in Directory.EnumerateDirectories(directory))
                if (!string.Equals(child, corpusPath, comparison) &&
                    (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
        }
    }

    public void Dispose() => _files.Dispose();

    public bool TryReadNext(out SubmittedDocument document)
    {
        if (!_files.MoveNext())
        {
            document = default;
            return false;
        }
        var file = _files.Current;
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
        var content = ReadContent(stream, out var observedLength);
        if (content.Length > _maximumBytes)
            UnmaterializedRejectedBytes = checked(UnmaterializedRejectedBytes +
                Math.Max(0, Math.Max(observedLength, stream.Length) - content.Length));
        document = new SubmittedDocument(
            Path.GetRelativePath(_root, file).Replace('\\', '/'), content,
            File.GetLastWriteTimeUtc(file), DocumentContentLifetime.UntilOperationCompletes);
        return true;
    }

    internal ReadOnlyMemory<byte> ReadContent(Stream stream, out long observedLength)
    {
        var limit = checked(_maximumBytes + 1);
        observedLength = stream.Length;
        if (observedLength > _maximumBytes)
        {
            // Only Length is consumed for an oversized document. Reuse one bounded
            // buffer to enter the existing rejection/DeleteExisting path without I/O.
            return _oversizedContent ??= GC.AllocateUninitializedArray<byte>(limit);
        }

        var bytes = GC.AllocateUninitializedArray<byte>((int)Math.Min(observedLength + 1, limit));
        var count = 0;
        while (count < limit)
        {
            var read = stream.Read(bytes.AsSpan(count));
            if (read == 0) return bytes.AsMemory(0, count);
            count += read;
            if (count == bytes.Length && count < limit)
                Array.Resize(ref bytes, (int)Math.Min(limit, 2L * bytes.Length));
        }
        return bytes;
    }
}
