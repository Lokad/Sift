using System.Buffers;
using Lokad.Sift.LowLevel;

namespace Lokad.Sift.Storage;

internal static class SegmentIndexBuilder
{
    private static DirectIndexedGramBuilder? s_bigramBuilder;
    private static DirectIndexedGramBuilder? s_trigramBuilder;

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
        bool caseInsensitive,
        CancellationToken cancellationToken)
        where TDocument : ISegmentBuildDocument
    {
        var builder = RentBuilder(3);
        try
        {
            CountPathIndexPairs(documents, caseInsensitive, builder, cancellationToken);
            builder.PrepareForFill(cancellationToken);
            FillPathIndexPairs(documents, caseInsensitive, builder, cancellationToken);
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
                if (!TryEncodeBigramSensitive(content, i, out var gram))
                {
                    continue;
                }

                builder.Count(gram);
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
                if (!TryEncodeBigramSensitive(content, i, out var gram))
                {
                    continue;
                }

                builder.AddOrdinal(gram, ordinal);
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
                if (!TryEncodeTrigramSensitive(content, i, out var gram))
                {
                    continue;
                }

                builder.Count(gram);
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
                if (!TryEncodeTrigramSensitive(content, i, out var gram))
                {
                    continue;
                }

                builder.AddOrdinal(gram, ordinal);
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
        bool caseInsensitive,
        DirectIndexedGramBuilder builder,
        CancellationToken cancellationToken)
        where TDocument : ISegmentBuildDocument
    {
        if (caseInsensitive)
        {
            CountPathIndexPairsInsensitive(documents, builder, cancellationToken);
        }
        else
        {
            CountPathIndexPairsSensitive(documents, builder, cancellationToken);
        }
    }

    private static void FillPathIndexPairs<TDocument>(
        IReadOnlyList<TDocument> documents,
        bool caseInsensitive,
        DirectIndexedGramBuilder builder,
        CancellationToken cancellationToken)
        where TDocument : ISegmentBuildDocument
    {
        if (caseInsensitive)
        {
            FillPathIndexPairsInsensitive(documents, builder, cancellationToken);
        }
        else
        {
            FillPathIndexPairsSensitive(documents, builder, cancellationToken);
        }
    }

    private static void CountPathIndexPairsSensitive<TDocument>(
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
                if (!TryEncodeTrigramSensitive(path, i, out var gram))
                {
                    continue;
                }

                builder.Count(gram);
            }
        }
    }

    private static void FillPathIndexPairsSensitive<TDocument>(
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
                if (!TryEncodeTrigramSensitive(path, i, out var gram))
                {
                    continue;
                }

                builder.AddOrdinal(gram, ordinal);
            }
        }
    }

    private static void CountPathIndexPairsInsensitive<TDocument>(
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
                if (!TryEncodeTrigramInsensitiveAscii(path, i, out var gram))
                {
                    continue;
                }

                builder.Count(gram);
            }
        }
    }

    private static void FillPathIndexPairsInsensitive<TDocument>(
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
                if (!TryEncodeTrigramInsensitiveAscii(path, i, out var gram))
                {
                    continue;
                }

                builder.AddOrdinal(gram, ordinal);
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

                current = FoldAscii(current);
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

    private static bool TryEncodeBigram(ReadOnlySpan<byte> value, int start, bool caseInsensitive, out int gram)
    {
        return caseInsensitive
            ? TryEncodeBigramInsensitiveAscii(value, start, out gram)
            : TryEncodeBigramSensitive(value, start, out gram);
    }

    private static bool TryEncodeBigramSensitive(ReadOnlySpan<byte> value, int start, out int gram)
    {
        var first = value[start];
        var second = value[start + 1];
        gram = (first << 8) | second;
        return true;
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

        first = FoldAscii(first);
        second = FoldAscii(second);
        gram = (first << 8) | second;
        return true;
    }

    private static bool TryEncodeTrigramSensitive(ReadOnlySpan<byte> value, int start, out int gram)
    {
        var first = value[start];
        var second = value[start + 1];
        var third = value[start + 2];
        gram = (first << 16) | (second << 8) | third;
        return true;
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

        first = FoldAscii(first);
        second = FoldAscii(second);
        third = FoldAscii(third);
        gram = (first << 16) | (second << 8) | third;
        return true;
    }

    public static byte FoldAscii(byte value)
    {
        return (byte)(value is >= (byte)'A' and <= (byte)'Z' ? value + 32 : value);
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
        _ = Interlocked.CompareExchange(ref pooledBuilder, builder, null);
    }

}

