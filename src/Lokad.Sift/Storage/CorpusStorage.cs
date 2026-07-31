using System.Globalization;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Lokad.Sift;
using Lokad.Sift.LowLevel;

namespace Lokad.Sift.Storage;

public sealed class CorpusStorage : ISiftStorage
{
    private const int LinuxOpenDirectory = 0x10000;
    private const int LinuxOpenCloseOnExec = 0x80000;
    private const ulong GenerationLeaseMagic = 0x455341454C544653UL;
    private const int GenerationLeaseVersion = 1;
    private const int GenerationLeaseHeaderSize = 40;
    private const int GenerationLeaseChecksumSize = 8;
    private readonly string _rootPath;
    private readonly long _maximumPhysicalBytes;
    private readonly object _layoutLock = new();
    private readonly ConcurrentDictionary<ulong, byte> _validatedSegmentIds = new();
    private readonly ConcurrentDictionary<ulong, object> _segmentValidationGates = new();
    private bool _segmentDirectoriesRecovered;
    private long _segmentOpenCount;
    private long _reclamationFailureCount;
    private string? _lastReclamationFailure;

    public CorpusStorage(string rootPath)
        : this(rootPath, long.MaxValue)
    {
    }

    internal CorpusStorage(string rootPath, long maximumPhysicalBytes)
    {
        _rootPath = rootPath ?? throw new ArgumentNullException(nameof(rootPath));
        _maximumPhysicalBytes = maximumPhysicalBytes;
    }

    public string ManifestPath => Path.Combine(_rootPath, "manifest.bin");
    public string SegmentsPath => Path.Combine(_rootPath, "segments");
    public string TempPath => Path.Combine(_rootPath, "temp");
    public string LockPath => Path.Combine(_rootPath, "lock");
    internal string GenerationLeasesPath => Path.Combine(LockPath, "generations");
    internal int ValidatedSegmentCount => _validatedSegmentIds.Count;
    internal long SegmentOpenCount => Interlocked.Read(ref _segmentOpenCount);
    private string ReclamationStatePath => Path.Combine(LockPath, "reclamation.state");
    private string RebuildRequiredPath => Path.Combine(LockPath, "rebuild.required");
    internal PublicationFaultInjector? FaultInjector { get; set; }
    public bool HasManifest => File.Exists(ManifestPath);

