using System.Runtime.InteropServices;
using Lokad.Sift.LowLevel;

namespace Lokad.Sift.Storage;

internal static class SegmentIndexBuilder
{
    private static DirectIndexedGramBuilder? s_bigramBuilder;
    private static DirectIndexedGramBuilder? s_trigramBuilder;

    public static BuiltIndex BuildContentIndex(IReadOnlyList<ISegmentBuildDocument> documents, int gramLength, bool caseInsensitive)
    {
        return (gramLength, caseInsensitive) switch
        {
            (2, false) => BuildContentIndexSensitiveBigram(documents),
            (2, true) => BuildContentIndexInsensitiveBigram(documents),
            (3, false) => BuildContentIndexSensitiveTrigram(documents),
            (3, true) => BuildContentIndexInsensitiveTrigram(documents),
            _ => throw new ArgumentOutOfRangeException(nameof(gramLength))
        };
    }

    private static BuiltIndex BuildContentIndexSensitiveBigram(IReadOnlyList<ISegmentBuildDocument> documents)
    {
        var builder = RentBuilder(2);
        try
        {
            CountContentIndexPairsSensitiveBigram(documents, builder);
            builder.PrepareForFill();
            FillContentIndexPairsSensitiveBigram(documents, builder);
            return builder.BuildIndex(2);
        }
        finally
        {
            ReturnBuilder(builder);
        }
    }

    private static BuiltIndex BuildContentIndexInsensitiveBigram(IReadOnlyList<ISegmentBuildDocument> documents)
    {
        var builder = RentBuilder(2);
        try
        {
            CountContentIndexPairsInsensitiveBigram(documents, builder);
            builder.PrepareForFill();
            FillContentIndexPairsInsensitiveBigram(documents, builder);
            return builder.BuildIndex(2);
        }
        finally
        {
            ReturnBuilder(builder);
        }
    }

    private static BuiltIndex BuildContentIndexSensitiveTrigram(IReadOnlyList<ISegmentBuildDocument> documents)
    {
        var builder = RentBuilder(3);
        try
        {
            CountContentIndexPairsSensitiveTrigram(documents, builder);
            builder.PrepareForFill();
            FillContentIndexPairsSensitiveTrigram(documents, builder);
            return builder.BuildIndex(3);
        }
        finally
        {
            ReturnBuilder(builder);
        }
    }

    private static BuiltIndex BuildContentIndexInsensitiveTrigram(IReadOnlyList<ISegmentBuildDocument> documents)
    {
        var builder = RentBuilder(3);
        try
        {
            CountContentIndexPairsInsensitiveTrigram(documents, builder);
            builder.PrepareForFill();
            FillContentIndexPairsInsensitiveTrigram(documents, builder);
            return builder.BuildIndex(3);
        }
        finally
        {
            ReturnBuilder(builder);
        }
    }

    public static BuiltIndex BuildPathTrigramIndex(IReadOnlyList<ISegmentBuildDocument> documents, bool caseInsensitive)
    {
        var builder = RentBuilder(3);
        try
        {
            CountPathIndexPairs(documents, caseInsensitive, builder);
            builder.PrepareForFill();
            FillPathIndexPairs(documents, caseInsensitive, builder);
            return builder.BuildIndex(3);
        }
        finally
        {
            ReturnBuilder(builder);
        }
    }

    private static void CountContentIndexPairsSensitiveBigram(IReadOnlyList<ISegmentBuildDocument> documents, DirectIndexedGramBuilder builder)
    {
        for (uint ordinal = 0; ordinal < documents.Count; ordinal++)
        {
            builder.StartDocument();
            var content = documents[(int)ordinal].GetContentBytes();
            if (content.Length < 2)
            {
                continue;
            }

            for (var i = 0; i < content.Length - 1; i++)
            {
                if (!TryEncodeBigramSensitive(content, i, out var gram))
                {
                    continue;
                }

                builder.Count(gram);
            }
        }
    }

    private static void FillContentIndexPairsSensitiveBigram(IReadOnlyList<ISegmentBuildDocument> documents, DirectIndexedGramBuilder builder)
    {
        for (uint ordinal = 0; ordinal < documents.Count; ordinal++)
        {
            builder.StartDocument();
            var content = documents[(int)ordinal].GetContentBytes();
            if (content.Length < 2)
            {
                continue;
            }

            for (var i = 0; i < content.Length - 1; i++)
            {
                if (!TryEncodeBigramSensitive(content, i, out var gram))
                {
                    continue;
                }

                builder.AddOrdinal(gram, ordinal);
            }
        }
    }

