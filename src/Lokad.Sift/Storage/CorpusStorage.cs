using Lokad.Sift.LowLevel;

namespace Lokad.Sift.Storage;

public sealed class CorpusStorage : ISiftStorage
{
    private readonly string _rootPath;
    private readonly object _layoutLock = new();
    private bool _segmentDirectoriesRecovered;

    public CorpusStorage(string rootPath)
    {
        _rootPath = rootPath ?? throw new ArgumentNullException(nameof(rootPath));
    }

    public string ManifestPath => Path.Combine(_rootPath, "manifest.bin");
    public string SegmentsPath => Path.Combine(_rootPath, "segments");
    public string TempPath => Path.Combine(_rootPath, "temp");
    public string LockPath => Path.Combine(_rootPath, "lock");
    internal PublicationFaultInjector? FaultInjector { get; set; }
    public bool HasManifest => File.Exists(ManifestPath);

    public void EnsureLayout()
    {
        Directory.CreateDirectory(_rootPath);
        Directory.CreateDirectory(SegmentsPath);
        Directory.CreateDirectory(TempPath);
        Directory.CreateDirectory(LockPath);
        RecoverStaleTempEntries();
        RecoverManifestTempFile();

        if (_segmentDirectoriesRecovered)
        {
            return;
        }

        lock (_layoutLock)
        {
            if (_segmentDirectoriesRecovered)
            {
                return;
            }

            RecoverIncompleteSegmentDirectories();
            _segmentDirectoriesRecovered = true;
        }
    }