    public void EnsureLayout()
    {
        Directory.CreateDirectory(_rootPath);
        Directory.CreateDirectory(SegmentsPath);
        Directory.CreateDirectory(TempPath);
        Directory.CreateDirectory(LockPath);
        Directory.CreateDirectory(GenerationLeasesPath);
        if (!File.Exists(ReclamationStatePath))
        {
            File.WriteAllBytes(ReclamationStatePath, [0]);
        }

        RecoverStaleTempEntries();
        RecoverManifestTempFile();
        RecoverGenerationLeaseTempFiles();

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

    public IDisposable AcquireWriterLock()
    {
        return AcquireWriterLock(CancellationToken.None);
    }

    public IDisposable AcquireWriterLock(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_rootPath);
        Directory.CreateDirectory(LockPath);
        var lockPath = Path.Combine(LockPath, "writer.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream stream;
            try
            {
                stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when (IsWriterLockContention(exception))
            {
                if (cancellationToken.WaitHandle.WaitOne(50))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                continue;
            }

            try
            {
                EnsureLayout();
                return stream;
            }
            catch (InvalidDataException)
            {
                // The writer lock is also required to replace a corrupt derived
                // index. The operation itself still observes the corruption.
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
    }

    internal static bool IsWriterLockContention(IOException exception)
    {
        // FileShare.None reports a sharing violation on Windows and EAGAIN on
        // Unix. Other I/O failures (disk, path, device) must reach the caller.
        var errorCode = exception.HResult & 0xFFFF;
        return errorCode is 11 or 32 or 33 or 35;
    }

    IDisposable ISiftStorage.AcquireWriterLock(CancellationToken cancellationToken) => AcquireWriterLock(cancellationToken);

    public SegmentManifestEntry PublishSegment<TDocument>(
        IReadOnlyList<TDocument> documents,
        ulong segmentId,
        long minimumFreeBytesAfterPublication,
        CancellationToken cancellationToken)
        where TDocument : ISegmentBuildDocument
    {
        EnsureLayout();
        // A crash can leave this pack published but absent from the manifest; startup must sweep it.
        SetReclamationPending(isPending: true);
        var tempSegmentDirectory = Path.Combine(TempPath, segmentId.ToString("X16"));
        Directory.CreateDirectory(tempSegmentDirectory);
        var tempSegmentPath = Path.Combine(tempSegmentDirectory, "segment.pack");
        var contentBytes = SegmentWriter.Write(
            tempSegmentPath,
            tempSegmentDirectory,
            segmentId,
            documents,
            segmentBytes => EnsureCapacity(segmentBytes, minimumFreeBytesAfterPublication),
            cancellationToken);
        ValidateSegmentPack(tempSegmentPath, cancellationToken);
        FaultInjector?.ThrowIfRequested(PublicationStage.AfterSegmentFlushBeforePublish);
        var finalSegmentDirectory = Path.Combine(SegmentsPath, segmentId.ToString("X16"));
        var finalSegmentPath = Path.Combine(finalSegmentDirectory, "segment.pack");
        if (File.Exists(finalSegmentPath))
        {
            throw new IOException($"Sift segment id collision for '{segmentId:X16}'.");
        }

        Directory.CreateDirectory(finalSegmentDirectory);
        File.Move(tempSegmentPath, finalSegmentPath);
        FlushDirectoryEntry(finalSegmentDirectory);
        FlushDirectoryEntry(SegmentsPath);
        _validatedSegmentIds.TryAdd(segmentId, 0);
        Directory.Delete(tempSegmentDirectory, recursive: true);
        return new SegmentManifestEntry(segmentId, documents.Count, contentBytes, [], DeadContentBytes: 0);
    }

    public void PublishManifest(CorpusManifest manifest)
    {
        EnsureLayout();
        WriteManifest(manifest);
    }

    public CorpusManifest ReadManifest()
    {
        return ReadManifest(ManifestPath);
    }

    private static CorpusManifest ReadManifest(string manifestPath)
    {
        using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < SegmentFormat.ManifestHeaderSize + sizeof(ulong) ||
            stream.Length > int.MaxValue)
        {
            throw new InvalidDataException("Invalid Sift manifest length.");
        }

        var length = checked((int)stream.Length);
        var bytes = GC.AllocateUninitializedArray<byte>(length);
        stream.ReadExactly(bytes);
        var span = bytes.AsSpan();
        if (LittleEndianCodec.ReadUInt64(span) != SegmentFormat.ManifestMagic)
        {
            throw new InvalidDataException("Invalid Sift manifest magic.");
        }

        var version = LittleEndianCodec.ReadInt32(span[8..]);
        if (version is not SegmentFormat.LegacyManifestFormatVersion and
            not SegmentFormat.ManifestFormatVersion)
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
        if (count < 0)
        {
            throw new InvalidDataException("Invalid Sift manifest segment count.");
        }

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
        var entryHeaderSize = version == SegmentFormat.ManifestFormatVersion
            ? SegmentFormat.ManifestEntryHeaderSize
            : SegmentFormat.LegacyManifestEntryHeaderSize;
        for (var i = 0; i < count; i++)
        {
            if (offset > limit - entryHeaderSize)
            {
                throw new InvalidDataException("Sift manifest segment table is truncated.");
            }

            var segmentId = LittleEndianCodec.ReadUInt64(span[offset..]);
            var documentCount = LittleEndianCodec.ReadInt32(span[(offset + 8)..]);
            var contentBytes = LittleEndianCodec.ReadInt64(span[(offset + 12)..]);
            var deadCount = LittleEndianCodec.ReadInt32(span[(offset + 20)..]);
            var deadBytes = LittleEndianCodec.ReadInt32(span[(offset + 24)..]);
            var deadContentBytes = version == SegmentFormat.ManifestFormatVersion
                ? LittleEndianCodec.ReadInt64(span[(offset + 28)..])
                : -1;
            offset += entryHeaderSize;
            if (documentCount < 0 ||
                contentBytes < 0 ||
                deadCount < 0 ||
                deadCount > documentCount ||
                deadBytes < 0 ||
                deadContentBytes < (version == SegmentFormat.ManifestFormatVersion ? 0 : -1) ||
                deadContentBytes > contentBytes ||
                offset > limit - deadBytes)
            {
                throw new InvalidDataException("Invalid Sift manifest segment entry.");
            }

            var deadPayload = span.Slice(offset, deadBytes);
            offset += deadBytes;
            var deadOrdinals = new uint[deadCount];
            uint current = 0;
            var deadOffset = 0;
            for (var j = 0; j < deadCount; j++)
            {
                current += VarInt.ReadUInt32(deadPayload, ref deadOffset);
                if (current >= documentCount ||
                    (j > 0 && current <= deadOrdinals[j - 1]))
                {
                    throw new InvalidDataException("Invalid Sift manifest dead ordinal.");
                }

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
                deadOrdinals,
                deadContentBytes);
        }

        if (offset != limit)
        {
            throw new InvalidDataException("Unexpected trailing bytes in Sift manifest segment table.");
        }

        return new CorpusManifest(corpusId, generation, segments);
    }

    public MappedSegment OpenSegment(ulong segmentId)
    {
        return OpenSegment(segmentId, CancellationToken.None);
    }

    public MappedSegment OpenSegment(ulong segmentId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _segmentOpenCount);
        var path = Path.Combine(SegmentsPath, segmentId.ToString("X16"), "segment.pack");
        return OpenAndValidateIdentity(
            path,
            segmentId,
            validateChecksums: false,
            cancellationToken);
    }

    public void ValidateSegment(ulong segmentId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _segmentOpenCount);
        var path = Path.Combine(SegmentsPath, segmentId.ToString("X16"), "segment.pack");
        if (_validatedSegmentIds.ContainsKey(segmentId))
        {
            using var _ = OpenAndValidateIdentity(
                path,
                segmentId,
                validateChecksums: false,
                cancellationToken);
            return;
        }

        var validationGate = _segmentValidationGates.GetOrAdd(segmentId, static _ => new object());
        while (!Monitor.TryEnter(validationGate, millisecondsTimeout: 50))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        try
        {
            if (_validatedSegmentIds.ContainsKey(segmentId))
            {
                using var _ = OpenAndValidateIdentity(
                    path,
                    segmentId,
                    validateChecksums: false,
                    cancellationToken);
                return;
            }

            using var segment = OpenAndValidateIdentity(
                path,
                segmentId,
                validateChecksums: true,
                cancellationToken);
            _validatedSegmentIds.TryAdd(segmentId, 0);
            _segmentValidationGates.TryRemove(segmentId, out _);
        }
        finally
        {
            Monitor.Exit(validationGate);
        }
    }

    public long GetSegmentBytes(ulong segmentId)
    {
        var path = Path.Combine(SegmentsPath, segmentId.ToString("X16"), "segment.pack");
        return new FileInfo(path).Length;
    }

    public long? GetAvailableBytes()
    {
        return GetAvailableBytes(GetCorpusPhysicalBytes());
    }

    private long? GetAvailableBytes(long corpusPhysicalBytes)
    {
        var corpusAvailableBytes = Math.Max(0, _maximumPhysicalBytes - corpusPhysicalBytes);
        var filesystemAvailableBytes = GetFilesystemAvailableBytes();
        return filesystemAvailableBytes is null
            ? corpusAvailableBytes
            : Math.Min(corpusAvailableBytes, filesystemAvailableBytes.Value);
    }

