using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Lokad.Sift.LowLevel;

namespace Lokad.Sift.Storage;

internal sealed unsafe class MappedSegment : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static long s_mappedBytes;
    private static int s_openSegments;
    private readonly MemoryMappedFile? _mappedFile;
    private readonly MemoryMappedViewAccessor? _view;
    private MemoryHandle _memoryHandle;
    private byte* _pointer;
    private readonly long _fileLength;
    private readonly long _mappedLength;
    private readonly ReadOnlyMemory<byte> _backingMemory;
    private readonly UnmanagedByteMemoryManager? _mappedContentMemory;
    private readonly (long Offset, long Length)[] _regions =
        new (long Offset, long Length)[SegmentFormat.ReservedRegionIdEnd + 1];
    private int _disposed;

    public MappedSegment(
        string path,
        bool validateChecksums,
        CancellationToken cancellationToken)
    {
        var accounted = false;
        try
        {
            // Mapped-view capacity is page-rounded on Windows; persisted bounds use the exact file length.
            _fileLength = new FileInfo(path).Length;
            _mappedFile = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            _view = _mappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer);
            _pointer += _view.PointerOffset;
            _mappedLength = _view.Capacity;
            Interlocked.Add(ref s_mappedBytes, _mappedLength);
            Interlocked.Increment(ref s_openSegments);
            accounted = true;
            Initialize(validateChecksums, cancellationToken);
            var contentRegion = GetRegion(SegmentRegionId.ContentBlob);
            if (contentRegion.Length <= int.MaxValue)
            {
                _mappedContentMemory = new UnmanagedByteMemoryManager(
                    this,
                    BasePointer + contentRegion.Offset,
                    (int)contentRegion.Length);
            }
        }
        catch
        {
            if (accounted)
            {
                Interlocked.Add(ref s_mappedBytes, -_mappedLength);
                Interlocked.Decrement(ref s_openSegments);
            }

            if (_pointer is not null)
            {
                _view?.SafeMemoryMappedViewHandle.ReleasePointer();
                _pointer = null;
            }

            _memoryHandle.Dispose();

            _view?.Dispose();
            _mappedFile?.Dispose();
            throw;
        }
    }

    public MappedSegment(
        ReadOnlyMemory<byte> bytes,
        bool validateChecksums,
        CancellationToken cancellationToken)
    {
        var accounted = false;
        try
        {
            _backingMemory = bytes;
            _memoryHandle = bytes.Pin();
            _pointer = (byte*)_memoryHandle.Pointer;
            _fileLength = bytes.Length;
            _mappedLength = bytes.Length;
            Interlocked.Add(ref s_mappedBytes, _mappedLength);
            Interlocked.Increment(ref s_openSegments);
            accounted = true;
            Initialize(validateChecksums, cancellationToken);
        }
        catch
        {
            if (accounted)
            {
                Interlocked.Add(ref s_mappedBytes, -_mappedLength);
                Interlocked.Decrement(ref s_openSegments);
            }

            _memoryHandle.Dispose();
            throw;
        }
    }

    public SegmentId SegmentId { get; private set; }
    public long BuildTimestampTicks { get; private set; }
    public int DocumentCount { get; private set; }
    public long ContentBytes { get; private set; }
    public int RegionCount { get; private set; }

    public SegmentDocument GetDocument(uint ordinal)
    {
        if (ordinal >= (uint)DocumentCount)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        var offset = GetRegion(SegmentRegionId.DocumentTable).Offset + ordinal * SegmentFormat.DocumentRowSize;
        return new SegmentDocument(
            ordinal,
            Read<ulong>(offset),
            Read<int>(offset + 8),
            Read<ulong>(offset + 12),
            Read<int>(offset + 20),
            Read<ulong>(offset + 24),
            Read<int>(offset + 32),
            Read<int>(offset + 36),
            (SegmentDocumentFlags)Read<ushort>(offset + 40),
            Read<long>(offset + 42),
            Read<ulong>(offset + 50));
    }

    public void ValidateManifestEntry(SegmentManifestEntry entry)
    {
        if (SegmentId != entry.SegmentId ||
            DocumentCount != entry.DocumentCount ||
            ContentBytes != entry.ContentBytes)
        {
            throw new InvalidDataException(
                $"Sift segment '{SegmentId.Value:X16}' does not match its manifest metadata.");
        }
    }

    public void ValidateDeadContentBytes(
        SegmentManifestEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.DeadContentBytes < 0)
        {
            return;
        }

        long deadContentBytes = 0;
        foreach (var ordinal in entry.DeadOrdinals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            deadContentBytes = checked(deadContentBytes + GetDocument(ordinal).ContentLength);
        }

        if (deadContentBytes != entry.DeadContentBytes)
        {
            throw new InvalidDataException(
                $"Sift segment '{SegmentId.Value:X16}' does not match its manifest dead-content metadata.");
        }
    }

    public string GetPath(SegmentDocument document)
    {
        return Encoding.UTF8.GetString(GetPathBytes(document));
    }

    public ReadOnlySpan<byte> GetPathBytes(SegmentDocument document)
    {
        var region = GetRegion(SegmentRegionId.PathBlob);
        ValidateDocumentSlice(document.PathOffset, document.PathLength, region.Length, "path");
        return new ReadOnlySpan<byte>(BasePointer + region.Offset + (long)document.PathOffset, document.PathLength);
    }

    public (uint StartInclusive, uint EndExclusive) FindPathPrefixRange(string prefix)
    {
        if (DocumentCount == 0)
        {
            return (0, 0);
        }

        if (prefix.Length == 0)
        {
            return (0, (uint)DocumentCount);
        }

        var first = LowerBound(prefix);
        if (first >= DocumentCount)
        {
            return ((uint)DocumentCount, (uint)DocumentCount);
        }

        var upperPrefix = GetExclusivePrefixUpperBound(prefix);
        var pastLast = upperPrefix is null
            ? (uint)DocumentCount
            : LowerBound(upperPrefix);
        return (first, pastLast);
    }

    public ReadOnlySpan<byte> GetContentBytes(SegmentDocument document)
    {
        var region = GetRegion(SegmentRegionId.ContentBlob);
        ValidateDocumentSlice(document.ContentOffset, document.ContentLength, region.Length, "content");
        return new ReadOnlySpan<byte>(BasePointer + region.Offset + (long)document.ContentOffset, document.ContentLength);
    }

    public ReadOnlyMemory<byte> GetContentMemory(SegmentDocument document)
    {
        var region = GetRegion(SegmentRegionId.ContentBlob);
        ValidateDocumentSlice(document.ContentOffset, document.ContentLength, region.Length, "content");
        var offset = checked((int)(region.Offset + (long)document.ContentOffset));
        if (_mappedFile is null)
        {
            return _backingMemory.Slice(offset, document.ContentLength);
        }

        return _mappedContentMemory is not null
            ? _mappedContentMemory.Memory.Slice(
                checked((int)document.ContentOffset),
                document.ContentLength)
            : new UnmanagedByteMemoryManager(
                this,
                BasePointer + offset,
                document.ContentLength).Memory;
    }

    public bool TryFindDocumentOrdinal(string normalizedPath, out uint ordinal)
    {
        ordinal = LowerBound(normalizedPath);
        if (ordinal >= (uint)DocumentCount)
        {
            return false;
        }

        var candidate = GetDocument(ordinal);
        return ComparePathOrdinal(candidate, normalizedPath) == 0;
    }

    // Segment paths are UTF-8, but their persisted order follows .NET ordinal
    // string order. Compare UTF-16 code units while decoding in place so path
    // lookup does not allocate a string for every binary-search probe.
    internal int ComparePathOrdinal(SegmentDocument document, string normalizedPath)
    {
        var left = new Utf8OrdinalEnumerator(GetPathBytes(document));
        var charIndex = 0;
        while (left.MoveNext(out var leftChar))
        {
            if (charIndex >= normalizedPath.Length)
            {
                return 1;
            }

            var comparison = leftChar.CompareTo(normalizedPath[charIndex++]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return charIndex < normalizedPath.Length ? -1 : 0;
    }

    internal static int ComparePathOrdinal(ReadOnlySpan<byte> leftUtf8, ReadOnlySpan<byte> rightUtf8)
    {
        var left = new Utf8OrdinalEnumerator(leftUtf8);
        var right = new Utf8OrdinalEnumerator(rightUtf8);
        while (true)
        {
            var hasLeft = left.MoveNext(out var leftChar);
            var hasRight = right.MoveNext(out var rightChar);
            if (!hasLeft || !hasRight)
            {
                return hasLeft ? 1 : hasRight ? -1 : 0;
            }

            var comparison = leftChar.CompareTo(rightChar);
            if (comparison != 0)
            {
                return comparison;
            }
        }
    }

    private ref struct Utf8OrdinalEnumerator
    {
        private ReadOnlySpan<byte> _remaining;
        private char _pendingLowSurrogate;

        public Utf8OrdinalEnumerator(ReadOnlySpan<byte> utf8)
        {
            _remaining = utf8;
        }

        public bool MoveNext(out char value)
        {
            if (_pendingLowSurrogate != 0)
            {
                value = _pendingLowSurrogate;
                _pendingLowSurrogate = (char)0;
                return true;
            }

            if (_remaining.Length == 0)
            {
                value = default;
                return false;
            }

            if (_remaining[0] <= 0x7F)
            {
                value = (char)_remaining[0];
                _remaining = _remaining[1..];
                return true;
            }

            if (Rune.DecodeFromUtf8(_remaining, out var rune, out var consumed) != OperationStatus.Done)
            {
                throw new InvalidDataException("Sift segment contains an invalid UTF-8 path.");
            }

            _remaining = _remaining[consumed..];
            if (rune.IsBmp)
            {
                value = (char)rune.Value;
                return true;
            }

            var supplementary = rune.Value - 0x10000;
            value = (char)(0xD800 + (supplementary >> 10));
            _pendingLowSurrogate = (char)(0xDC00 + (supplementary & 0x3FF));
            return true;
        }
    }

    public ReadOnlySpan<byte> GetEncodedLineIndexBytes(SegmentDocument document)
    {
        var region = GetRegion(SegmentRegionId.LineIndexBlob);
        ValidateDocumentSlice(document.LineIndexOffset, document.LineIndexLength, region.Length, "line index");
        return new ReadOnlySpan<byte>(BasePointer + region.Offset + (long)document.LineIndexOffset, document.LineIndexLength);
    }

    public bool TryGetPostings(IndexRegionPair pair, int gram, out ReadOnlySpan<byte> postings, out int docFreq)
    {
        var lexiconRegion = GetRegion(pair.Lexicon);
        var lexicon = new ReadOnlySpan<byte>(BasePointer + lexiconRegion.Offset, (int)lexiconRegion.Length);
        if (lexicon.Length < SegmentFormat.LexiconHeaderSize)
        {
            postings = default;
            docFreq = 0;
            return false;
        }

        var entryCount = LittleEndianCodec.ReadInt32(lexicon);
        var suffixSize = SegmentFormat.GetLexiconSuffixSize(pair.Lexicon);
        var entrySize = SegmentFormat.GetLexiconEntrySize(pair.Lexicon);
        var bucket = GetLexiconBucket(pair.Lexicon, gram);
        var low = LittleEndianCodec.ReadInt32(lexicon.Slice(4 + bucket * 4, 4));
        var high = LittleEndianCodec.ReadInt32(lexicon.Slice(4 + (bucket + 1) * 4, 4)) - 1;
        while (low <= high)
        {
            var mid = low + ((high - low) >> 1);
            if ((uint)mid >= (uint)entryCount)
            {
                break;
            }

            var entryOffset = SegmentFormat.LexiconHeaderSize + mid * entrySize;
            var currentSuffix = ReadLexiconSuffix(lexicon.Slice(entryOffset, suffixSize));
            var targetSuffix = gram & ((1 << (suffixSize * 8)) - 1);
            if (currentSuffix == targetSuffix)
            {
                var postingsRegion = GetRegion(pair.Postings);
                var postingsOffset = LittleEndianCodec.ReadInt32(lexicon.Slice(entryOffset + suffixSize, 4));
                var postingsBytes = LittleEndianCodec.ReadInt32(lexicon.Slice(entryOffset + suffixSize + 4, 4));
                if (postingsOffset < 0 || postingsBytes < 0 || postingsOffset > postingsRegion.Length || postingsBytes > postingsRegion.Length - postingsOffset)
                {
                    throw new InvalidDataException($"Invalid postings bounds for gram '{gram}'.");
                }

                docFreq = LittleEndianCodec.ReadInt32(lexicon.Slice(entryOffset + suffixSize + 8, 4));
                ValidateDocumentFrequency(docFreq);
                postings = new ReadOnlySpan<byte>(BasePointer + postingsRegion.Offset + postingsOffset, postingsBytes);
                return true;
            }

            if (currentSuffix < targetSuffix)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        postings = default;
        docFreq = 0;
        return false;
    }

    public void VisitPostings(IndexRegionPair pair, PostingsVisitor visitor)
    {
        var lexiconRegion = GetRegion(pair.Lexicon);
        var postingsRegion = GetRegion(pair.Postings);
        var lexicon = new ReadOnlySpan<byte>(BasePointer + lexiconRegion.Offset, (int)lexiconRegion.Length);
        if (lexicon.Length < SegmentFormat.LexiconHeaderSize)
        {
            return;
        }

        var entryCount = LittleEndianCodec.ReadInt32(lexicon);
        if (entryCount == 0)
        {
            return;
        }

        var suffixSize = SegmentFormat.GetLexiconSuffixSize(pair.Lexicon);
        var entrySize = SegmentFormat.GetLexiconEntrySize(pair.Lexicon);
        for (var i = 0; i < entryCount; i++)
        {
            var entryOffset = SegmentFormat.LexiconHeaderSize + i * entrySize;
            var postingsOffset = LittleEndianCodec.ReadInt32(lexicon.Slice(entryOffset + suffixSize, 4));
            var postingsBytes = LittleEndianCodec.ReadInt32(lexicon.Slice(entryOffset + suffixSize + 4, 4));
            var docFreq = LittleEndianCodec.ReadInt32(lexicon.Slice(entryOffset + suffixSize + 8, 4));
            ValidateDocumentFrequency(docFreq);
            if (postingsOffset < 0 || postingsBytes < 0 || postingsOffset > postingsRegion.Length || postingsBytes > postingsRegion.Length - postingsOffset)
            {
                throw new InvalidDataException($"Invalid postings bounds in region '{pair.Postings}'.");
            }

            visitor(new ReadOnlySpan<byte>(BasePointer + postingsRegion.Offset + postingsOffset, postingsBytes), docFreq);
        }
    }

    private static int GetLexiconBucket(SegmentRegionId lexiconRegion, int gram)
    {
        return lexiconRegion is SegmentRegionId.ContentBiCsLex or SegmentRegionId.ContentBiIcLex
            ? (gram >> 8) & 0xFF
            : (gram >> 16) & 0xFF;
    }

    private void ValidateLexiconRegion(
        SegmentRegionId regionId,
        bool validateEntries,
        CancellationToken cancellationToken)
    {
        var region = GetRegion(regionId);
        if (region.Length > int.MaxValue)
        {
            throw new InvalidDataException($"Lexicon region '{regionId}' exceeds the supported length.");
        }

        var lexicon = new ReadOnlySpan<byte>(BasePointer + region.Offset, (int)region.Length);
        if (lexicon.Length < SegmentFormat.LexiconHeaderSize)
        {
            throw new InvalidDataException($"Invalid lexicon header for region '{regionId}'.");
        }

        var entryCount = LittleEndianCodec.ReadInt32(lexicon);
        var suffixSize = SegmentFormat.GetLexiconSuffixSize(regionId);
        var entrySize = SegmentFormat.GetLexiconEntrySize(regionId);
        if (entryCount < 0 ||
            lexicon.Length != SegmentFormat.LexiconHeaderSize + (long)entryCount * entrySize)
        {
            throw new InvalidDataException($"Invalid lexicon length for region '{regionId}'.");
        }

        var previous = 0;
        for (var bucket = 0; bucket < SegmentFormat.LexiconBucketCount; bucket++)
        {
            var current = LittleEndianCodec.ReadInt32(lexicon.Slice(4 + bucket * 4, 4));
            if (current < previous || current > entryCount)
            {
                throw new InvalidDataException($"Invalid lexicon bucket table for region '{regionId}'.");
            }

            previous = current;
        }

        // Checksums make the variable-length entry payload immutable after its first
        // successful open; later mappings only need the fixed-size shape checks above.
        if (!validateEntries)
        {
            return;
        }

        var previousSuffix = -1;
        var bucketPrefix = 0;
        for (var entry = 0; entry < entryCount; entry++)
        {
            if ((entry & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            while (bucketPrefix + 1 < SegmentFormat.LexiconBucketCount &&
                   LittleEndianCodec.ReadInt32(lexicon.Slice(4 + (bucketPrefix + 1) * 4, 4)) <= entry)
            {
                bucketPrefix++;
                previousSuffix = -1;
            }

            var entryOffset = SegmentFormat.LexiconHeaderSize + entry * entrySize;
            var suffix = ReadLexiconSuffix(lexicon.Slice(entryOffset, suffixSize));
            if (suffix < previousSuffix)
            {
                throw new InvalidDataException($"Non-monotonic lexicon suffixes for region '{regionId}'.");
            }

            previousSuffix = suffix;
        }
    }

    private void ValidatePostingsRegion(
        IndexRegionPair pair,
        CancellationToken cancellationToken)
    {
        var lexiconRegion = GetRegion(pair.Lexicon);
        var postingsRegion = GetRegion(pair.Postings);
        if (lexiconRegion.Length > int.MaxValue || postingsRegion.Length > int.MaxValue)
        {
            throw new InvalidDataException($"Index regions for '{pair.Lexicon}' exceed the supported length.");
        }

        var lexicon = new ReadOnlySpan<byte>(BasePointer + lexiconRegion.Offset, (int)lexiconRegion.Length);
        var entryCount = LittleEndianCodec.ReadInt32(lexicon);
        var suffixSize = SegmentFormat.GetLexiconSuffixSize(pair.Lexicon);
        var entrySize = SegmentFormat.GetLexiconEntrySize(pair.Lexicon);
        for (var entry = 0; entry < entryCount; entry++)
        {
            if ((entry & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var entryOffset = SegmentFormat.LexiconHeaderSize + entry * entrySize;
            var postingsOffset = LittleEndianCodec.ReadInt32(lexicon.Slice(entryOffset + suffixSize, 4));
            var postingsBytes = LittleEndianCodec.ReadInt32(lexicon.Slice(entryOffset + suffixSize + 4, 4));
            var documentFrequency = LittleEndianCodec.ReadInt32(lexicon.Slice(entryOffset + suffixSize + 8, 4));
            if (postingsOffset < 0 ||
                postingsBytes < 0 ||
                postingsOffset > postingsRegion.Length ||
                postingsBytes > postingsRegion.Length - postingsOffset)
            {
                throw new InvalidDataException($"Invalid postings bounds in region '{pair.Postings}'.");
            }

            PostingsCodec.Validate(
                new ReadOnlySpan<byte>(BasePointer + postingsRegion.Offset + postingsOffset, postingsBytes),
                documentFrequency,
                DocumentCount);
        }
    }

    private static int ReadLexiconSuffix(ReadOnlySpan<byte> source)
    {
        return source.Length == 1
            ? source[0]
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(source);
    }

    private void ValidateStatsRegion(int version)
    {
        var region = GetRegion(SegmentRegionId.Stats);
        var validLength = version == SegmentFormat.FormatVersion
            ? region.Length == SegmentFormat.StatsLength
            : region.Length is SegmentFormat.PreviousStatsPrefixLength or SegmentFormat.PreviousStatsLength;
        if (!validLength)
        {
            throw new InvalidDataException("Invalid Sift stats region length.");
        }

        var stats = new ReadOnlySpan<byte>(BasePointer + region.Offset, (int)region.Length);
        if (LittleEndianCodec.ReadInt32(stats) != DocumentCount ||
            LittleEndianCodec.ReadInt32(stats[4..]) != ContentBytes)
        {
            throw new InvalidDataException("Sift statistics do not match segment metadata.");
        }

        _ = GetNonAsciiDocumentStats();
    }

    internal NonAsciiDocumentStats GetNonAsciiDocumentStats()
    {
        var region = GetRegion(SegmentRegionId.Stats);
        var stats = new ReadOnlySpan<byte>(BasePointer + region.Offset, (int)region.Length);
        // The oldest version-3 layout has no Unicode counters. Preserve its
        // conservative estimate without rescanning document rows.
        if (stats.Length == SegmentFormat.PreviousStatsPrefixLength)
            return new NonAsciiDocumentStats(DocumentCount, ContentBytes);

        var nonAsciiOffset = stats.Length == SegmentFormat.StatsLength
            ? SegmentFormat.NonAsciiDocumentCountOffset
            : SegmentFormat.PreviousStatsPrefixLength;
        var documentCount = LittleEndianCodec.ReadInt32(stats[nonAsciiOffset..]);
        var contentBytes = LittleEndianCodec.ReadInt64(stats[(nonAsciiOffset + 4)..]);
        if (documentCount < 0 || documentCount > DocumentCount ||
            contentBytes < 0 || contentBytes > ContentBytes)
        {
            throw new InvalidDataException("Invalid non-ASCII Sift document statistics.");
        }

        return new NonAsciiDocumentStats(documentCount, contentBytes);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_pointer is not null)
        {
            _view?.SafeMemoryMappedViewHandle.ReleasePointer();
            _pointer = null;
        }

        _memoryHandle.Dispose();
        Interlocked.Add(ref s_mappedBytes, -_mappedLength);
        Interlocked.Decrement(ref s_openSegments);
        _view?.Dispose();
        _mappedFile?.Dispose();
    }

    public static long CurrentMappedBytes => Interlocked.Read(ref s_mappedBytes);
    public static int CurrentOpenSegments => Volatile.Read(ref s_openSegments);

    private byte* BasePointer => _pointer;

    private (long Offset, long Length) GetRegion(SegmentRegionId regionId) => _regions[(int)regionId];

    private uint LowerBound(string value)
    {
        var low = 0;
        var high = DocumentCount;
        while (low < high)
        {
            var mid = low + ((high - low) >> 1);
            var document = GetDocument((uint)mid);
            if (ComparePathOrdinal(document, value) < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return (uint)low;
    }

    private static string? GetExclusivePrefixUpperBound(string prefix)
    {
        var upperBound = prefix.ToCharArray();
        for (var i = upperBound.Length - 1; i >= 0; i--)
        {
            if (upperBound[i] == char.MaxValue)
            {
                continue;
            }

            upperBound[i]++;
            return new string(upperBound, 0, i + 1);
        }

        return null;
    }

    private void ValidateRegionChecksum(
        SegmentRegionId regionId,
        ulong expected,
        CancellationToken cancellationToken)
    {
        var region = GetRegion(regionId);
        if (region.Length > int.MaxValue)
        {
            throw new InvalidDataException($"Region '{regionId}' exceeds the supported checksum length.");
        }

        var span = new ReadOnlySpan<byte>(BasePointer + region.Offset, checked((int)region.Length));
        var actual = Hashing.Fnv1A64(span, cancellationToken);
        if (actual != expected)
        {
            throw new InvalidDataException($"Checksum mismatch for region '{regionId}'.");
        }
    }

    private T Read<T>(long offset) where T : unmanaged
    {
        return Unsafe.ReadUnaligned<T>(BasePointer + offset);
    }

    private static void ValidateTableBounds(long offset, long length, long fileLength, string name)
    {
        if (offset < 0 || length < 0 || offset > fileLength || length > fileLength - offset)
        {
            throw new InvalidDataException($"Invalid {name} bounds.");
        }
    }

    private void Initialize(
        bool validateChecksums,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fileLength = _fileLength;
        if (fileLength < SegmentFormat.HeaderSize)
        {
            throw new InvalidDataException("Sift segment header is truncated.");
        }

        if (fileLength > int.MaxValue)
        {
            throw new InvalidDataException("Sift segment exceeds the supported length.");
        }

        if (Read<ulong>(0) != SegmentFormat.SegmentMagic)
        {
            throw new InvalidDataException("Invalid Sift segment magic.");
        }

        var version = Read<int>(8);
        if (version is not SegmentFormat.FormatVersion and not SegmentFormat.PreviousFormatVersion)
        {
            throw new InvalidDataException($"Unsupported Sift segment version: {version}.");
        }

        SegmentId = new SegmentId(Read<ulong>(12));
        BuildTimestampTicks = Read<long>(20);
        DocumentCount = Read<int>(28);
        ContentBytes = Read<long>(32);
        RegionCount = Read<int>(40);
        if (DocumentCount < 0 || ContentBytes < 0)
        {
            throw new InvalidDataException("Invalid Sift segment document metadata.");
        }

        if (RegionCount != SegmentFormat.RegionCount)
        {
            throw new InvalidDataException($"Unexpected Sift region count: {RegionCount}.");
        }

        var regionDirectoryOffset = Read<long>(44);
        var checksumTableOffset = Read<long>(52);
        ValidateTableBounds(regionDirectoryOffset, RegionCount * SegmentFormat.RegionDirectoryEntrySize, fileLength, "region directory");
        ValidateTableBounds(checksumTableOffset, RegionCount * SegmentFormat.ChecksumEntrySize, fileLength, "checksum table");

        var cursor = regionDirectoryOffset;
        ulong seenRegionIds = 0;
        for (var i = 0; i < RegionCount; i++)
        {
            var id = (SegmentRegionId)Read<int>(cursor);
            if (!Enum.IsDefined(id))
            {
                throw new InvalidDataException($"Invalid region id '{id}'.");
            }

            var idMask = 1UL << (int)id;
            if ((seenRegionIds & idMask) != 0)
            {
                throw new InvalidDataException($"Duplicate region id '{id}'.");
            }

            var regionOffset = Read<long>(cursor + 4);
            var regionLength = Read<long>(cursor + 12);
            ValidateTableBounds(regionOffset, regionLength, fileLength, $"region '{id}'");
            seenRegionIds |= idMask;
            _regions[(int)id] = (regionOffset, regionLength);
            cursor += SegmentFormat.RegionDirectoryEntrySize;
        }

        var expectedRegionIds = ((1UL << (SegmentFormat.RegionCount + 1)) - 1) & ~1UL;
        if (seenRegionIds != expectedRegionIds)
        {
            throw new InvalidDataException("The Sift segment region directory is incomplete.");
        }

        var checksumCursor = checksumTableOffset;
        ulong seenChecksumIds = 0;
        for (var i = 0; i < RegionCount; i++)
        {
            var id = (SegmentRegionId)Read<int>(checksumCursor);
            if (!Enum.IsDefined(id))
            {
                throw new InvalidDataException($"Invalid checksum region id '{id}'.");
            }

            var idMask = 1UL << (int)id;
            if ((seenChecksumIds & idMask) != 0 ||
                (seenRegionIds & idMask) == 0)
            {
                throw new InvalidDataException($"Invalid or duplicate checksum region id '{id}'.");
            }

            seenChecksumIds |= idMask;

            if (validateChecksums)
            {
                var expected = Read<ulong>(checksumCursor + 4);
                ValidateRegionChecksum(id, expected, cancellationToken);
            }

            checksumCursor += SegmentFormat.ChecksumEntrySize;
        }

        if (seenChecksumIds != expectedRegionIds)
        {
            throw new InvalidDataException("The Sift segment checksum table is incomplete.");
        }

        ValidateCanonicalLayout(regionDirectoryOffset, checksumTableOffset, fileLength);

        var documentTable = GetRegion(SegmentRegionId.DocumentTable);
        var expectedDocumentTableLength = checked((long)DocumentCount * SegmentFormat.DocumentRowSize);
        if (documentTable.Length != expectedDocumentTableLength)
        {
            throw new InvalidDataException("Invalid Sift document table length.");
        }

        if (GetRegion(SegmentRegionId.ContentBlob).Length != ContentBytes)
        {
            throw new InvalidDataException("Invalid Sift content length.");
        }

        ValidateStatsRegion(version);

        ValidateLexiconRegion(SegmentRegionId.ContentTriCsLex, validateChecksums, cancellationToken);
        ValidateLexiconRegion(SegmentRegionId.ContentTriIcLex, validateChecksums, cancellationToken);
        ValidateLexiconRegion(SegmentRegionId.ContentBiCsLex, validateChecksums, cancellationToken);
        ValidateLexiconRegion(SegmentRegionId.ContentBiIcLex, validateChecksums, cancellationToken);
        ValidateLexiconRegion(SegmentRegionId.PathTriCsLex, validateChecksums, cancellationToken);
        ValidateLexiconRegion(SegmentRegionId.PathTriIcLex, validateChecksums, cancellationToken);
        if (validateChecksums)
        {
            ValidateDocuments(cancellationToken);
            ValidatePostingsRegion(
                new IndexRegionPair(SegmentRegionId.ContentTriCsLex, SegmentRegionId.ContentTriCsPost),
                cancellationToken);
            ValidatePostingsRegion(
                new IndexRegionPair(SegmentRegionId.ContentTriIcLex, SegmentRegionId.ContentTriIcPost),
                cancellationToken);
            ValidatePostingsRegion(
                new IndexRegionPair(SegmentRegionId.ContentBiCsLex, SegmentRegionId.ContentBiCsPost),
                cancellationToken);
            ValidatePostingsRegion(
                new IndexRegionPair(SegmentRegionId.ContentBiIcLex, SegmentRegionId.ContentBiIcPost),
                cancellationToken);
            ValidatePostingsRegion(
                new IndexRegionPair(SegmentRegionId.PathTriCsLex, SegmentRegionId.PathTriCsPost),
                cancellationToken);
            ValidatePostingsRegion(
                new IndexRegionPair(SegmentRegionId.PathTriIcLex, SegmentRegionId.PathTriIcPost),
                cancellationToken);
        }
    }

    private void ValidateCanonicalLayout(
        long regionDirectoryOffset,
        long checksumTableOffset,
        long fileLength)
    {
        var expectedOffset = SegmentFormat.HeaderSize +
            (long)RegionCount * SegmentFormat.RegionDirectoryEntrySize;
        if (regionDirectoryOffset != SegmentFormat.HeaderSize)
        {
            throw new InvalidDataException("Invalid Sift region directory position.");
        }

        for (var regionId = 1; regionId <= SegmentFormat.RegionCount; regionId++)
        {
            var region = _regions[regionId];
            if (region.Offset != expectedOffset)
            {
                throw new InvalidDataException("Sift segment regions are not contiguous and ordered.");
            }

            expectedOffset = checked(expectedOffset + region.Length);
        }

        var checksumTableLength = (long)RegionCount * SegmentFormat.ChecksumEntrySize;
        if (checksumTableOffset != expectedOffset)
        {
            throw new InvalidDataException(
                $"Invalid Sift checksum table position: expected {expectedOffset}, found {checksumTableOffset}.");
        }

        if (fileLength != checksumTableOffset + checksumTableLength)
        {
            throw new InvalidDataException(
                $"Invalid Sift segment length: expected {checksumTableOffset + checksumTableLength}, found {fileLength}.");
        }
    }

    private void ValidateDocuments(CancellationToken cancellationToken)
    {
        var pathBlob = GetRegion(SegmentRegionId.PathBlob);
        var contentBlob = GetRegion(SegmentRegionId.ContentBlob);
        var lineIndexBlob = GetRegion(SegmentRegionId.LineIndexBlob);
        string? previousPath = null;
        for (uint ordinal = 0; ordinal < DocumentCount; ordinal++)
        {
            if ((ordinal & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var document = GetDocument(ordinal);
            ValidateDocumentSlice(document.PathOffset, document.PathLength, pathBlob.Length, "path");
            ValidateDocumentSlice(document.ContentOffset, document.ContentLength, contentBlob.Length, "content");
            ValidateDocumentSlice(document.LineIndexOffset, document.LineIndexLength, lineIndexBlob.Length, "line index");
            if (document.LineCount < 0)
            {
                throw new InvalidDataException($"Invalid line count for document ordinal {ordinal}.");
            }

            string path;
            try
            {
                path = StrictUtf8.GetString(GetPathBytes(document));
                if (!string.Equals(DocumentPath.Normalize(path), path, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"Non-normalized path for document ordinal {ordinal}.");
                }
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException($"Invalid UTF-8 path for document ordinal {ordinal}.", exception);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException($"Invalid path for document ordinal {ordinal}.", exception);
            }

            if (previousPath is not null && string.CompareOrdinal(previousPath, path) >= 0)
            {
                throw new InvalidDataException("Sift segment document paths are not strictly ordered.");
            }

            previousPath = path;
            LineMap.Validate(
                GetEncodedLineIndexBytes(document),
                GetContentBytes(document),
                document.LineCount,
                cancellationToken);
        }
    }

    private static void ValidateDocumentSlice(ulong offset, int length, long regionLength, string name)
    {
        if (length < 0 ||
            offset > (ulong)regionLength ||
            (ulong)length > (ulong)regionLength - offset)
        {
            throw new InvalidDataException($"Invalid Sift document {name} bounds.");
        }
    }

    private void ValidateDocumentFrequency(int documentFrequency)
    {
        if (documentFrequency < 0 || documentFrequency > DocumentCount)
        {
            throw new InvalidDataException("Invalid Sift postings document frequency.");
        }
    }
}

internal delegate void PostingsVisitor(ReadOnlySpan<byte> payload, int docFreq);

internal readonly record struct NonAsciiDocumentStats(
    int DocumentCount,
    long ContentBytes);

internal readonly record struct IndexRegionPair(
    SegmentRegionId Lexicon,
    SegmentRegionId Postings);

internal readonly record struct SegmentDocument(
    uint Ordinal,
    ulong PathOffset,
    int PathLength,
    ulong ContentOffset,
    int ContentLength,
    ulong LineIndexOffset,
    int LineIndexLength,
    int LineCount,
    SegmentDocumentFlags Flags,
    long MTimeUtcTicks,
    ulong ContentHash64);

internal sealed unsafe class UnmanagedByteMemoryManager : MemoryManager<byte>
{
    // ReadOnlyMemory must keep the mapped segment alive for as long as its view is reachable.
    private readonly object _owner;
    private readonly byte* _pointer;
    private readonly int _length;

    public UnmanagedByteMemoryManager(object owner, byte* pointer, int length)
    {
        _owner = owner;
        _pointer = pointer;
        _length = length;
    }

    public override Span<byte> GetSpan() => new(_pointer, _length);

    public override MemoryHandle Pin(int elementIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementIndex);
        if (elementIndex > _length)
        {
            throw new ArgumentOutOfRangeException(nameof(elementIndex));
        }

        return new MemoryHandle(_pointer + elementIndex);
    }

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
    }
}
