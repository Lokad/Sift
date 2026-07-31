using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Lokad.Sift.LowLevel;

namespace Lokad.Sift.Storage;

public sealed unsafe class MappedSegment : IDisposable
{
    private static long s_mappedBytes;
    private static int s_openSegments;
    private readonly MemoryMappedFile? _mappedFile;
    private readonly MemoryMappedViewAccessor? _view;
    private MemoryHandle _memoryHandle;
    private byte* _pointer;
    private readonly long _mappedLength;
    private readonly ReadOnlyMemory<byte> _backingMemory;
    private readonly Dictionary<SegmentRegionId, (long Offset, long Length)> _regions = new();
    private int _disposed;

    public MappedSegment(string path)
        : this(path, validateChecksums: true, CancellationToken.None)
    {
    }

    public MappedSegment(string path, bool validateChecksums)
        : this(path, validateChecksums, CancellationToken.None)
    {
    }

    public MappedSegment(
        string path,
        bool validateChecksums,
        CancellationToken cancellationToken)
    {
        var accounted = false;
        try
        {
            _mappedFile = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            _view = _mappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer);
            _pointer += _view.PointerOffset;
            _mappedLength = _view.Capacity;
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

    public MappedSegment(ReadOnlyMemory<byte> bytes)
        : this(bytes, validateChecksums: true, CancellationToken.None)
    {
    }

    public MappedSegment(ReadOnlyMemory<byte> bytes, bool validateChecksums)
        : this(bytes, validateChecksums, CancellationToken.None)
    {
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

    public ulong SegmentId { get; private set; }
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
            Read<ushort>(offset + 40),
            Read<long>(offset + 42),
            Read<ulong>(offset + 50));
    }

    public string GetPath(SegmentDocument document)
    {
        return Encoding.UTF8.GetString(GetPathBytes(document));
    }

    public ReadOnlySpan<byte> GetPathBytes(SegmentDocument document)
    {
        var region = GetRegion(SegmentRegionId.PathBlob);
        return new ReadOnlySpan<byte>(BasePointer + region.Offset + (long)document.PathOffset, document.PathLength);
    }