internal sealed class DirectIndexedGramBuilder
{
    private readonly ushort[] _counts;
    private readonly ushort[] _seen;
    private int[] _touched;
    private int _touchedCount;
    private ushort _documentStamp;
    private uint[] _ordinals = [];
    private Dictionary<int, int>? _overflowCounts;
    private int[] _gramIndexes = [];
    private int[] _docFrequencies = [];
    private int[] _writePositions = [];
    public int GramLength { get; }

    public DirectIndexedGramBuilder(int gramLength)
    {
        GramLength = gramLength;
        var domainSize = gramLength == 2 ? 1 << 16 : 1 << 24;
        _counts = GC.AllocateUninitializedArray<ushort>(domainSize);
        _seen = GC.AllocateUninitializedArray<ushort>(domainSize);
        Array.Clear(_counts);
        Array.Clear(_seen);
        _touched = ArrayPool<int>.Shared.Rent(1024);
    }

    public void StartDocument()
    {
        _documentStamp++;
        if (_documentStamp != 0)
        {
            return;
        }

        Array.Clear(_seen);
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
        if (_touchedCount == 0)
        {
            return;
        }

        Array.Sort(_touched, 0, _touchedCount);
        cancellationToken.ThrowIfCancellationRequested();
        var totalPairs = 0;
        EnsureLocalCapacity();
        EnsureCapacity(ref _gramIndexes, _counts.Length);
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
        if (_gramIndexes.Length == 0)
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

        var entries = new List<IndexEntry>(_touchedCount);
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
                bucketStarts[bucket] = entries.Count;
            }

            var postingsBytes = PostingsCodec.GetEncodedLength(_ordinals.AsSpan(ordinalsOffset, docFreq));
            entries.Add(new IndexEntry(gram, totalBytes, postingsBytes, docFreq));
            totalBytes = checked(totalBytes + postingsBytes);
            ordinalsOffset += docFreq;
        }

        bucketStarts[^1] = entries.Count;
        for (var bucket = bucketStarts.Length - 2; bucket >= 0; bucket--)
        {
            if (bucketStarts[bucket] < 0)
            {
                bucketStarts[bucket] = bucketStarts[bucket + 1];
            }
        }

        for (var i = 0; i < _touchedCount; i++)
        {
            if ((i & 0x3FF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var gram = _touched[i];
            _counts[gram] = 0;
        }

        _overflowCounts?.Clear();
        _touchedCount = 0;

        var suffixSize = gramLength == 2 ? 1 : 2;
        var entrySize = suffixSize + 12;
        var lexiconLength = SegmentFormat.LexiconHeaderSize + entries.Count * entrySize;
        var index = BuiltIndex.Rent(lexiconLength, totalBytes);
        var lexicon = index.Lexicon;
        LittleEndianCodec.WriteInt32(lexicon, entries.Count);
        SegmentIndexBuilder.WriteBucketTable(lexicon, bucketStarts, entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            if ((i & 0x3FF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var entryOffset = SegmentFormat.LexiconHeaderSize + i * entrySize;
            var entry = entries[i];
            SegmentIndexBuilder.WriteSuffix(lexicon.Slice(entryOffset, suffixSize), entry.Gram, suffixSize);
            LittleEndianCodec.WriteInt32(lexicon.Slice(entryOffset + suffixSize, 4), entry.PostingsOffset);
            LittleEndianCodec.WriteInt32(lexicon.Slice(entryOffset + suffixSize + 4, 4), entry.PostingsBytes);
            LittleEndianCodec.WriteInt32(lexicon.Slice(entryOffset + suffixSize + 8, 4), entry.DocFreq);
        }

        var postingsBlob = index.Postings;
        ordinalsOffset = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            if ((i & 0x3FF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var entry = entries[i];
            PostingsCodec.Encode(
                _ordinals.AsSpan(ordinalsOffset, entry.DocFreq),
                postingsBlob.Slice(entry.PostingsOffset, entry.PostingsBytes));
            ordinalsOffset += entry.DocFreq;
        }

        return index;
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
        Return(ref _gramIndexes);
        Return(ref _docFrequencies);
        Return(ref _writePositions);
        ArrayPool<int>.Shared.Return(_touched);
        _touched = ArrayPool<int>.Shared.Rent(1024);

        _touchedCount = 0;
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

internal readonly record struct IndexEntry(
    int Gram,
    int PostingsOffset,
    int PostingsBytes,
    int DocFreq);

