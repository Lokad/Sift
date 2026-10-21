using System.Buffers;
using System.Runtime.InteropServices;
using Lokad.Sift.LowLevel;

namespace Lokad.Sift.Storage;

internal static class SegmentIndexBuilder
{
    private static DirectIndexedGramBuilder? s_bigramBuilder;
    private static DirectIndexedGramBuilder? s_trigramBuilder;
    private static int s_workspaceLeaseCount;

    internal static IDisposable AcquireWorkspaceLease()
    {
        Interlocked.Increment(ref s_workspaceLeaseCount);
        return new WorkspaceLease();
    }

    internal static void ThrowIfWorkspaceLeaseMissing()
    {
        if (Volatile.Read(ref s_workspaceLeaseCount) == 0)
        {
            throw new InvalidOperationException("A Sift indexing workspace lease is required.");
        }
    }

    public static BuiltIndex BuildContentIndex<TDocument>(
        IReadOnlyList<TDocument> documents,
        int gramLength,
        bool caseInsensitive,
        CancellationToken cancellationToken)
        where TDocument : ISegmentBuildDocument
    {
        return (gramLength, caseInsensitive) switch
        {
            (2, false) => BuildContentIndexSensitiveBigram(documents, cancellationToken),
            (2, true) => BuildContentIndexInsensitiveBigram(documents, cancellationToken),
            (3, false) => BuildContentIndexSensitiveTrigram(documents, cancellationToken),
            (3, true) => BuildContentIndexInsensitiveTrigram(documents, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(gramLength))
        };
    }

    private static BuiltIndex BuildContentIndexSensitiveBigram<TDocument>(
        IReadOnlyList<TDocument> documents,
        CancellationToken cancellationToken)
        where TDocument : ISegmentBuildDocument
    {
        var builder = RentBuilder(2);
        try
        {
            CountContentIndexPairsSensitiveBigram(documents, builder, cancellationToken);
            builder.PrepareForFill(cancellationToken);
            FillContentIndexPairsSensitiveBigram(documents, builder, cancellationToken);
            return builder.BuildIndex(2, cancellationToken);
        }
        finally
        {
            ReturnBuilder(builder);
        }
    }

    private static BuiltIndex BuildContentIndexInsensitiveBigram<TDocument>(
        IReadOnlyList<TDocument> documents,
        CancellationToken cancellationToken)
        where TDocument : ISegmentBuildDocument
    {
        var builder = RentBuilder(2);
        try
        {
            CountContentIndexPairsInsensitiveBigram(documents, builder, cancellationToken);
            builder.PrepareForFill(cancellationToken);
            FillContentIndexPairsInsensitiveBigram(documents, builder, cancellationToken);
            return builder.BuildIndex(2, cancellationToken);
        }
        finally
        {
            ReturnBuilder(builder);
        }
    }

    private static BuiltIndex BuildContentIndexSensitiveTrigram<TDocument>(
        IReadOnlyList<TDocument> documents,
        CancellationToken cancellationToken)
        where TDocument : ISegmentBuildDocument
    {
        var builder = RentBuilder(3);
        try
        {
            CountContentIndexPairsSensitiveTrigram(documents, builder, cancellationToken);
            builder.PrepareForFill(cancellationToken);
            FillContentIndexPairsSensitiveTrigram(documents, builder, cancellationToken);
            return builder.BuildIndex(3, cancellationToken);
        }
        finally
        {
            ReturnBuilder(builder);
        }
    }

    private static BuiltIndex BuildContentIndexInsensitiveTrigram<TDocument>(
        IReadOnlyList<TDocument> documents,
        CancellationToken cancellationToken)
        where TDocument : ISegmentBuildDocument
    {
        var builder = RentBuilder(3);
        try
        {
            CountContentIndexPairsInsensitiveTrigram(documents, builder, cancellationToken);
            builder.PrepareForFill(cancellationToken);
            FillContentIndexPairsInsensitiveTrigram(documents, builder, cancellationToken);
            return builder.BuildIndex(3, cancellationToken);
        }
        finally
        {
            ReturnBuilder(builder);
        }
    }

    public static BuiltIndex BuildPathTrigramIndex<TDocument>(
        IReadOnlyList<TDocument> documents,
        CancellationToken cancellationToken)
        where TDocument : ISegmentBuildDocument
    {
        var builder = RentBuilder(3);
        try
        {
            CountPathIndexPairs(documents, builder, cancellationToken);
            builder.PrepareForFill(cancellationToken);
            FillPathIndexPairs(documents, builder, cancellationToken);
            return builder.BuildIndex(3, cancellationToken);
        }
        finally
        {
            ReturnBuilder(builder);
        }
    }