    private static void CountContentIndexPairsInsensitiveBigram(IReadOnlyList<ISegmentBuildDocument> documents, DirectIndexedGramBuilder builder)
    {
        for (uint ordinal = 0; ordinal < documents.Count; ordinal++)
        {
            builder.StartDocument();
            var content = documents[(int)ordinal].GetContentBytes();
            if (content.Length < 2)
            {
                continue;
            }

            for (var i = 0; i < content.Length - 1; i++)
            {
                if (!TryEncodeBigramInsensitiveAscii(content, i, out var gram))
                {
                    continue;
                }

                builder.Count(gram);
            }
        }
    }

    private static void FillContentIndexPairsInsensitiveBigram(IReadOnlyList<ISegmentBuildDocument> documents, DirectIndexedGramBuilder builder)
    {
        for (uint ordinal = 0; ordinal < documents.Count; ordinal++)
        {
            builder.StartDocument();
            var content = documents[(int)ordinal].GetContentBytes();
            if (content.Length < 2)
            {
                continue;
            }

            for (var i = 0; i < content.Length - 1; i++)
            {
                if (!TryEncodeBigramInsensitiveAscii(content, i, out var gram))
                {
                    continue;
                }

                builder.AddOrdinal(gram, ordinal);
            }
        }
    }

    private static void CountContentIndexPairsSensitiveTrigram(IReadOnlyList<ISegmentBuildDocument> documents, DirectIndexedGramBuilder builder)
    {
        for (uint ordinal = 0; ordinal < documents.Count; ordinal++)
        {
            builder.StartDocument();
            var content = documents[(int)ordinal].GetContentBytes();
            if (content.Length < 3)
            {
                continue;
            }

            for (var i = 0; i < content.Length - 2; i++)
            {
                if (!TryEncodeTrigramSensitive(content, i, out var gram))
                {
                    continue;
                }

                builder.Count(gram);
            }
        }
    }

    private static void FillContentIndexPairsSensitiveTrigram(IReadOnlyList<ISegmentBuildDocument> documents, DirectIndexedGramBuilder builder)
    {
        for (uint ordinal = 0; ordinal < documents.Count; ordinal++)
        {
            builder.StartDocument();
            var content = documents[(int)ordinal].GetContentBytes();
            if (content.Length < 3)
            {
                continue;
            }

            for (var i = 0; i < content.Length - 2; i++)
            {
                if (!TryEncodeTrigramSensitive(content, i, out var gram))
                {
                    continue;
                }

                builder.AddOrdinal(gram, ordinal);
            }
        }
    }

    private static void CountContentIndexPairsInsensitiveTrigram(IReadOnlyList<ISegmentBuildDocument> documents, DirectIndexedGramBuilder builder)
    {
        for (uint ordinal = 0; ordinal < documents.Count; ordinal++)
        {
            builder.StartDocument();
            var content = documents[(int)ordinal].GetContentBytes();
            if (content.Length < 3)
            {
                continue;
            }

            for (var i = 0; i < content.Length - 2; i++)
            {
                if (!TryEncodeTrigramInsensitiveAscii(content, i, out var gram))
                {
                    continue;
                }

                builder.Count(gram);
            }
        }
    }

    private static void FillContentIndexPairsInsensitiveTrigram(IReadOnlyList<ISegmentBuildDocument> documents, DirectIndexedGramBuilder builder)
    {
        for (uint ordinal = 0; ordinal < documents.Count; ordinal++)
        {
            builder.StartDocument();
            var content = documents[(int)ordinal].GetContentBytes();
            if (content.Length < 3)
            {
                continue;
            }

            for (var i = 0; i < content.Length - 2; i++)
            {
                if (!TryEncodeTrigramInsensitiveAscii(content, i, out var gram))
                {
                    continue;
                }

                builder.AddOrdinal(gram, ordinal);
            }
        }
    }

    private static void CountPathIndexPairs(IReadOnlyList<ISegmentBuildDocument> documents, bool caseInsensitive, DirectIndexedGramBuilder builder)
    {
        if (caseInsensitive)
        {
            CountPathIndexPairsInsensitive(documents, builder);
        }
        else
        {
            CountPathIndexPairsSensitive(documents, builder);
        }
    }

    private static void FillPathIndexPairs(IReadOnlyList<ISegmentBuildDocument> documents, bool caseInsensitive, DirectIndexedGramBuilder builder)
    {
        if (caseInsensitive)
        {
            FillPathIndexPairsInsensitive(documents, builder);
        }
        else
        {
            FillPathIndexPairsSensitive(documents, builder);
        }
    }