    public FileStream AcquireWriterLock(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_rootPath);
        Directory.CreateDirectory(LockPath);
        EnsureLayout();
        var lockPath = Path.Combine(LockPath, "writer.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }

    IDisposable ISiftStorage.AcquireWriterLock(CancellationToken cancellationToken) => AcquireWriterLock(cancellationToken);

    public BuildPublication PublishFullBuild(IReadOnlyList<IReadOnlyList<StoredDocument>> segments, CancellationToken cancellationToken)
    {
        EnsureLayout();

        var existing = File.Exists(ManifestPath) ? ReadManifest() : null;
        var corpusId = existing?.CorpusId ?? Guid.NewGuid();
        var generation = (existing?.Generation ?? 0UL) + 1UL;
        var entries = new List<SegmentManifestEntry>(segments.Count);
        var totalDocuments = 0;
        long totalBytes = 0;
        ulong segmentSeed = generation ^ ((ulong)DateTime.UtcNow.Ticks << 1);
        for (var i = 0; i < segments.Count; i++)
        {
            var segmentId = segmentSeed + (ulong)i;
            var entry = PublishSegment(segments[i], segmentId, cancellationToken);
            entries.Add(entry);
            totalDocuments += entry.DocumentCount;
            totalBytes += entry.ContentBytes;
        }

        WriteManifest(new CorpusManifest(corpusId, generation, entries));
        return new BuildPublication(totalDocuments, entries.Count, totalBytes);
    }

    public SegmentManifestEntry PublishSegment(IReadOnlyList<ISegmentBuildDocument> documents, ulong segmentId, CancellationToken cancellationToken)
    {
        EnsureLayout();
        var tempSegmentDirectory = Path.Combine(TempPath, segmentId.ToString("X16"));
        Directory.CreateDirectory(tempSegmentDirectory);
        var tempSegmentPath = Path.Combine(tempSegmentDirectory, "segment.pack");
        var contentBytes = SegmentWriter.Write(tempSegmentPath, tempSegmentDirectory, segmentId, documents, cancellationToken);
        ValidateSegmentPack(tempSegmentPath);
        FaultInjector?.ThrowIfRequested(PublicationStage.AfterSegmentFlushBeforePublish);
        var finalSegmentDirectory = Path.Combine(SegmentsPath, segmentId.ToString("X16"));
        Directory.CreateDirectory(finalSegmentDirectory);
        File.Move(tempSegmentPath, Path.Combine(finalSegmentDirectory, "segment.pack"), overwrite: true);
        Directory.Delete(tempSegmentDirectory, recursive: true);
        return new SegmentManifestEntry(segmentId, documents.Count, contentBytes, []);
    }

    public void PublishManifest(CorpusManifest manifest)
    {
        EnsureLayout();
        WriteManifest(manifest);
    }

    public CorpusManifest ReadManifest()
    {
        using var stream = new FileStream(ManifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = checked((int)stream.Length);
        var bytes = GC.AllocateUninitializedArray<byte>(length);
        RandomAccess.Read(stream.SafeFileHandle, bytes, 0);
        var span = bytes.AsSpan();
        if (LittleEndianCodec.ReadUInt64(span) != SegmentFormat.ManifestMagic)
        {
            throw new InvalidDataException("Invalid Sift manifest magic.");
        }

        var version = LittleEndianCodec.ReadInt32(span[8..]);
        if (version != SegmentFormat.ManifestFormatVersion)
        {
            throw new InvalidDataException($"Unsupported Sift manifest version: {version}.");
        }

        var corpusId = new Guid(span.Slice(12, 16));
        var generation = LittleEndianCodec.ReadUInt64(span[28..]);
        var count = LittleEndianCodec.ReadInt32(span[36..]);
        var segmentTableOffset = LittleEndianCodec.ReadInt32(span[40..]);
        var segmentTableLength = LittleEndianCodec.ReadInt32(span[44..]);
        var checksumOffset = LittleEndianCodec.ReadInt32(span[48..]);
        var checksumLength = LittleEndianCodec.ReadInt32(span[52..]);
        ValidateManifestBounds(segmentTableOffset, segmentTableLength, bytes.Length, "segment table");
        ValidateManifestBounds(checksumOffset, checksumLength, bytes.Length, "checksum region");
        if (checksumLength != 8)
        {
            throw new InvalidDataException("Invalid Sift manifest checksum length.");
        }

        var expectedChecksum = LittleEndianCodec.ReadUInt64(span.Slice(checksumOffset, checksumLength));
        if (expectedChecksum != Hashing.Fnv1A64(span.Slice(0, checksumOffset)))
        {
            throw new InvalidDataException("Invalid Sift manifest checksum.");
        }

        if (segmentTableOffset != SegmentFormat.ManifestHeaderSize)
        {
            throw new InvalidDataException("Unexpected Sift manifest segment table offset.");
        }

        var segments = new SegmentManifestEntry[count];
        var offset = segmentTableOffset;
        var limit = segmentTableOffset + segmentTableLength;
        for (var i = 0; i < count; i++)
        {
            if (offset > limit - SegmentFormat.ManifestEntryHeaderSize)
            {
                throw new InvalidDataException("Sift manifest segment table is truncated.");
            }

            var segmentId = LittleEndianCodec.ReadUInt64(span[offset..]);
            var documentCount = LittleEndianCodec.ReadInt32(span[(offset + 8)..]);
            var contentBytes = LittleEndianCodec.ReadInt64(span[(offset + 12)..]);
            var deadCount = LittleEndianCodec.ReadInt32(span[(offset + 20)..]);
            var deadBytes = LittleEndianCodec.ReadInt32(span[(offset + 24)..]);
            offset += SegmentFormat.ManifestEntryHeaderSize;
            if (deadCount < 0 || deadBytes < 0 || offset > limit - deadBytes)
            {
                throw new InvalidDataException("Invalid Sift manifest dead-ordinal bounds.");
            }

            var deadPayload = span.Slice(offset, deadBytes);
            offset += deadBytes;
            var deadOrdinals = new uint[deadCount];
            uint current = 0;
            var deadOffset = 0;
            for (var j = 0; j < deadCount; j++)
            {
                current += VarInt.ReadUInt32(deadPayload, ref deadOffset);
                deadOrdinals[j] = current;
            }

            if (deadOffset != deadBytes)
            {
                throw new InvalidDataException("Invalid Sift manifest dead-ordinal payload.");
            }

            segments[i] = new SegmentManifestEntry(
                segmentId,
                documentCount,
                contentBytes,
                deadOrdinals);
        }

        if (offset != limit)
        {
            throw new InvalidDataException("Unexpected trailing bytes in Sift manifest segment table.");
        }

        return new CorpusManifest(corpusId, generation, segments);
    }

    public MappedSegment OpenSegment(ulong segmentId, bool validateChecksums = true)
    {
        return new MappedSegment(Path.Combine(SegmentsPath, segmentId.ToString("X16"), "segment.pack"), validateChecksums);
    }

    private void WriteManifest(CorpusManifest manifest)
    {
        var tempPath = ManifestPath + ".tmp";
        var segmentTableLength = 0;
        foreach (var segment in manifest.Segments)
        {
            segmentTableLength += SegmentFormat.ManifestEntryHeaderSize + GetDeadOrdinalPayloadLength(segment.DeadOrdinals);
        }

        var segmentTableOffset = SegmentFormat.ManifestHeaderSize;
        var checksumOffset = segmentTableOffset + segmentTableLength;
        var checksumLength = 8;
        var size = checksumOffset + checksumLength;
        var bytes = GC.AllocateUninitializedArray<byte>(size);
        var span = bytes.AsSpan();
        LittleEndianCodec.WriteUInt64(span, SegmentFormat.ManifestMagic);
        LittleEndianCodec.WriteInt32(span[8..], SegmentFormat.ManifestFormatVersion);
        manifest.CorpusId.TryWriteBytes(span[12..28]);
        LittleEndianCodec.WriteUInt64(span[28..], manifest.Generation);
        LittleEndianCodec.WriteInt32(span[36..], manifest.Segments.Count);
        LittleEndianCodec.WriteInt32(span[40..], segmentTableOffset);
        LittleEndianCodec.WriteInt32(span[44..], segmentTableLength);
        LittleEndianCodec.WriteInt32(span[48..], checksumOffset);
        LittleEndianCodec.WriteInt32(span[52..], checksumLength);

        var offset = segmentTableOffset;
        foreach (var segment in manifest.Segments)
        {
            LittleEndianCodec.WriteUInt64(span[offset..], segment.SegmentId);
            LittleEndianCodec.WriteInt32(span[(offset + 8)..], segment.DocumentCount);
            LittleEndianCodec.WriteInt64(span[(offset + 12)..], segment.ContentBytes);
            LittleEndianCodec.WriteInt32(span[(offset + 20)..], segment.DeadOrdinals.Length);
            var deadBytes = GetDeadOrdinalPayloadLength(segment.DeadOrdinals);
            LittleEndianCodec.WriteInt32(span[(offset + 24)..], deadBytes);
            offset += SegmentFormat.ManifestEntryHeaderSize;
            uint previous = 0;
            foreach (var dead in segment.DeadOrdinals)
            {
                offset += VarInt.WriteUInt32(span[offset..], dead - previous);
                previous = dead;
            }
        }

        LittleEndianCodec.WriteUInt64(span.Slice(checksumOffset, checksumLength), Hashing.Fnv1A64(span.Slice(0, checksumOffset)));

        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(size);
            RandomAccess.Write(stream.SafeFileHandle, bytes, 0);
            RandomAccess.FlushToDisk(stream.SafeFileHandle);
        }

        FaultInjector?.ThrowIfRequested(PublicationStage.AfterManifestFlushBeforeSwap);

        if (File.Exists(ManifestPath) && OperatingSystem.IsWindows())
        {
            File.Replace(tempPath, ManifestPath, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, ManifestPath, overwrite: true);
        }
    }

    private static void ValidateSegmentPack(string path)
    {
        using var _ = new MappedSegment(path);
    }

    private void RecoverStaleTempEntries()
    {
        if (!Directory.Exists(TempPath))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(TempPath))
        {
            Directory.Delete(directory, recursive: true);
        }

        foreach (var file in Directory.EnumerateFiles(TempPath))
        {
            File.Delete(file);
        }
    }