    private static void CountContentIndexPairsSensitiveBigram<TDocument>(
        IReadOnlyList<TDocument> documents,
        DirectIndexedGramBuilder builder,
        CancellationToken cancellationToken)
        where TDocument : ISegmentBuildDocument
    {
        for (uint ordinal = 0; ordinal < documents.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.StartDocument();
            var content = documents[(int)ordinal].GetContentBytes();
            if (content.Length < 2)
            {
                continue;
            }

            for (var i = 0; i < content.Length - 1; i++)
            {
                ThrowIfCancellationRequested(cancellationToken, i);
                builder.Count(EncodeBigramSensitive(content, i));
            }
        }
    }

    private static void FillContentIndexPairsSensitiveBigram<TDocument>(
        IReadOnlyList<TDocument> documents,
        DirectIndexedGramBuilder builder,
        CancellationToken cancellationToken)
        where TDocument : ISegmentBuildDocument
    {
        for (uint ordinal = 0; ordinal < documents.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.StartDocument();
            var content = documents[(int)ordinal].GetContentBytes();
            if (content.Length < 2)
            {
                continue;
            }

            for (var i = 0; i < content.Length - 1; i++)
            {
                ThrowIfCancellationRequested(cancellationToken, i);
                builder.AddOrdinal(EncodeBigramSensitive(content, i), ordinal);
            }
        }
    }

    private static void CountContentIndexPairsInsensitiveBigram<TDocument>(
        IReadOnlyList<TDocument> documents,
        DirectIndexedGramBuilder builder,
        CancellationToken cancellationToken)
        where TDocument : ISegmentBuildDocument
    {
        for (uint ordinal = 0; ordinal < documents.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.StartDocument();
            var content = documents[(int)ordinal].GetContentBytes();
            if (content.Length < 2)
            {
                continue;
            }

            for (var i = 0; i < content.Length - 1; i++)
            {
                ThrowIfCancellationRequested(cancellationToken, i);
                if (!TryEncodeBigramInsensitiveAscii(content, i, out var gram))
                {
                    continue;
                }

                builder.Count(gram);
            }
        }
    }

    private static void FillContentIndexPairsInsensitiveBigram<TDocument>(
        IReadOnlyList<TDocument> documents,
        DirectIndexedGramBuilder builder,
        CancellationToken cancellationToken)
        where TDocument : ISegmentBuildDocument
    {
        for (uint ordinal = 0; ordinal < documents.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.StartDocument();
            var content = documents[(int)ordinal].GetContentBytes();
            if (content.Length < 2)
            {
                continue;
            }

            for (var i = 0; i < content.Length - 1; i++)
            {
                ThrowIfCancellationRequested(cancellationToken, i);
                if (!TryEncodeBigramInsensitiveAscii(content, i, out var gram))
                {
                    continue;
                }

                builder.AddOrdinal(gram, ordinal);
            }
        }
    }

    private static void CountContentIndexPairsSensitiveTrigram<TDocument>(
        IReadOnlyList<TDocument> documents,
        DirectIndexedGramBuilder builder,
        CancellationToken cancellationToken)
        where TDocument : ISegmentBuildDocument
    {
        for (uint ordinal = 0; ordinal < documents.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.StartDocument();
            var content = documents[(int)ordinal].GetContentBytes();
            if (content.Length < 3)
            {
                continue;
            }

            for (var i = 0; i < content.Length - 2; i++)
            {
                ThrowIfCancellationRequested(cancellationToken, i);
                builder.Count(EncodeTrigramSensitive(content, i));
            }
        }
    }

    private static void FillContentIndexPairsSensitiveTrigram<TDocument>(
        IReadOnlyList<TDocument> documents,
        DirectIndexedGramBuilder builder,
        CancellationToken cancellationToken)
        where TDocument : ISegmentBuildDocument
    {
        for (uint ordinal = 0; ordinal < documents.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.StartDocument();
            var content = documents[(int)ordinal].GetContentBytes();
            if (content.Length < 3)
            {
                continue;
            }

            for (var i = 0; i < content.Length - 2; i++)
            {
                ThrowIfCancellationRequested(cancellationToken, i);
                builder.AddOrdinal(EncodeTrigramSensitive(content, i), ordinal);
            }
        }
    }