    private long? GetFilesystemAvailableBytes()
    {
        try
        {
            var fullPath = Path.GetFullPath(_rootPath);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var containingDrive = DriveInfo.GetDrives()
                .Where(drive => IsPathWithinRoot(fullPath, drive.RootDirectory.FullName, comparison))
                .MaxBy(drive => drive.RootDirectory.FullName.Length);
            return containingDrive?.AvailableFreeSpace;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private void EnsureCapacity(long addedBytes, long minimumFreeBytesAfterPublication)
    {
        var availableBytes = GetAvailableBytes();
        if (addedBytes > availableBytes ||
            minimumFreeBytesAfterPublication > availableBytes - addedBytes)
        {
            var requiredBytes = addedBytes > long.MaxValue - minimumFreeBytesAfterPublication
                ? long.MaxValue
                : addedBytes + minimumFreeBytesAfterPublication;
            throw new IOException(
                $"Sift requires {requiredBytes} available bytes to publish a {addedBytes}-byte file " +
                $"while retaining {minimumFreeBytesAfterPublication} bytes, but only {availableBytes} bytes are available.");
        }
    }

    private long GetCorpusPhysicalBytes()
    {
        if (!Directory.Exists(_rootPath))
        {
            return 0;
        }

        long bytes = 0;
        foreach (var path in Directory.EnumerateFiles(_rootPath, "*", SearchOption.AllDirectories))
        {
            try
            {
                bytes = checked(bytes + new FileInfo(path).Length);
            }
            catch (FileNotFoundException)
            {
                // A concurrent snapshot cleanup may remove an obsolete lease.
            }
        }

        return bytes;
    }

    bool ISiftStorage.IsReclamationPending
    {
        get => IsReclamationPending();
    }

    void ISiftStorage.EnsureSnapshotMetadata()
    {
        if (File.Exists(RebuildRequiredPath))
        {
            throw new InvalidDataException(
                "A previous Sift rebuild did not complete; a full Build is required.");
        }

        if (!HasManifest)
        {
            if (!File.Exists(ManifestPath + ".tmp"))
            {
                return;
            }

            using var recoveryLock = AcquireWriterLock();
            EnsureCurrentGenerationLeaseUnderWriterLock();
            SetReclamationPending(isPending: true);
            return;
        }

        var manifest = ReadManifest();
        var leasePath = GetGenerationLeasePath(manifest.Generation);
        if (File.Exists(leasePath))
        {
            // Normal startup stays read-only and does not contend with writers.
            using var stream = new FileStream(leasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            ValidateGenerationLease(stream, manifest);
            return;
        }

        // Pre-lease corpora need a one-time writer-serialized migration and orphan sweep.
        using var migrationLock = AcquireWriterLock();
        EnsureCurrentGenerationLeaseUnderWriterLock();
        SetReclamationPending(isPending: true);
    }

    StorageReadSnapshot ISiftStorage.OpenReadSnapshot()
    {
        // Publication can advance the manifest between the read and lease open; retry only that race.
        while (true)
        {
            if (!HasManifest)
            {
                return new StorageReadSnapshot(
                    new CorpusManifest(Guid.Empty, 0, []),
                    EmptyDisposable.Instance);
            }

            var manifest = ReadManifest();
            var leasePath = GetGenerationLeasePath(manifest.Generation);
            FileStream lease;
            try
            {
                // FileShare.Read lets other readers join but prevents reclamation's exclusive delete.
                lease = new FileStream(leasePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (FileNotFoundException)
            {
                if (HasManifest && ReadManifest().Generation != manifest.Generation)
                {
                    continue;
                }

                throw new InvalidDataException(
                    $"Missing Sift generation lease for manifest generation {manifest.Generation}.");
            }
            catch (DirectoryNotFoundException)
            {
                if (HasManifest && ReadManifest().Generation != manifest.Generation)
                {
                    continue;
                }

                throw new InvalidDataException(
                    $"Missing Sift generation lease directory for manifest generation {manifest.Generation}.");
            }
            catch (IOException)
            {
                if (HasManifest && ReadManifest().Generation != manifest.Generation)
                {
                    continue;
                }

                throw;
            }

            try
            {
                ValidateGenerationLease(lease, manifest);
                return new StorageReadSnapshot(manifest, lease);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
    }

    StorageStats ISiftStorage.GetStorageStats()
    {
        using var writerLock = AcquireWriterLock();
        SegmentReclamationStats reclamation;
        if (IsReclamationPending())
        {
            reclamation = ReclaimUnreferencedSegmentsUnderWriterLock();
        }
        else
        {
            // A clear marker avoids the retired-generation sweep, but the current
            // lease remains the fail-closed authority for the live manifest.
            reclamation = default;
            if (HasManifest && !IsCurrentGenerationLeaseValid(ReadManifest()))
            {
                reclamation = reclamation with { IsBlocked = true };
            }
        }

        return GetStorageStatsUnderWriterLock(reclamation);
    }

    void ISiftStorage.ReclaimUnreferencedSegments(CancellationToken cancellationToken)
    {
        try
        {
            using var writerLock = AcquireWriterLock(cancellationToken);
            _ = ReclaimUnreferencedSegmentsUnderWriterLock(cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            RecordReclamationFailure(exception);
            throw;
        }
    }

    void ISiftStorage.EnsureReplacementAllowedUnderWriterLock()
    {
        EnsureReplacementAllowedUnderWriterLock();
    }

    bool ISiftStorage.CanReplaceSegmentsUnderWriterLock()
    {
        if (!HasManifest)
        {
            return true;
        }

        // Publication leaves the marker clear only after every retired pack is gone.
        if (!IsReclamationPending())
        {
            return IsCurrentGenerationLeaseValid(ReadManifest());
        }

        var reclamation = ReclaimUnreferencedSegmentsUnderWriterLock();
        return !reclamation.IsBlocked &&
               reclamation.FailedSegmentCount == 0 &&
               reclamation.DeferredSegmentCount == 0;
    }

    void ISiftStorage.PrepareFullBuildUnderWriterLock(bool discardCorruptState)
    {
        if (!discardCorruptState)
        {
            EnsureReplacementAllowedUnderWriterLock();
            return;
        }

        ResetCorruptIndexUnderWriterLock();
    }

    void ISiftStorage.CleanupFailedMutationUnderWriterLock(IReadOnlyCollection<ulong> attemptedSegmentIds)
    {
        // With no current manifest, a flushed initial manifest may still name every
        // attempted pack. Roll it back before making those packs unreachable.
        var canDeleteAttemptedPacks =
            HasManifest ||
            TryRemoveUnpublishedManifestArtifactsUnderWriterLock();
        HashSet<ulong>? referencedSegmentIds;
        if (!canDeleteAttemptedPacks)
        {
            referencedSegmentIds = null;
        }
        else
        {
            try
            {
                referencedSegmentIds = HasManifest
                    ? ReadManifest().Segments.Select(static segment => segment.SegmentId).ToHashSet()
                    : [];
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // An unreadable manifest could reference any attempted pack. Leave final
                // packs untouched and let explicit recovery decide what is authoritative.
                referencedSegmentIds = null;
            }
        }

        var deletedFinalSegment = false;
        foreach (var segmentId in attemptedSegmentIds.Distinct())
        {
            TryDeleteDirectory(Path.Combine(TempPath, segmentId.ToString("X16")));
            if (referencedSegmentIds is null || referencedSegmentIds.Contains(segmentId))
            {
                continue;
            }

            if (TryDeleteDirectory(Path.Combine(SegmentsPath, segmentId.ToString("X16"))))
            {
                deletedFinalSegment = true;
                _validatedSegmentIds.TryRemove(segmentId, out _);
                _segmentValidationGates.TryRemove(segmentId, out _);
            }
        }

        if (deletedFinalSegment)
        {
            try
            {
                FlushDirectoryEntry(SegmentsPath);
            }
            catch (IOException)
            {
                // Cleanup is best effort and the persistent marker schedules a retry.
            }
        }

        if (!canDeleteAttemptedPacks)
        {
            SetReclamationPending(isPending: true);
            return;
        }

        try
        {
            _ = ReclaimUnreferencedSegmentsUnderWriterLock();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Keep the retry marker set when normal reachability cleanup cannot finish.
        }
    }

    private void EnsureReplacementAllowedUnderWriterLock()
    {
        if (!HasManifest)
        {
            // A full build is authoritative. Complete packs left by a crashed initial build
            // have no readable generation and must not permanently prevent reconstruction.
            var abandonedSegments = EnumeratePhysicalSegments();
            var failedCount = 0;
            long failedBytes = 0;
            foreach (var segment in abandonedSegments)
            {
                try
                {
                    Directory.Delete(segment.DirectoryPath, recursive: true);
                }
                catch (DirectoryNotFoundException)
                {
                }
                catch (IOException)
                {
                    failedCount++;
                    failedBytes += segment.Bytes;
                }
                catch (UnauthorizedAccessException)
                {
                    failedCount++;
                    failedBytes += segment.Bytes;
                }
            }

            if (abandonedSegments.Count > 0)
            {
                FlushDirectoryEntry(SegmentsPath);
            }

            if (failedCount > 0)
            {
                throw new IOException(
                    $"Cannot start a full Sift build because {failedCount} abandoned segment pack(s) " +
                    $"({failedBytes} bytes) could not be deleted.");
            }

            DeleteDirectoryEntriesForRebuild(GenerationLeasesPath);
            FlushDirectoryEntry(GenerationLeasesPath);
            return;
        }

        // One unreclaimed replacement generation is the disk-growth bound.
        var reclamation = ReclaimUnreferencedSegmentsUnderWriterLock();
        if (reclamation.IsBlocked)
        {
            throw new InvalidDataException(
                "Cannot replace Sift segments because generation-lease metadata is missing or invalid.");
        }

        if (reclamation.FailedSegmentCount > 0)
        {
            throw new IOException(
                $"Cannot replace Sift segments because {reclamation.FailedSegmentCount} retired segment pack(s) " +
                $"({reclamation.FailedSegmentBytes} bytes) could not be deleted.");
        }

        if (reclamation.DeferredSegmentCount > 0)
        {
            throw new InvalidOperationException(
                $"Cannot replace Sift segments while {reclamation.DeferredSegmentCount} retired segment pack(s) " +
                $"({reclamation.DeferredSegmentBytes} bytes) remain leased by older snapshots.");
        }
    }

    private void ResetCorruptIndexUnderWriterLock()
    {
        Directory.CreateDirectory(_rootPath);
        Directory.CreateDirectory(LockPath);
        using (var marker = new FileStream(
                   RebuildRequiredPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.Read))
        {
            marker.WriteByte(1);
            marker.Flush(flushToDisk: true);
        }

        FlushDirectoryEntry(LockPath);
        DeleteFileForRebuild(ManifestPath);
        DeleteFileForRebuild(ManifestPath + ".tmp");
        DeleteDirectoryEntriesForRebuild(SegmentsPath);
        DeleteDirectoryEntriesForRebuild(TempPath);
        DeleteDirectoryEntriesForRebuild(GenerationLeasesPath);
        FlushDirectoryEntry(SegmentsPath);
        FlushDirectoryEntry(TempPath);
        FlushDirectoryEntry(GenerationLeasesPath);
        _validatedSegmentIds.Clear();
        _segmentValidationGates.Clear();
        _segmentDirectoriesRecovered = true;
        SetReclamationPending(isPending: false);
        FlushDirectoryEntry(_rootPath);
    }

    private void WriteManifest(CorpusManifest manifest)
    {
        manifest = ResolveLegacyDeadContentBytes(manifest);
        // Set the retry marker and publish the reader lease before making the generation discoverable.
        SetReclamationPending(isPending: true);
        EnsureGenerationLease(manifest);
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
            LittleEndianCodec.WriteInt64(span[(offset + 28)..], segment.DeadContentBytes);
            offset += SegmentFormat.ManifestEntryHeaderSize;
            uint previous = 0;
            foreach (var dead in segment.DeadOrdinals)
            {
                offset += VarInt.WriteUInt32(span[offset..], dead - previous);
                previous = dead;
            }
        }

        LittleEndianCodec.WriteUInt64(span.Slice(checksumOffset, checksumLength), Hashing.Fnv1A64(span.Slice(0, checksumOffset)));

        EnsureCapacity(size, minimumFreeBytesAfterPublication: 0);
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

        FlushDirectoryEntry(_rootPath);
        if (File.Exists(RebuildRequiredPath))
        {
            File.Delete(RebuildRequiredPath);
            FlushDirectoryEntry(LockPath);
        }

        TryReclaimAfterPublication();
    }

    private CorpusManifest ResolveLegacyDeadContentBytes(CorpusManifest manifest)
    {
        SegmentManifestEntry[]? resolvedSegments = null;
        for (var i = 0; i < manifest.Segments.Count; i++)
        {
            var entry = manifest.Segments[i];
            if (entry.DeadContentBytes >= 0)
            {
                if (resolvedSegments is not null)
                {
                    resolvedSegments[i] = entry;
                }

                continue;
            }

            resolvedSegments ??= manifest.Segments.ToArray();
            long deadContentBytes = 0;
            using var segment = OpenSegment(entry.SegmentId);
            foreach (var ordinal in entry.DeadOrdinals)
            {
                deadContentBytes = checked(
                    deadContentBytes + segment.GetDocument(ordinal).ContentLength);
            }

            resolvedSegments[i] = entry with { DeadContentBytes = deadContentBytes };
        }

        return resolvedSegments is null
            ? manifest
            : manifest with { Segments = resolvedSegments };
    }

    private static void ValidateSegmentPack(
        string path,
        CancellationToken cancellationToken)
    {
        using var _ = new MappedSegment(
            path,
            validateChecksums: true,
            cancellationToken);
    }

    private static MappedSegment OpenAndValidateIdentity(
        string path,
        ulong expectedSegmentId,
        bool validateChecksums,
        CancellationToken cancellationToken)
    {
        var segment = new MappedSegment(path, validateChecksums, cancellationToken);
        if (segment.SegmentId == expectedSegmentId)
        {
            return segment;
        }

        var actualSegmentId = segment.SegmentId;
        segment.Dispose();
        throw new InvalidDataException(
            $"Sift segment '{expectedSegmentId:X16}' contains id '{actualSegmentId:X16}'.");
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

        try
        {
            _ = ReadManifest(tempPath);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
        {
            // A full build can safely discard any packs that were published before this
            // incomplete first manifest because no generation ever made them visible.
            File.Delete(tempPath);
            return;
        }

        File.Move(tempPath, ManifestPath, overwrite: true);
        FlushDirectoryEntry(_rootPath);
    }

    private void RecoverGenerationLeaseTempFiles()
    {
        if (!Directory.Exists(GenerationLeasesPath))
        {
            return;
        }

        foreach (var filePath in Directory.EnumerateFiles(GenerationLeasesPath, "*.tmp"))
        {
            File.Delete(filePath);
        }
    }

    private void RecoverIncompleteSegmentDirectories()
    {
        if (!Directory.Exists(SegmentsPath))
        {
            return;
        }

        var referencedSegmentIds = HasManifest
            ? ReadManifest().Segments.Select(static segment => segment.SegmentId).ToHashSet()
            : [];
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
                var directoryName = Path.GetFileName(directory);
                if (directoryName.Length != 16 ||
                    !ulong.TryParse(directoryName, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var segmentId))
                {
                    Directory.Delete(directory, recursive: true);
                    continue;
                }

                using var _ = OpenSegment(segmentId);
            }
            catch (InvalidDataException)
            {
                var directoryName = Path.GetFileName(directory);
                if (directoryName.Length == 16 &&
                    ulong.TryParse(directoryName, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var segmentId) &&
                    referencedSegmentIds.Contains(segmentId))
                {
                    throw new InvalidDataException(
                        $"The current Sift manifest references corrupt segment '{segmentId:X16}'.");
                }

                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private void EnsureCurrentGenerationLeaseUnderWriterLock()
    {
        if (HasManifest)
        {
            EnsureGenerationLease(ReadManifest());
        }
    }

    private void EnsureGenerationLease(CorpusManifest manifest)
    {
        // Readers pin a generation's segment set by holding this immutable file open.
        Directory.CreateDirectory(GenerationLeasesPath);
        var leasePath = GetGenerationLeasePath(manifest.Generation);
        if (File.Exists(leasePath))
        {
            // A generation number may never be reused for different contents.
            using var existing = new FileStream(leasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            ValidateGenerationLease(existing, manifest);
            return;
        }

        var checksumOffset = checked(GenerationLeaseHeaderSize + manifest.Segments.Count * sizeof(ulong));
        var bytes = GC.AllocateUninitializedArray<byte>(checked(checksumOffset + GenerationLeaseChecksumSize));
        var span = bytes.AsSpan();
        LittleEndianCodec.WriteUInt64(span, GenerationLeaseMagic);
        LittleEndianCodec.WriteInt32(span[8..], GenerationLeaseVersion);
        manifest.CorpusId.TryWriteBytes(span[12..28]);
        LittleEndianCodec.WriteUInt64(span[28..], manifest.Generation);
        LittleEndianCodec.WriteInt32(span[36..], manifest.Segments.Count);
        for (var i = 0; i < manifest.Segments.Count; i++)
        {
            LittleEndianCodec.WriteUInt64(span[(GenerationLeaseHeaderSize + i * sizeof(ulong))..], manifest.Segments[i].SegmentId);
        }

        LittleEndianCodec.WriteUInt64(
            span[checksumOffset..],
            Hashing.Fnv1A64(span[..checksumOffset]));

        var tempPath = leasePath + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            // Flush then rename so a reader can never acquire a partial lease.
            EnsureCapacity(bytes.Length, minimumFreeBytesAfterPublication: 0);
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(bytes.Length);
                RandomAccess.Write(stream.SafeFileHandle, bytes, 0);
                RandomAccess.FlushToDisk(stream.SafeFileHandle);
            }

            File.Move(tempPath, leasePath);
            FlushDirectoryEntry(GenerationLeasesPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private string GetGenerationLeasePath(ulong generation)
    {
        return Path.Combine(GenerationLeasesPath, generation.ToString("X16", CultureInfo.InvariantCulture) + ".lease");
    }

    private static GenerationLeaseData ReadGenerationLease(FileStream stream)
    {
        if (stream.Length < GenerationLeaseHeaderSize + GenerationLeaseChecksumSize ||
            stream.Length > int.MaxValue)
        {
            throw new InvalidDataException("Invalid Sift generation lease length.");
        }

        stream.Position = 0;
        var bytes = GC.AllocateUninitializedArray<byte>((int)stream.Length);
        stream.ReadExactly(bytes);
        var span = bytes.AsSpan();
        if (LittleEndianCodec.ReadUInt64(span) != GenerationLeaseMagic)
        {
            throw new InvalidDataException("Invalid Sift generation lease magic.");
        }

        var version = LittleEndianCodec.ReadInt32(span[8..]);
        if (version != GenerationLeaseVersion)
        {
            throw new InvalidDataException($"Unsupported Sift generation lease version: {version}.");
        }

        var count = LittleEndianCodec.ReadInt32(span[36..]);
        if (count < 0)
        {
            throw new InvalidDataException("Invalid Sift generation lease segment count.");
        }

        var payloadLength = bytes.Length - GenerationLeaseHeaderSize - GenerationLeaseChecksumSize;
        if (count > payloadLength / sizeof(ulong))
        {
            throw new InvalidDataException("Invalid Sift generation lease segment count.");
        }

        var checksumOffset = GenerationLeaseHeaderSize + count * sizeof(ulong);
        if (bytes.Length != checksumOffset + GenerationLeaseChecksumSize)
        {
            throw new InvalidDataException("Invalid Sift generation lease payload length.");
        }

        var expectedChecksum = LittleEndianCodec.ReadUInt64(span[checksumOffset..]);
        if (expectedChecksum != Hashing.Fnv1A64(span[..checksumOffset]))
        {
            throw new InvalidDataException("Invalid Sift generation lease checksum.");
        }

        var segmentIds = new ulong[count];
        for (var i = 0; i < count; i++)
        {
            segmentIds[i] = LittleEndianCodec.ReadUInt64(span[(GenerationLeaseHeaderSize + i * sizeof(ulong))..]);
        }

        return new GenerationLeaseData(
            new Guid(span[12..28]),
            LittleEndianCodec.ReadUInt64(span[28..]),
            segmentIds);
    }

    private static void ValidateGenerationLease(FileStream stream, CorpusManifest manifest)
    {
        var lease = ReadGenerationLease(stream);
        if (lease.CorpusId != manifest.CorpusId ||
            lease.Generation != manifest.Generation ||
            lease.SegmentIds.Length != manifest.Segments.Count)
        {
            throw new InvalidDataException("Sift generation lease does not match its manifest.");
        }

        for (var i = 0; i < lease.SegmentIds.Length; i++)
        {
            if (lease.SegmentIds[i] != manifest.Segments[i].SegmentId)
            {
                throw new InvalidDataException("Sift generation lease segment table does not match its manifest.");
            }
        }
    }

    private LeaseProtection InspectGenerationLeases(
        CorpusManifest manifest,
        CancellationToken cancellationToken)
    {
        var protectedSegmentIds = manifest.Segments.Select(static segment => segment.SegmentId).ToHashSet();
        var currentLeasePath = GetGenerationLeasePath(manifest.Generation);
        try
        {
            // Without trusted current metadata, reachability cannot be established safely.
            using var currentLease = new FileStream(currentLeasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            ValidateGenerationLease(currentLease, manifest);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new LeaseProtection(protectedSegmentIds, 0, null, IsBlocked: true);
        }

        var leasedGenerationCount = 0;
        ulong? oldestLeasedGeneration = null;
        var isBlocked = false;
        foreach (var leasePath in Directory.EnumerateFiles(GenerationLeasesPath, "*.lease"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(leasePath, currentLeasePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                // DeleteOnClose closes the race between proving no reader exists and removing the lease.
                using (new FileStream(
                    leasePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose))
                {
                }

                continue;
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
                isBlocked = true;
                continue;
            }

            try
            {
                // Exclusive open failed: treat a valid shared handle as a live cross-process reader.
                using var activeLease = new FileStream(leasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var lease = ReadGenerationLease(activeLease);
                var fileName = Path.GetFileNameWithoutExtension(leasePath);
                if (lease.CorpusId != manifest.CorpusId ||
                    fileName.Length != 16 ||
                    !ulong.TryParse(fileName, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var fileGeneration) ||
                    fileGeneration != lease.Generation)
                {
                    isBlocked = true;
                    continue;
                }

                for (var i = 0; i < lease.SegmentIds.Length; i++)
                {
                    protectedSegmentIds.Add(lease.SegmentIds[i]);
                }

                leasedGenerationCount++;
                oldestLeasedGeneration = oldestLeasedGeneration is null
                    ? lease.Generation
                    : Math.Min(oldestLeasedGeneration.Value, lease.Generation);
            }
            catch (FileNotFoundException)
            {
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                isBlocked = true;
            }
        }

        return new LeaseProtection(
            protectedSegmentIds,
            leasedGenerationCount,
            oldestLeasedGeneration,
            isBlocked);
    }

    private SegmentReclamationStats ReclaimUnreferencedSegmentsUnderWriterLock()
    {
        return ReclaimUnreferencedSegmentsUnderWriterLock(CancellationToken.None);
    }

    private SegmentReclamationStats ReclaimUnreferencedSegmentsUnderWriterLock(
        CancellationToken cancellationToken)
    {
        EnsureLayout();
        var physicalSegments = EnumeratePhysicalSegments(cancellationToken);
        if (!HasManifest)
        {
            // No manifest means no authoritative reachability set: preserve every complete pack.
            var physicalBytes = physicalSegments.Sum(static segment => segment.Bytes);
            return CompleteReclamation(new SegmentReclamationStats(
                ReclaimedSegmentCount: 0,
                ReclaimedSegmentBytes: 0,
                DeferredSegmentCount: 0,
                DeferredSegmentBytes: 0,
                FailedSegmentCount: 0,
                FailedSegmentBytes: 0,
                BlockedSegmentCount: physicalSegments.Count,
                BlockedSegmentBytes: physicalBytes,
                IsBlocked: physicalSegments.Count > 0,
                LeasedGenerationCount: 0,
                OldestLeasedGeneration: null));
        }

        var manifest = ReadManifest();
        var manifestSegmentIds = manifest.Segments.Select(static segment => segment.SegmentId).ToHashSet();
        var protection = InspectGenerationLeases(manifest, cancellationToken);
        if (protection.IsBlocked)
        {
            // A malformed or inaccessible lease could name any retired pack, so delete none.
            var retired = physicalSegments.Where(segment => !manifestSegmentIds.Contains(segment.SegmentId)).ToArray();
            return CompleteReclamation(new SegmentReclamationStats(
                ReclaimedSegmentCount: 0,
                ReclaimedSegmentBytes: 0,
                DeferredSegmentCount: 0,
                DeferredSegmentBytes: 0,
                FailedSegmentCount: 0,
                FailedSegmentBytes: 0,
                BlockedSegmentCount: retired.Length,
                BlockedSegmentBytes: retired.Sum(static segment => segment.Bytes),
                IsBlocked: true,
                protection.LeasedGenerationCount,
                protection.OldestLeasedGeneration));
        }

        var reclaimedSegmentCount = 0;
        long reclaimedSegmentBytes = 0;
        var deferredSegmentCount = 0;
        long deferredSegmentBytes = 0;
        var failedSegmentCount = 0;
        long failedSegmentBytes = 0;
        foreach (var segment in physicalSegments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (manifestSegmentIds.Contains(segment.SegmentId))
            {
                continue;
            }

            if (protection.ProtectedSegmentIds.Contains(segment.SegmentId))
            {
                deferredSegmentCount++;
                deferredSegmentBytes += segment.Bytes;
                continue;
            }

            try
            {
                Directory.Delete(segment.DirectoryPath, recursive: true);
                ForgetSegmentValidation(segment.SegmentId);
                reclaimedSegmentCount++;
                reclaimedSegmentBytes += segment.Bytes;
            }
            catch (DirectoryNotFoundException)
            {
                ForgetSegmentValidation(segment.SegmentId);
                reclaimedSegmentCount++;
                reclaimedSegmentBytes += segment.Bytes;
            }
            catch (IOException)
            {
                failedSegmentCount++;
                failedSegmentBytes += segment.Bytes;
            }
            catch (UnauthorizedAccessException)
            {
                failedSegmentCount++;
                failedSegmentBytes += segment.Bytes;
            }
        }

        if (reclaimedSegmentCount > 0)
        {
            FlushDirectoryEntry(SegmentsPath);
        }

        return CompleteReclamation(new SegmentReclamationStats(
            reclaimedSegmentCount,
            reclaimedSegmentBytes,
            deferredSegmentCount,
            deferredSegmentBytes,
            failedSegmentCount,
            failedSegmentBytes,
            BlockedSegmentCount: 0,
            BlockedSegmentBytes: 0,
            IsBlocked: false,
            protection.LeasedGenerationCount,
            protection.OldestLeasedGeneration));
    }

    private void ForgetSegmentValidation(ulong segmentId)
    {
        _validatedSegmentIds.TryRemove(segmentId, out _);
        _segmentValidationGates.TryRemove(segmentId, out _);
    }

    private StorageStats GetStorageStatsUnderWriterLock(SegmentReclamationStats reclamation)
    {
        var manifestSegmentIds = HasManifest
            ? ReadManifest().Segments.Select(static segment => segment.SegmentId).ToHashSet()
            : [];
        var physicalSegments = EnumeratePhysicalSegments();
        long manifestSegmentBytes = 0;
        var manifestSegmentCount = 0;
        long retiredSegmentBytes = 0;
        var retiredSegmentCount = 0;
        long physicalSegmentBytes = 0;
        foreach (var segment in physicalSegments)
        {
            physicalSegmentBytes += segment.Bytes;
            if (manifestSegmentIds.Contains(segment.SegmentId))
            {
                manifestSegmentCount++;
                manifestSegmentBytes += segment.Bytes;
            }
            else
            {
                retiredSegmentCount++;
                retiredSegmentBytes += segment.Bytes;
            }
        }

        var deferredSegmentCount = Math.Min(retiredSegmentCount, reclamation.DeferredSegmentCount);
        var deferredSegmentBytes = Math.Min(retiredSegmentBytes, reclamation.DeferredSegmentBytes);
        var blockedSegmentCount = Math.Min(
            retiredSegmentCount - deferredSegmentCount,
            reclamation.BlockedSegmentCount);
        var blockedSegmentBytes = Math.Min(
            retiredSegmentBytes - deferredSegmentBytes,
            reclamation.BlockedSegmentBytes);
        var temporaryBytes = GetTemporaryBytes();
        var corpusPhysicalBytes = GetCorpusPhysicalBytes();
        return new StorageStats(
            ManifestSegmentCount: manifestSegmentCount,
            ManifestSegmentBytes: manifestSegmentBytes,
            PhysicalSegmentCount: physicalSegments.Count,
            PhysicalSegmentBytes: physicalSegmentBytes,
            RetiredSegmentCount: retiredSegmentCount,
            RetiredSegmentBytes: retiredSegmentBytes,
            TemporaryBytes: temporaryBytes,
            CorpusPhysicalBytes: corpusPhysicalBytes,
            MaximumPhysicalBytes: _maximumPhysicalBytes,
            AvailableBytes: GetAvailableBytes(corpusPhysicalBytes),
            LeasedGenerationCount: reclamation.LeasedGenerationCount,
            OldestLeasedGeneration: reclamation.OldestLeasedGeneration,
            SnapshotDeferredSegmentCount: deferredSegmentCount,
            SnapshotDeferredSegmentBytes: deferredSegmentBytes,
            PendingDeletionSegmentCount: retiredSegmentCount - deferredSegmentCount - blockedSegmentCount,
            PendingDeletionSegmentBytes: retiredSegmentBytes - deferredSegmentBytes - blockedSegmentBytes,
            BlockedReclamationSegmentCount: blockedSegmentCount,
            BlockedReclamationSegmentBytes: blockedSegmentBytes,
            IsReclamationBlocked: reclamation.IsBlocked,
            ReclamationFailureCount: Interlocked.Read(ref _reclamationFailureCount),
            LastReclamationFailure: Volatile.Read(ref _lastReclamationFailure));
    }

    private void TryReclaimAfterPublication()
    {
        try
        {
            _ = ReclaimUnreferencedSegmentsUnderWriterLock();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // The manifest is already committed; the persistent marker makes cleanup retryable.
            RecordReclamationFailure(exception);
        }
    }

    private SegmentReclamationStats CompleteReclamation(SegmentReclamationStats reclamation)
    {
        SetReclamationPending(
            reclamation.IsBlocked ||
            reclamation.DeferredSegmentCount > 0 ||
            reclamation.FailedSegmentCount > 0 ||
            reclamation.LeasedGenerationCount > 0);
        return reclamation;
    }

    private bool IsReclamationPending()
    {
        if (!File.Exists(ReclamationStatePath))
        {
            // A pre-marker corpus may contain legacy orphans, so absence is conservatively pending.
            return HasManifest;
        }

        using var stream = new FileStream(
            ReclamationStatePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        return stream.ReadByte() != 0;
    }

    private bool IsCurrentGenerationLeaseValid(CorpusManifest manifest)
    {
        try
        {
            using var stream = new FileStream(
                GetGenerationLeasePath(manifest.Generation),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            ValidateGenerationLease(stream, manifest);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private void SetReclamationPending(bool isPending)
    {
        try
        {
            // This advisory byte avoids directory scans on every snapshot disposal.
            using var stream = new FileStream(
                ReclamationStatePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite);
            var marker = isPending ? (byte)1 : (byte)0;
            if (stream.Length == 1 && stream.ReadByte() == marker)
            {
                // Avoid turning a read-only maintenance hint into a synchronous metadata flush.
                return;
            }

            stream.Position = 0;
            stream.SetLength(1);
            stream.WriteByte(marker);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RecordReclamationFailure(exception);
        }
    }

    private void RecordReclamationFailure(Exception exception)
    {
        Interlocked.Increment(ref _reclamationFailureCount);
        Volatile.Write(ref _lastReclamationFailure, exception.Message);
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return false;
            }

            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool TryRemoveUnpublishedManifestArtifactsUnderWriterLock()
    {
        try
        {
            DeleteFileForRebuild(ManifestPath + ".tmp");
            DeleteDirectoryEntriesForRebuild(GenerationLeasesPath);
            FlushDirectoryEntry(_rootPath);
            FlushDirectoryEntry(GenerationLeasesPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preserve the attempted packs if the temporary manifest can still
            // become visible during crash recovery.
            return false;
        }
    }

    private static void DeleteDirectoryEntriesForRebuild(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(path))
        {
            Directory.Delete(directory, recursive: true);
        }

        foreach (var file in Directory.EnumerateFiles(path))
        {
            File.Delete(file);
        }
    }

    private static void DeleteFileForRebuild(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static bool IsPathWithinRoot(
        string fullPath,
        string rootPath,
        StringComparison comparison)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        if (fullPath.Equals(normalizedRoot, comparison))
        {
            return true;
        }

        return fullPath.StartsWith(normalizedRoot, comparison) &&
               (Path.EndsInDirectorySeparator(normalizedRoot) ||
                fullPath.Length > normalizedRoot.Length &&
                IsDirectorySeparator(fullPath[normalizedRoot.Length]));
    }

    private static bool IsDirectorySeparator(char value) =>
        value == Path.DirectorySeparatorChar ||
        value == Path.AltDirectorySeparatorChar;

    private static void FlushDirectoryEntry(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // File data must precede the durable rename that makes a pack or manifest discoverable.
        var descriptor = Open(
            path,
            LinuxOpenDirectory | LinuxOpenCloseOnExec);
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException(
                $"Unable to open Sift directory '{path}' for durable publication.",
                new Win32Exception(error));
        }

        using var handle = new SafeFileHandle(descriptor, ownsHandle: true);
        RandomAccess.FlushToDisk(handle);
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    private List<PhysicalSegment> EnumeratePhysicalSegments()
    {
        return EnumeratePhysicalSegments(CancellationToken.None);
    }

    private List<PhysicalSegment> EnumeratePhysicalSegments(
        CancellationToken cancellationToken)
    {
        var segments = new List<PhysicalSegment>();
        if (!Directory.Exists(SegmentsPath))
        {
            return segments;
        }

        foreach (var directoryPath in Directory.EnumerateDirectories(SegmentsPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directoryName = Path.GetFileName(directoryPath);
            if (directoryName.Length != 16 ||
                !ulong.TryParse(directoryName, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var segmentId))
            {
                continue;
            }

            var packPath = Path.Combine(directoryPath, "segment.pack");
            if (File.Exists(packPath))
            {
                segments.Add(new PhysicalSegment(segmentId, directoryPath, new FileInfo(packPath).Length));
            }
        }

        return segments;
    }

    private long GetTemporaryBytes()
    {
        long bytes = 0;
        if (Directory.Exists(TempPath))
        {
            foreach (var filePath in Directory.EnumerateFiles(TempPath, "*", SearchOption.AllDirectories))
            {
                bytes += new FileInfo(filePath).Length;
            }
        }

        var manifestTempPath = ManifestPath + ".tmp";
        if (File.Exists(manifestTempPath))
        {
            bytes += new FileInfo(manifestTempPath).Length;
        }

        if (Directory.Exists(GenerationLeasesPath))
        {
            foreach (var filePath in Directory.EnumerateFiles(GenerationLeasesPath, "*.tmp"))
            {
                bytes += new FileInfo(filePath).Length;
            }
        }

        return bytes;
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

    private readonly record struct PhysicalSegment(
        ulong SegmentId,
        string DirectoryPath,
        long Bytes);

    private readonly record struct GenerationLeaseData(
        Guid CorpusId,
        ulong Generation,
        ulong[] SegmentIds);

    private readonly record struct LeaseProtection(
        HashSet<ulong> ProtectedSegmentIds,
        int LeasedGenerationCount,
        ulong? OldestLeasedGeneration,
        bool IsBlocked);

    private sealed class EmptyDisposable : IDisposable
    {
        public static EmptyDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

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
    private byte[] _pathUtf8 = [];
    private int _lineCount;
    private byte[] _encodedLineIndex = [];

    public required string Path { get; init; }
    public required byte[] PathUtf8
    {
        get => _pathUtf8;
        init => _pathUtf8 = value;
    }
    public required ReadOnlyMemory<byte> Content { get; init; }
    public required int LineCount
    {
        get => _lineCount;
        init => _lineCount = value;
    }
    public required byte[] EncodedLineIndex
    {
        get => _encodedLineIndex;
        init => _encodedLineIndex = value;
    }
    public required ushort Flags { get; init; }
    public required long MTimeUtcTicks { get; init; }
    public required ulong ContentHash64 { get; init; }

    public ReadOnlySpan<byte> GetPathUtf8() => PathUtf8;

    public ReadOnlySpan<byte> GetContentBytes() => Content.Span;

    public ReadOnlySpan<byte> GetEncodedLineIndex() => EncodedLineIndex;

    internal void SetIndexMetadata(byte[] pathUtf8, int lineCount, byte[] encodedLineIndex)
    {
        _pathUtf8 = pathUtf8;
        _lineCount = lineCount;
        _encodedLineIndex = encodedLineIndex;
    }
}