    private void RecoverManifestTempFile()
    {
        var tempPath = ManifestPath + ".tmp";
        if (!File.Exists(tempPath))
        {
            return;
        }

        if (File.Exists(ManifestPath))
        {
            File.Delete(tempPath);
            return;
        }

        File.Move(tempPath, ManifestPath, overwrite: true);
    }

    private void RecoverIncompleteSegmentDirectories()
    {
        if (!Directory.Exists(SegmentsPath))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(SegmentsPath))
        {
            var segmentPackPath = Path.Combine(directory, "segment.pack");
            if (!File.Exists(segmentPackPath))
            {
                Directory.Delete(directory, recursive: true);
                continue;
            }

            try
            {
                using var _ = new MappedSegment(segmentPackPath);
            }
            catch (InvalidDataException)
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static int GetDeadOrdinalPayloadLength(ReadOnlySpan<uint> deadOrdinals)
    {
        var length = 0;
        uint previous = 0;
        for (var i = 0; i < deadOrdinals.Length; i++)
        {
            length += VarInt.GetUInt32Length(deadOrdinals[i] - previous);
            previous = deadOrdinals[i];
        }

        return length;
    }

    private static void ValidateManifestBounds(int offset, int length, int fileLength, string name)
    {
        if (offset < 0 || length < 0 || offset > fileLength || length > fileLength - offset)
        {
            throw new InvalidDataException($"Invalid Sift manifest {name} bounds.");
        }
    }
}

public readonly record struct BuildPublication(int IndexedDocuments, int SegmentCount, long ContentBytes);

internal enum PublicationStage
{
    AfterSegmentFlushBeforePublish,
    AfterManifestFlushBeforeSwap
}

internal sealed class PublicationFaultInjector
{
    public PublicationStage? ThrowOnStage { get; set; }
    public Exception Exception { get; set; } = new IOException("Injected publication failure.");

    public void ThrowIfRequested(PublicationStage stage)
    {
        if (ThrowOnStage == stage)
        {
            throw Exception;
        }
    }
}

public interface ISegmentBuildDocument
{
    ReadOnlySpan<byte> GetPathUtf8();
    ReadOnlySpan<byte> GetContentBytes();
    int LineCount { get; }
    ReadOnlySpan<byte> GetEncodedLineIndex();
    ushort Flags { get; }
    long MTimeUtcTicks { get; }
    ulong ContentHash64 { get; }
}

public sealed class StoredDocument : ISegmentBuildDocument
{
    public required string Path { get; init; }
    public required byte[] PathUtf8 { get; init; }
    public required ReadOnlyMemory<byte> Content { get; init; }
    public required int LineCount { get; init; }
    public required byte[] EncodedLineIndex { get; init; }
    public required ushort Flags { get; init; }
    public required long MTimeUtcTicks { get; init; }
    public required ulong ContentHash64 { get; init; }

    public ReadOnlySpan<byte> GetPathUtf8() => PathUtf8;

    public ReadOnlySpan<byte> GetContentBytes() => Content.Span;

    public ReadOnlySpan<byte> GetEncodedLineIndex() => EncodedLineIndex;
}