    private static void CountContentIndexPairsInsensitiveTrigram<TDocument>(
        IReadOnlyList<TDocument> documents,
        DirectIndexedGramBuilder builder,
        CancellationToken cancellationToken)
        where TDocument : ISegmentBuildDocument
    {
        for (uint ordinal = 0; ordinal < documents.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.StartDocument();
            var content = documents[(int)ordinal].GetContentBytes();
            if (content.Length < 3)
            {
                continue;
            }

            for (var i = 0; i < content.Length - 2; i++)
            {
                ThrowIfCancellationRequested(cancellationToken, i);
                if (!TryEncodeTrigramInsensitiveAscii(content, i, out var gram))
                {
                    continue;
                }

                builder.Count(gram);
            }
        }
    }

    private static void FillContentIndexPairsInsensitiveTrigram<TDocument>(
        IReadOnlyList<TDocument> documents,
        DirectIndexedGramBuilder builder,
        CancellationToken cancellationToken)
        where TDocument : ISegmentBuildDocument
    {
        for (uint ordinal = 0; ordinal < documents.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.StartDocument();
            var content = documents[(int)ordinal].GetContentBytes();
            if (content.Length < 3)
            {
                continue;
            }

            for (var i = 0; i < content.Length - 2; i++)
            {
                ThrowIfCancellationRequested(cancellationToken, i);
                if (!TryEncodeTrigramInsensitiveAscii(content, i, out var gram))
                {
                    continue;
                }

                builder.AddOrdinal(gram, ordinal);
            }
        }
    }

    private static void CountPathIndexPairs<TDocument>(
        IReadOnlyList<TDocument> documents,
        DirectIndexedGramBuilder builder,
        CancellationToken cancellationToken)
        where TDocument : ISegmentBuildDocument
    {
        for (uint ordinal = 0; ordinal < documents.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.StartDocument();
            var path = documents[(int)ordinal].GetPathUtf8();
            if (path.Length < 3)
            {
                continue;
            }

            for (var i = 0; i < path.Length - 2; i++)
            {
                ThrowIfCancellationRequested(cancellationToken, i);
                builder.Count(EncodeTrigramSensitive(path, i));
            }
        }
    }

    private static void FillPathIndexPairs<TDocument>(
        IReadOnlyList<TDocument> documents,
        DirectIndexedGramBuilder builder,
        CancellationToken cancellationToken)
        where TDocument : ISegmentBuildDocument
    {
        for (uint ordinal = 0; ordinal < documents.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.StartDocument();
            var path = documents[(int)ordinal].GetPathUtf8();
            if (path.Length < 3)
            {
                continue;
            }

            for (var i = 0; i < path.Length - 2; i++)
            {
                ThrowIfCancellationRequested(cancellationToken, i);
                builder.AddOrdinal(EncodeTrigramSensitive(path, i), ordinal);
            }
        }
    }

    public static bool TryEncodeGram(ReadOnlySpan<byte> value, bool caseInsensitive, out int gram)
    {
        gram = 0;
        if (value.Length is < 2 or > 3)
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (caseInsensitive)
            {
                if (current > 0x7F)
                {
                    gram = 0;
                    return false;
                }

                current = AsciiCase.Fold(current);
            }

            gram = (gram << 8) | current;
        }