    private static void CountPathIndexPairsSensitive(IReadOnlyList<ISegmentBuildDocument> documents, DirectIndexedGramBuilder builder)
    {
        for (uint ordinal = 0; ordinal < documents.Count; ordinal++)
        {
            builder.StartDocument();
            var path = documents[(int)ordinal].GetPathUtf8();
            if (path.Length < 3)
            {
                continue;
            }

            for (var i = 0; i < path.Length - 2; i++)
            {
                if (!TryEncodeTrigramSensitive(path, i, out var gram))
                {
                    continue;
                }

                builder.Count(gram);
            }
        }
    }

    private static void FillPathIndexPairsSensitive(IReadOnlyList<ISegmentBuildDocument> documents, DirectIndexedGramBuilder builder)
    {
        for (uint ordinal = 0; ordinal < documents.Count; ordinal++)
        {
            builder.StartDocument();
            var path = documents[(int)ordinal].GetPathUtf8();
            if (path.Length < 3)
            {
                continue;
            }

            for (var i = 0; i < path.Length - 2; i++)
            {
                if (!TryEncodeTrigramSensitive(path, i, out var gram))
                {
                    continue;
                }

                builder.AddOrdinal(gram, ordinal);
            }
        }
    }

    private static void CountPathIndexPairsInsensitive(IReadOnlyList<ISegmentBuildDocument> documents, DirectIndexedGramBuilder builder)
    {
        for (uint ordinal = 0; ordinal < documents.Count; ordinal++)
        {
            builder.StartDocument();
            var path = documents[(int)ordinal].GetPathUtf8();
            if (path.Length < 3)
            {
                continue;
            }

            for (var i = 0; i < path.Length - 2; i++)
            {
                if (!TryEncodeTrigramInsensitiveAscii(path, i, out var gram))
                {
                    continue;
                }

                builder.Count(gram);
            }
        }
    }