    public (uint StartInclusive, uint EndExclusive) FindPathPrefixRange(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);

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
        return new ReadOnlySpan<byte>(BasePointer + region.Offset + (long)document.ContentOffset, document.ContentLength);
    }

    public ReadOnlyMemory<byte> GetContentMemory(SegmentDocument document)
    {
        var region = GetRegion(SegmentRegionId.ContentBlob);
        var offset = checked((int)(region.Offset + (long)document.ContentOffset));
        return _mappedFile is null
            ? _backingMemory.Slice(offset, document.ContentLength)
            : new UnmanagedByteMemoryManager(this, BasePointer + offset, document.ContentLength).Memory;
    }

    public bool TryFindDocumentOrdinal(string normalizedPath, out uint ordinal)
    {
        ArgumentNullException.ThrowIfNull(normalizedPath);

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

    public int[] GetLineStarts(SegmentDocument document)
    {
        return LineMap.DecodeLineIndex(GetEncodedLineIndexBytes(document));
    }

    public ReadOnlySpan<byte> GetEncodedLineIndexBytes(SegmentDocument document)
    {
        var region = GetRegion(SegmentRegionId.LineIndexBlob);
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

    public PostingsMetadata[] GetPostingsMetadata(IndexRegionPair pair)
    {
        var lexiconRegion = GetRegion(pair.Lexicon);
        var lexicon = new ReadOnlySpan<byte>(BasePointer + lexiconRegion.Offset, (int)lexiconRegion.Length);
        if (lexicon.Length < SegmentFormat.LexiconHeaderSize)
        {
            return [];
        }

        var entryCount = LittleEndianCodec.ReadInt32(lexicon);
        if (entryCount == 0)
        {
            return [];
        }

        var suffixSize = SegmentFormat.GetLexiconSuffixSize(pair.Lexicon);
        var entrySize = SegmentFormat.GetLexiconEntrySize(pair.Lexicon);
        var results = GC.AllocateUninitializedArray<PostingsMetadata>(entryCount);
        for (var i = 0; i < entryCount; i++)
        {
            var entryOffset = SegmentFormat.LexiconHeaderSize + i * entrySize;
            var postingsBytes = LittleEndianCodec.ReadInt32(lexicon.Slice(entryOffset + suffixSize + 4, 4));
            var docFreq = LittleEndianCodec.ReadInt32(lexicon.Slice(entryOffset + suffixSize + 8, 4));
            results[i] = new PostingsMetadata(docFreq, postingsBytes);
        }

        return results;
    }

    public void VisitPostings(IndexRegionPair pair, PostingsVisitor visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);

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
        CancellationToken cancellationToken)
    {
        var region = GetRegion(regionId);
        var lexicon = new ReadOnlySpan<byte>(BasePointer + region.Offset, (int)region.Length);
        if (lexicon.Length < SegmentFormat.LexiconHeaderSize)
        {
            throw new InvalidDataException($"Invalid lexicon header for region '{regionId}'.");
        }

        var entryCount = LittleEndianCodec.ReadInt32(lexicon);
        var suffixSize = SegmentFormat.GetLexiconSuffixSize(regionId);
        var entrySize = SegmentFormat.GetLexiconEntrySize(regionId);
        var expectedLength = SegmentFormat.LexiconHeaderSize + entryCount * entrySize;
        if (entryCount < 0 || lexicon.Length != expectedLength)
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

    private static int ReadLexiconSuffix(ReadOnlySpan<byte> source)
    {
        return source.Length == 1
            ? source[0]
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(source);
    }

    public int GetByteFrequency(byte value)
    {
        var region = GetRegion(SegmentRegionId.Stats);
        var stats = new ReadOnlySpan<byte>(BasePointer + region.Offset, (int)region.Length);
        var bucketOffset = 8 + (value * 4);
        if (stats.Length < bucketOffset + 4)
        {
            throw new InvalidDataException("Invalid Sift stats region.");
        }

        return LittleEndianCodec.ReadInt32(stats.Slice(bucketOffset, 4));
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

    private (long Offset, long Length) GetRegion(SegmentRegionId regionId) => _regions[regionId];

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
        var fileLength = _mappedLength;
        if (fileLength < SegmentFormat.HeaderSize)
        {
            throw new InvalidDataException("Sift segment header is truncated.");
        }

        if (Read<ulong>(0) != SegmentFormat.SegmentMagic)
        {
            throw new InvalidDataException("Invalid Sift segment magic.");
        }

        var version = Read<int>(8);
        if (version != SegmentFormat.FormatVersion)
        {
            throw new InvalidDataException($"Unsupported Sift segment version: {version}.");
        }

        SegmentId = Read<ulong>(12);
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
        var seen = new HashSet<SegmentRegionId>();
        for (var i = 0; i < RegionCount; i++)
        {
            var id = (SegmentRegionId)Read<int>(cursor);
            if (!Enum.IsDefined(id) || !seen.Add(id))
            {
                throw new InvalidDataException($"Invalid or duplicate region id '{id}'.");
            }

            var regionOffset = Read<long>(cursor + 4);
            var regionLength = Read<long>(cursor + 12);
            ValidateTableBounds(regionOffset, regionLength, fileLength, $"region '{id}'");
            _regions[id] = (regionOffset, regionLength);
            cursor += SegmentFormat.RegionDirectoryEntrySize;
        }

        var checksumCursor = checksumTableOffset;
        var checksumIds = new HashSet<SegmentRegionId>();
        for (var i = 0; i < RegionCount; i++)
        {
            var id = (SegmentRegionId)Read<int>(checksumCursor);
            if (!Enum.IsDefined(id) || !checksumIds.Add(id) || !_regions.ContainsKey(id))
            {
                throw new InvalidDataException($"Invalid or duplicate checksum region id '{id}'.");
            }

            if (validateChecksums)
            {
                var expected = Read<ulong>(checksumCursor + 4);
                ValidateRegionChecksum(id, expected, cancellationToken);
            }

            checksumCursor += SegmentFormat.ChecksumEntrySize;
        }

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

        ValidateLexiconRegion(SegmentRegionId.ContentTriCsLex, cancellationToken);
        ValidateLexiconRegion(SegmentRegionId.ContentTriIcLex, cancellationToken);
        ValidateLexiconRegion(SegmentRegionId.ContentBiCsLex, cancellationToken);
        ValidateLexiconRegion(SegmentRegionId.ContentBiIcLex, cancellationToken);
        ValidateLexiconRegion(SegmentRegionId.PathTriCsLex, cancellationToken);
        ValidateLexiconRegion(SegmentRegionId.PathTriIcLex, cancellationToken);
        if (validateChecksums)
        {
            ValidateDocuments(cancellationToken);
        }
    }

    private void ValidateDocuments(CancellationToken cancellationToken)
    {
        var pathBlob = GetRegion(SegmentRegionId.PathBlob);
        var contentBlob = GetRegion(SegmentRegionId.ContentBlob);
        var lineIndexBlob = GetRegion(SegmentRegionId.LineIndexBlob);
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
}

public delegate void PostingsVisitor(ReadOnlySpan<byte> payload, int docFreq);

public readonly record struct PostingsMetadata(
    int DocumentFrequency,
    int EncodedBytes);

public readonly record struct IndexRegionPair(
    SegmentRegionId Lexicon,
    SegmentRegionId Postings);

public readonly record struct SegmentDocument(
    uint Ordinal,
    ulong PathOffset,
    int PathLength,
    ulong ContentOffset,
    int ContentLength,
    ulong LineIndexOffset,
    int LineIndexLength,
    int LineCount,
    ushort Flags,
    long MTimeUtcTicks,
    ulong ContentHash64);

internal sealed unsafe class UnmanagedByteMemoryManager : MemoryManager<byte>
{
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