        return true;
    }

    private static void ThrowIfCancellationRequested(
        CancellationToken cancellationToken,
        int offset)
    {
        if ((offset & 0xFFFF) == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static int EncodeBigramSensitive(ReadOnlySpan<byte> value, int start)
    {
        var first = value[start];
        var second = value[start + 1];
        return (first << 8) | second;
    }

    private static bool TryEncodeBigramInsensitiveAscii(ReadOnlySpan<byte> value, int start, out int gram)
    {
        var first = value[start];
        var second = value[start + 1];
        if ((first | second) > 0x7F)
        {
            gram = 0;
            return false;
        }

        first = AsciiCase.Fold(first);
        second = AsciiCase.Fold(second);
        gram = (first << 8) | second;
        return true;
    }

    private static int EncodeTrigramSensitive(ReadOnlySpan<byte> value, int start)
    {
        var first = value[start];
        var second = value[start + 1];
        var third = value[start + 2];
        return (first << 16) | (second << 8) | third;
    }

    private static bool TryEncodeTrigramInsensitiveAscii(ReadOnlySpan<byte> value, int start, out int gram)
    {
        var first = value[start];
        var second = value[start + 1];
        var third = value[start + 2];
        if ((first | second | third) > 0x7F)
        {
            gram = 0;
            return false;
        }

        first = AsciiCase.Fold(first);
        second = AsciiCase.Fold(second);
        third = AsciiCase.Fold(third);
        gram = (first << 16) | (second << 8) | third;
        return true;
    }

    internal static void WriteBucketTable(Span<byte> lexicon, ReadOnlySpan<int> bucketStarts, int entryCount)
    {
        for (var bucket = 0; bucket < SegmentFormat.LexiconBucketCount; bucket++)
        {
            var value = bucket < bucketStarts.Length ? bucketStarts[bucket] : entryCount;
            LittleEndianCodec.WriteInt32(lexicon.Slice(4 + bucket * 4, 4), value);
        }
    }

    internal static int GetBucket(int gram, int gramLength)
    {
        return gramLength == 2
            ? (gram >> 8) & 0xFF
            : (gram >> 16) & 0xFF;
    }

    internal static void WriteSuffix(Span<byte> destination, int gram, int suffixSize)
    {
        if (suffixSize == 1)
        {
            destination[0] = (byte)(gram & 0xFF);
        }
        else
        {
            LittleEndianCodec.WriteUInt16(destination, (ushort)(gram & 0xFFFF));
        }
    }

    private static DirectIndexedGramBuilder RentBuilder(int gramLength)
    {
        ref var pooledBuilder = ref gramLength == 2
            ? ref s_bigramBuilder
            : ref s_trigramBuilder;
        return Interlocked.Exchange(ref pooledBuilder, null) ??
            new DirectIndexedGramBuilder(gramLength);
    }

    private static void ReturnBuilder(DirectIndexedGramBuilder builder)
    {
        builder.ResetForReuse();
        ref var pooledBuilder = ref builder.GramLength == 2
            ? ref s_bigramBuilder
            : ref s_trigramBuilder;
        if (Interlocked.CompareExchange(ref pooledBuilder, builder, null) is not null)
        {
            builder.Dispose();
        }
    }

    private static void ReleaseWorkspaceLease()
    {
        if (Interlocked.Decrement(ref s_workspaceLeaseCount) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref s_bigramBuilder, null)?.Dispose();
        Interlocked.Exchange(ref s_trigramBuilder, null)?.Dispose();
    }

    private sealed class WorkspaceLease : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                ReleaseWorkspaceLease();
            }
        }
    }

}

internal sealed unsafe class DirectIndexedGramBuilder : IDisposable
{
    private static long s_currentNativeWorkspaceBytes;
    private readonly int _domainSize;
    private readonly long _workspaceBytes;
    private void* _workspace;
    private ushort* _counts;
    private ushort* _seen;
    private int* _gramIndexes;
    private int[] _touched;
    private int _touchedCount;
    private ushort _documentStamp;
    private uint[] _ordinals = [];
    private Dictionary<int, int>? _overflowCounts;
    private int[] _docFrequencies = [];
    private int[] _writePositions = [];
    private bool _preparedForFill;
    public int GramLength { get; }

    public DirectIndexedGramBuilder(int gramLength)
    {
        GramLength = gramLength;
        var domainSize = gramLength == 2 ? 1 << 16 : 1 << 24;
        _domainSize = domainSize;
        _workspaceBytes = checked((long)domainSize * 8);
        _workspace = NativeMemory.AllocZeroed((nuint)_workspaceBytes);
        if (_workspace == null)
        {
            throw new OutOfMemoryException();
        }

        Interlocked.Add(ref s_currentNativeWorkspaceBytes, _workspaceBytes);

        // These domain-sized primitive tables are reused across segment indexes.
        // Native storage keeps the fixed workspace out of the managed heap
        // (128 MiB for trigrams, where the distinction matters).
        _counts = (ushort*)_workspace;
        _seen = _counts + domainSize;
        _gramIndexes = (int*)(_seen + domainSize);
        _touched = ArrayPool<int>.Shared.Rent(1024);
    }

    internal static long CurrentNativeWorkspaceBytes =>
        Interlocked.Read(ref s_currentNativeWorkspaceBytes);

    public void StartDocument()
    {
        _documentStamp++;
        if (_documentStamp != 0)
        {
            return;
        }

        new Span<ushort>(_seen, _domainSize).Clear();
        _documentStamp = 1;
    }