    private static void FillPathIndexPairsInsensitive(IReadOnlyList<ISegmentBuildDocument> documents, DirectIndexedGramBuilder builder)
    {
        for (uint ordinal = 0; ordinal < documents.Count; ordinal++)
        {
            builder.StartDocument();
            var path = documents[(int)ordinal].GetPathUtf8();
            if (path.Length < 3)
            {
                continue;
            }

            for (var i = 0; i < path.Length - 2; i++)
            {
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

    internal static BuiltIndex BuildIndexFromPostingsBlob(List<IndexEntry> entries, byte[] postingsBlob, int[] bucketStarts, int gramLength)
    {
        if (entries.Count == 0)
        {
            var emptyLexicon = GC.AllocateUninitializedArray<byte>(SegmentFormat.LexiconHeaderSize);
            LittleEndianCodec.WriteInt32(emptyLexicon, 0);
            emptyLexicon[4] = 0;
            return new BuiltIndex(emptyLexicon, [0], []);
        }

        var suffixSize = gramLength == 2 ? 1 : 2;
        var entrySize = suffixSize + 12;
        var lexicon = GC.AllocateUninitializedArray<byte>(SegmentFormat.LexiconHeaderSize + entries.Count * entrySize);
        LittleEndianCodec.WriteInt32(lexicon, entries.Count);
        WriteBucketTable(lexicon, bucketStarts, entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var entryOffset = SegmentFormat.LexiconHeaderSize + i * entrySize;
            var entry = entries[i];
            WriteSuffix(lexicon.AsSpan(entryOffset, suffixSize), entry.Gram, suffixSize);
            LittleEndianCodec.WriteInt32(lexicon.AsSpan(entryOffset + suffixSize, 4), entry.PostingsOffset);
            LittleEndianCodec.WriteInt32(lexicon.AsSpan(entryOffset + suffixSize + 4, 4), entry.PostingsBytes);
            LittleEndianCodec.WriteInt32(lexicon.AsSpan(entryOffset + suffixSize + 8, 4), entry.DocFreq);
        }

        return new BuiltIndex(lexicon, postingsBlob, [.. entries]);
    }

    internal static void WriteBucketTable(byte[] lexicon, ReadOnlySpan<int> bucketStarts, int entryCount)
    {
        for (var bucket = 0; bucket < SegmentFormat.LexiconBucketCount; bucket++)
        {
            var value = bucket < bucketStarts.Length ? bucketStarts[bucket] : entryCount;
            LittleEndianCodec.WriteInt32(lexicon.AsSpan(4 + bucket * 4, 4), value);
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
    private Dictionary<int, int>? _touchedLookup;
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
        _touched = GC.AllocateUninitializedArray<int>(1024);
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

    public void PrepareForFill()
    {
        if (_touchedCount == 0)
        {
            _ordinals = [];
            return;
        }

        Array.Sort(_touched, 0, _touchedCount);
        var totalPairs = 0;
        EnsureLocalCapacity();
        _touchedLookup ??= new Dictionary<int, int>(_touchedCount);
        _touchedLookup.Clear();
        for (var i = 0; i < _touchedCount; i++)
        {
            var gram = _touched[i];
            var docFreq = GetCount(gram);
            _docFrequencies[i] = docFreq;
            _writePositions[i] = totalPairs;
            _touchedLookup.Add(gram, i);
            totalPairs += docFreq;
        }

        if (_ordinals.Length < totalPairs)
        {
            _ordinals = GC.AllocateUninitializedArray<uint>(totalPairs);
        }
    }

    public void AddOrdinal(int gram, uint ordinal)
    {
        if (_seen[gram] == _documentStamp)
        {
            return;
        }

        _seen[gram] = _documentStamp;
        _ordinals[_writePositions[_touchedLookup![gram]]++] = ordinal;
    }

    public BuiltIndex BuildIndex(int gramLength)
    {
        if (_touchedCount == 0)
        {
            var emptyLexicon = GC.AllocateUninitializedArray<byte>(SegmentFormat.LexiconHeaderSize);
            LittleEndianCodec.WriteInt32(emptyLexicon, 0);
            emptyLexicon[4] = 0;
            return new BuiltIndex(emptyLexicon, [0], []);
        }

        var entries = new List<IndexEntry>(_touchedCount);
        var bucketStarts = GC.AllocateUninitializedArray<int>(SegmentFormat.LexiconBucketCount);
        Array.Fill(bucketStarts, -1);

        var totalBytes = 0;
        var ordinalsOffset = 0;
        for (var i = 0; i < _touchedCount; i++)
        {
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
            var gram = _touched[i];
            _counts[gram] = 0;
        }

        _overflowCounts?.Clear();
        _touchedLookup?.Clear();
        _touchedCount = 0;

        var suffixSize = gramLength == 2 ? 1 : 2;
        var entrySize = suffixSize + 12;
        var lexicon = GC.AllocateUninitializedArray<byte>(SegmentFormat.LexiconHeaderSize + entries.Count * entrySize);
        LittleEndianCodec.WriteInt32(lexicon, entries.Count);
        SegmentIndexBuilder.WriteBucketTable(lexicon, bucketStarts, entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var entryOffset = SegmentFormat.LexiconHeaderSize + i * entrySize;
            var entry = entries[i];
            SegmentIndexBuilder.WriteSuffix(lexicon.AsSpan(entryOffset, suffixSize), entry.Gram, suffixSize);
            LittleEndianCodec.WriteInt32(lexicon.AsSpan(entryOffset + suffixSize, 4), entry.PostingsOffset);
            LittleEndianCodec.WriteInt32(lexicon.AsSpan(entryOffset + suffixSize + 4, 4), entry.PostingsBytes);
            LittleEndianCodec.WriteInt32(lexicon.AsSpan(entryOffset + suffixSize + 8, 4), entry.DocFreq);
        }

        var postingsBlob = GC.AllocateUninitializedArray<byte>(totalBytes);
        ordinalsOffset = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            PostingsCodec.Encode(
                _ordinals.AsSpan(ordinalsOffset, entry.DocFreq),
                postingsBlob.AsSpan(entry.PostingsOffset, entry.PostingsBytes));
            ordinalsOffset += entry.DocFreq;
        }

        return new BuiltIndex(lexicon, postingsBlob, [.. entries]);
    }

    private void AddTouched(int gram)
    {
        if (_touchedCount == _touched.Length)
        {
            Array.Resize(ref _touched, _touched.Length * 2);
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
        _touchedLookup = null;
        _ordinals = [];
        _docFrequencies = [];
        _writePositions = [];
        if (_touched.Length > 1024)
        {
            _touched = GC.AllocateUninitializedArray<int>(1024);
        }

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
        if (_docFrequencies.Length < _touchedCount)
        {
            Array.Resize(ref _docFrequencies, _touchedCount);
        }

        if (_writePositions.Length < _touchedCount)
        {
            Array.Resize(ref _writePositions, _touchedCount);
        }
    }
}

internal readonly record struct BuiltIndex(
    byte[] Lexicon,
    byte[] Postings,
    IndexEntry[] Entries);

internal readonly record struct IndexEntry(
    int Gram,
    int PostingsOffset,
    int PostingsBytes,
    int DocFreq);