    public void Count(int gram)
    {
        if (_seen[gram] == _documentStamp)
        {
            return;
        }

        _seen[gram] = _documentStamp;
        if (_counts[gram] == 0)
        {
            AddTouched(gram);
        }

        if (_counts[gram] != ushort.MaxValue)
        {
            _counts[gram]++;
            return;
        }

        _overflowCounts ??= [];
        _overflowCounts[gram] = _overflowCounts.TryGetValue(gram, out var overflowCount)
            ? overflowCount + 1
            : ushort.MaxValue + 1;
    }

    public void PrepareForFill(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _preparedForFill = true;
        if (_touchedCount == 0)
        {
            return;
        }

        Array.Sort(_touched, 0, _touchedCount);
        cancellationToken.ThrowIfCancellationRequested();
        var totalPairs = 0;
        EnsureLocalCapacity();
        for (var i = 0; i < _touchedCount; i++)
        {
            if ((i & 0x3FF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var gram = _touched[i];
            var docFreq = GetCount(gram);
            _docFrequencies[i] = docFreq;
            _writePositions[i] = totalPairs;
            _gramIndexes[gram] = i;
            totalPairs += docFreq;
        }

        if (_ordinals.Length < totalPairs)
        {
            Return(ref _ordinals);
            _ordinals = ArrayPool<uint>.Shared.Rent(totalPairs);
        }
    }

    public void AddOrdinal(int gram, uint ordinal)
    {
        if (_seen[gram] == _documentStamp)
        {
            return;
        }

        _seen[gram] = _documentStamp;
        if (!_preparedForFill)
        {
            throw new InvalidOperationException("PrepareForFill must run before ordinals are added.");
        }

        _ordinals[_writePositions[_gramIndexes[gram]]++] = ordinal;
    }

    public BuiltIndex BuildIndex(
        int gramLength,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_touchedCount == 0)
        {
            var empty = BuiltIndex.Rent(SegmentFormat.LexiconHeaderSize, postingsLength: 1);
            empty.Lexicon.Clear();
            empty.Postings[0] = 0;
            return empty;
        }

        var bucketStarts = GC.AllocateUninitializedArray<int>(SegmentFormat.LexiconBucketCount);
        Array.Fill(bucketStarts, -1);

        var totalBytes = 0;
        var ordinalsOffset = 0;
        for (var i = 0; i < _touchedCount; i++)
        {
            if ((i & 0x3FF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var gram = _touched[i];
            var docFreq = _docFrequencies[i];
            var bucket = SegmentIndexBuilder.GetBucket(gram, gramLength);
            if (bucketStarts[bucket] < 0)
            {
                bucketStarts[bucket] = i;
            }

            var postingsBytes = PostingsCodec.GetEncodedLength(_ordinals.AsSpan(ordinalsOffset, docFreq));
            totalBytes = checked(totalBytes + postingsBytes);
            ordinalsOffset += docFreq;
        }

        bucketStarts[^1] = _touchedCount;
        for (var bucket = bucketStarts.Length - 2; bucket >= 0; bucket--)
        {
            if (bucketStarts[bucket] < 0)
            {
                bucketStarts[bucket] = bucketStarts[bucket + 1];
            }
        }

        var suffixSize = gramLength == 2 ? 1 : 2;
        var entrySize = suffixSize + 12;
        var lexiconLength = SegmentFormat.LexiconHeaderSize + _touchedCount * entrySize;
        var index = BuiltIndex.Rent(lexiconLength, totalBytes);
        try
        {
            var lexicon = index.Lexicon;
            LittleEndianCodec.WriteInt32(lexicon, _touchedCount);
            SegmentIndexBuilder.WriteBucketTable(lexicon, bucketStarts, _touchedCount);
            var postingsBlob = index.Postings;
            var postingsOffset = 0;
            ordinalsOffset = 0;
            for (var i = 0; i < _touchedCount; i++)
            {
                if ((i & 0x3FF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var gram = _touched[i];
                var docFreq = _docFrequencies[i];
                var postingsBytes = PostingsCodec.Encode(
                    _ordinals.AsSpan(ordinalsOffset, docFreq),
                    postingsBlob[postingsOffset..]);
                var entryOffset = SegmentFormat.LexiconHeaderSize + i * entrySize;
                SegmentIndexBuilder.WriteSuffix(lexicon.Slice(entryOffset, suffixSize), gram, suffixSize);
                LittleEndianCodec.WriteInt32(lexicon.Slice(entryOffset + suffixSize, 4), postingsOffset);
                LittleEndianCodec.WriteInt32(lexicon.Slice(entryOffset + suffixSize + 4, 4), postingsBytes);
                LittleEndianCodec.WriteInt32(lexicon.Slice(entryOffset + suffixSize + 8, 4), docFreq);
                postingsOffset = checked(postingsOffset + postingsBytes);
                ordinalsOffset += docFreq;
            }

            if (postingsOffset != totalBytes)
            {
                throw new InvalidOperationException("The encoded postings length does not match its allocation.");
            }

            return index;
        }
        catch
        {
            index.Dispose();
            throw;
        }
    }

    private void AddTouched(int gram)
    {
        if (_touchedCount == _touched.Length)
        {
            var expanded = ArrayPool<int>.Shared.Rent(_touched.Length * 2);
            _touched.AsSpan(0, _touchedCount).CopyTo(expanded);
            ArrayPool<int>.Shared.Return(_touched);
            _touched = expanded;
        }

        _touched[_touchedCount++] = gram;
    }

    public void ResetForReuse()
    {
        for (var i = 0; i < _touchedCount; i++)
        {
            _counts[_touched[i]] = 0;
        }

        _overflowCounts = null;
        Return(ref _ordinals);
        Return(ref _docFrequencies);
        Return(ref _writePositions);
        ArrayPool<int>.Shared.Return(_touched);
        _touched = ArrayPool<int>.Shared.Rent(1024);

        _touchedCount = 0;
        _preparedForFill = false;
    }

    ~DirectIndexedGramBuilder()
    {
        ReleaseWorkspace();
    }

    public void Dispose()
    {
        Return(ref _ordinals);
        Return(ref _docFrequencies);
        Return(ref _writePositions);
        if (_touched.Length > 0)
        {
            ArrayPool<int>.Shared.Return(_touched);
            _touched = [];
        }

        ReleaseWorkspace();
        GC.SuppressFinalize(this);
    }

    private int GetCount(int gram)
    {
        if (_counts[gram] != ushort.MaxValue || _overflowCounts is null)
        {
            return _counts[gram];
        }

        return _overflowCounts.TryGetValue(gram, out var overflowCount)
            ? overflowCount
            : _counts[gram];
    }

    private void EnsureLocalCapacity()
    {
        EnsureCapacity(ref _docFrequencies, _touchedCount);
        EnsureCapacity(ref _writePositions, _touchedCount);
    }

    private static void EnsureCapacity(ref int[] buffer, int length)
    {
        if (buffer.Length >= length)
        {
            return;
        }

        Return(ref buffer);
        buffer = ArrayPool<int>.Shared.Rent(length);
    }

    private static void Return(ref int[] buffer)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        ArrayPool<int>.Shared.Return(buffer);
        buffer = [];
    }

    private static void Return(ref uint[] buffer)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        ArrayPool<uint>.Shared.Return(buffer);
        buffer = [];
    }

    private void ReleaseWorkspace()
    {
        if (_workspace == null)
        {
            return;
        }

        NativeMemory.Free(_workspace);
        _workspace = null;
        Interlocked.Add(ref s_currentNativeWorkspaceBytes, -_workspaceBytes);
        _counts = null;
        _seen = null;
        _gramIndexes = null;
    }
}

internal sealed class BuiltIndex : IDisposable
{
    private byte[] _lexicon;
    private byte[] _postings;

    private BuiltIndex(byte[] lexicon, int lexiconLength, byte[] postings, int postingsLength)
    {
        _lexicon = lexicon;
        _postings = postings;
        LexiconLength = lexiconLength;
        PostingsLength = postingsLength;
    }

    public int LexiconLength { get; }
    public int PostingsLength { get; }
    public Span<byte> Lexicon => _lexicon.AsSpan(0, LexiconLength);
    public Span<byte> Postings => _postings.AsSpan(0, PostingsLength);

    public static BuiltIndex Rent(int lexiconLength, int postingsLength)
    {
        return new BuiltIndex(
            ArrayPool<byte>.Shared.Rent(lexiconLength),
            lexiconLength,
            ArrayPool<byte>.Shared.Rent(postingsLength),
            postingsLength);
    }

    public void Dispose()
    {
        var lexicon = Interlocked.Exchange(ref _lexicon, []);
        if (lexicon.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(lexicon);
        }

        var postings = Interlocked.Exchange(ref _postings, []);
        if (postings.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(postings);
        }
    }
}

