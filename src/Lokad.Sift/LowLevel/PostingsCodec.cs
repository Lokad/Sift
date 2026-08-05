using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;

namespace Lokad.Sift.LowLevel;

internal static class PostingsCodec
{
    public const int CheckpointInterval = 256;
    public const int CheckpointThreshold = 4096;

    private const byte PlainEncoding = 0;
    private const byte CheckpointedEncoding = 1;
    private const int TinyIntersectionThreshold = 256;
    private const int SimdBlockIntersectionThreshold = 2048;
    private const int ScalarBlockSize = 4;

    public static byte[] Encode(ReadOnlySpan<uint> ordinals)
    {
        var payload = GC.AllocateUninitializedArray<byte>(GetEncodedLength(ordinals));
        Encode(ordinals, payload);
        return payload;
    }

    internal static int GetEncodedLength(ReadOnlySpan<uint> ordinals)
    {
        var totalBytes = ordinals.Length >= CheckpointThreshold
            ? 1 + 4 + ((ordinals.Length + CheckpointInterval - 1) / CheckpointInterval) * 8
            : 1;
        uint previous = 0;
        for (var i = 0; i < ordinals.Length; i++)
        {
            totalBytes = checked(totalBytes + VarInt.GetUInt32Length(ordinals[i] - previous));
            previous = ordinals[i];
        }

        return totalBytes;
    }

    internal static void Encode(ReadOnlySpan<uint> ordinals, Span<byte> payload)
    {
        if (payload.Length != GetEncodedLength(ordinals))
        {
            throw new ArgumentException("The postings destination has an invalid length.", nameof(payload));
        }

        if (ordinals.Length >= CheckpointThreshold)
        {
            EncodeCheckpointed(ordinals, payload);
        }
        else
        {
            EncodePlain(ordinals, payload);
        }
    }

    public static PostingsView Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
        {
            return new PostingsView(IsCheckpointed: false, [], []);
        }

        return payload[0] switch
        {
            PlainEncoding => new PostingsView(IsCheckpointed: false, payload[1..], []),
            CheckpointedEncoding => ParseCheckpointed(payload),
            _ => throw new InvalidDataException($"Unsupported postings encoding '{payload[0]}'.")
        };
    }

    public static uint[] Decode(ReadOnlySpan<byte> payload)
    {
        var view = Parse(payload);
        var values = new List<uint>(16);
        uint current = 0;
        var offset = 0;
        while (offset < view.Data.Length)
        {
            current += VarInt.ReadUInt32(view.Data, ref offset);
            values.Add(current);
        }

        return values.ToArray();
    }

    internal static void DecodeIntoBitSet(ReadOnlySpan<byte> payload, PooledBitSet bitSet)
    {
        var view = Parse(payload);
        uint current = 0;
        var offset = 0;
        while (offset < view.Data.Length)
        {
            current += VarInt.ReadUInt32(view.Data, ref offset);
            if (current >= (uint)bitSet.BitCount)
            {
                throw new InvalidDataException("Sift postings contain an out-of-range document ordinal.");
            }

            bitSet.Set((int)current);
        }
    }

    public static uint[] IntersectSorted(ReadOnlySpan<uint> left, ReadOnlySpan<byte> payload, int docFreq)
    {
        if (left.Length == 0 || payload.Length == 0 || docFreq == 0)
        {
            return [];
        }

        var result = GC.AllocateUninitializedArray<uint>(Math.Min(left.Length, docFreq));
        var count = IntersectSorted(left, payload, docFreq, result);
        Array.Resize(ref result, count);
        return result;
    }

    internal static int IntersectSorted(
        ReadOnlySpan<uint> left,
        ReadOnlySpan<byte> payload,
        int docFreq,
        Span<uint> destination)
    {
        if (left.Length == 0 || payload.Length == 0 || docFreq == 0)
        {
            return 0;
        }

        if (destination.Length < Math.Min(left.Length, docFreq))
        {
            throw new ArgumentException("The intersection destination is too short.", nameof(destination));
        }

        var view = Parse(payload);
        var smaller = Math.Min(left.Length, docFreq);
        if (smaller >= SimdBlockIntersectionThreshold)
        {
            return IntersectSortedAdaptive(left, view, docFreq, destination, useSimd: true);
        }

        return view.IsCheckpointed
            ? IntersectSortedWithCheckpointed(left, view, destination)
            : IntersectSortedSequential(left, view.Data, destination);
    }

    private static void EncodePlain(ReadOnlySpan<uint> ordinals, Span<byte> payload)
    {
        payload[0] = PlainEncoding;
        var cursor = 1;
        uint previous = 0;
        for (var i = 0; i < ordinals.Length; i++)
        {
            cursor += VarInt.WriteUInt32(payload[cursor..], ordinals[i] - previous);
            previous = ordinals[i];
        }
    }

    private static void EncodeCheckpointed(ReadOnlySpan<uint> ordinals, Span<byte> payload)
    {
        var checkpointCount = (ordinals.Length + CheckpointInterval - 1) / CheckpointInterval;
        var checkpointHeaderBytes = 1 + 4 + checkpointCount * 8;
        payload[0] = CheckpointedEncoding;
        BinaryPrimitives.WriteInt32LittleEndian(payload.Slice(1, 4), checkpointCount);

        var checkpointCursor = 5;
        var dataCursor = checkpointHeaderBytes;
        uint previous = 0;
        for (var i = 0; i < ordinals.Length; i++)
        {
            var ordinal = ordinals[i];
            dataCursor += VarInt.WriteUInt32(payload[dataCursor..], ordinal - previous);
            if (i % CheckpointInterval == 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(payload.Slice(checkpointCursor, 4), ordinal);
                BinaryPrimitives.WriteInt32LittleEndian(payload.Slice(checkpointCursor + 4, 4), dataCursor - checkpointHeaderBytes);
                checkpointCursor += 8;
            }

            previous = ordinal;
        }
    }

    private static PostingsView ParseCheckpointed(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 5)
        {
            throw new InvalidDataException("Invalid checkpointed postings header.");
        }

        var checkpointCount = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(1, 4));
        if (checkpointCount <= 0)
        {
            throw new InvalidDataException("Invalid checkpoint count.");
        }

        if (checkpointCount > (payload.Length - 5) / 8)
        {
            throw new InvalidDataException("Checkpoint table exceeds postings payload.");
        }

        var checkpointBytes = checkpointCount * 8;
        var dataOffset = 5 + checkpointBytes;
        var dataLength = payload.Length - dataOffset;
        var checkpoints = payload.Slice(5, checkpointBytes);
        var previousDataOffset = -1;
        uint previousOrdinal = 0;
        for (var i = 0; i < checkpointCount; i++)
        {
            var checkpoint = checkpoints.Slice(i * 8, 8);
            var ordinal = BinaryPrimitives.ReadUInt32LittleEndian(checkpoint);
            var checkpointDataOffset = BinaryPrimitives.ReadInt32LittleEndian(checkpoint[4..]);
            if (checkpointDataOffset < 0 ||
                checkpointDataOffset > dataLength ||
                checkpointDataOffset <= previousDataOffset ||
                (i > 0 && ordinal <= previousOrdinal))
            {
                throw new InvalidDataException("Invalid postings checkpoint.");
            }

            previousDataOffset = checkpointDataOffset;
            previousOrdinal = ordinal;
        }

        return new PostingsView(IsCheckpointed: true, payload[dataOffset..], checkpoints);
    }

    private static int IntersectSortedSequential(ReadOnlySpan<uint> left, ReadOnlySpan<byte> data, Span<uint> result)
    {
        var count = 0;
        var leftIndex = 0;
        uint current = 0;
        var offset = 0;

        while (leftIndex < left.Length && offset < data.Length)
        {
            current += VarInt.ReadUInt32(data, ref offset);
            while (leftIndex < left.Length && left[leftIndex] < current)
            {
                leftIndex++;
            }

            if (leftIndex >= left.Length)
            {
                break;
            }

            if (left[leftIndex] == current)
            {
                result[count++] = current;
                leftIndex++;
            }
        }

        return count;
    }

    private static int IntersectSortedAdaptive(ReadOnlySpan<uint> left, PostingsView view, int docFreq, Span<uint> result, bool useSimd)
    {
        if (TryGetApproximateRange(view, out var rightFirst, out var rightLast) &&
            (left[0] > rightLast || left[^1] < rightFirst))
        {
            return 0;
        }

        var rentedRight = ArrayPool<uint>.Shared.Rent(docFreq);
        try
        {
            var rightLength = DecodeOrdinals(view, rentedRight);
            var right = rentedRight.AsSpan(0, rightLength);
            if (right.Length == 0)
            {
                return 0;
            }

            if (left[0] > right[^1] || right[0] > left[^1])
            {
                return 0;
            }

            var smaller = Math.Min(left.Length, right.Length);
            if (smaller <= TinyIntersectionThreshold)
            {
                return IntersectSortedScalar(left, right, result);
            }

            return useSimd
                ? IntersectSortedBlockMergeSimd(left, right, result)
                : IntersectSortedBlockMerge(left, right, result);
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(rentedRight);
        }
    }

    private static int IntersectSortedWithCheckpointed(ReadOnlySpan<uint> left, PostingsView view, Span<uint> result)
    {
        var count = 0;
        var leftIndex = 0;
        var dataOffset = 0;
        uint current = 0;
        var checkpointIndex = -1;
        var initialized = false;

        while (leftIndex < left.Length)
        {
            var target = left[leftIndex];
            if (!initialized || current < target)
            {
                var candidateCheckpoint = FindCheckpoint(view.Checkpoints, target);
                if (candidateCheckpoint > checkpointIndex)
                {
                    checkpointIndex = candidateCheckpoint;
                    current = BinaryPrimitives.ReadUInt32LittleEndian(view.Checkpoints.Slice(checkpointIndex * 8, 4));
                    dataOffset = BinaryPrimitives.ReadInt32LittleEndian(view.Checkpoints.Slice(checkpointIndex * 8 + 4, 4));
                    initialized = true;
                }

                while (initialized && current < target && dataOffset < view.Data.Length)
                {
                    current += VarInt.ReadUInt32(view.Data, ref dataOffset);
                }
            }

            if (!initialized || current < target)
            {
                break;
            }

            if (current == target)
            {
                result[count++] = current;
            }

            leftIndex++;
        }

        return count;
    }

    private static int DecodeOrdinals(PostingsView view, Span<uint> values)
    {
        var count = 0;
        uint current = 0;
        var offset = 0;
        while (offset < view.Data.Length && count < values.Length)
        {
            current += VarInt.ReadUInt32(view.Data, ref offset);
            values[count++] = current;
        }

        return count;
    }

    private static bool TryGetApproximateRange(PostingsView view, out uint first, out uint last)
    {
        if (!view.IsCheckpointed || view.Checkpoints.Length < 8)
        {
            first = 0;
            last = 0;
            return false;
        }

        first = BinaryPrimitives.ReadUInt32LittleEndian(view.Checkpoints);
        var lastCheckpointOffset = view.Checkpoints.Length - 8;
        last = BinaryPrimitives.ReadUInt32LittleEndian(view.Checkpoints.Slice(lastCheckpointOffset, 4));
        var dataOffset = BinaryPrimitives.ReadInt32LittleEndian(view.Checkpoints.Slice(lastCheckpointOffset + 4, 4));
        while (dataOffset < view.Data.Length)
        {
            last += VarInt.ReadUInt32(view.Data, ref dataOffset);
        }

        return true;
    }

    private static int IntersectSortedScalar(ReadOnlySpan<uint> left, ReadOnlySpan<uint> right, Span<uint> result)
    {
        var count = 0;
        var i = 0;
        var j = 0;
        while (i < left.Length && j < right.Length)
        {
            var leftValue = left[i];
            var rightValue = right[j];
            if (leftValue == rightValue)
            {
                result[count++] = leftValue;
                i++;
                j++;
            }
            else if (leftValue < rightValue)
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return count;
    }

    private static int IntersectSortedBlockMerge(ReadOnlySpan<uint> left, ReadOnlySpan<uint> right, Span<uint> result)
    {
        var count = 0;
        var ai = 0;
        var bi = 0;
        while (ai < left.Length && bi < right.Length)
        {
            var aCount = Math.Min(ScalarBlockSize, left.Length - ai);
            var bCount = Math.Min(ScalarBlockSize, right.Length - bi);

            for (var i = 0; i < aCount; i++)
            {
                var value = left[ai + i];
                for (var j = 0; j < bCount; j++)
                {
                    if (value == right[bi + j])
                    {
                        result[count++] = value;
                        break;
                    }
                }
            }

            var aLast = left[ai + aCount - 1];
            var bLast = right[bi + bCount - 1];
            if (aLast <= bLast)
            {
                ai += aCount;
            }

            if (bLast <= aLast)
            {
                bi += bCount;
            }
        }

        return count;
    }

    private static int IntersectSortedBlockMergeSimd(ReadOnlySpan<uint> left, ReadOnlySpan<uint> right, Span<uint> result)
    {
        if (!Vector.IsHardwareAccelerated || Vector<uint>.Count < 4)
        {
            return IntersectSortedBlockMerge(left, right, result);
        }

        var blockSize = Vector<uint>.Count;
        var count = 0;
        var ai = 0;
        var bi = 0;
        var zero = Vector<uint>.Zero;
        while (ai < left.Length && bi < right.Length)
        {
            var aCount = Math.Min(blockSize, left.Length - ai);
            var bCount = Math.Min(blockSize, right.Length - bi);

            if (aCount == blockSize && bCount == blockSize)
            {
                var blockB = new Vector<uint>(right.Slice(bi, blockSize));
                for (var i = 0; i < blockSize; i++)
                {
                    var value = left[ai + i];
                    var eq = Vector.Equals(new Vector<uint>(value), blockB);
                    if (!Vector.EqualsAll(eq, zero))
                    {
                        result[count++] = value;
                    }
                }
            }
            else
            {
                for (var i = 0; i < aCount; i++)
                {
                    var value = left[ai + i];
                    for (var j = 0; j < bCount; j++)
                    {
                        if (value == right[bi + j])
                        {
                            result[count++] = value;
                            break;
                        }
                    }
                }
            }

            var aLast = left[ai + aCount - 1];
            var bLast = right[bi + bCount - 1];
            if (aLast <= bLast)
            {
                ai += aCount;
            }

            if (bLast <= aLast)
            {
                bi += bCount;
            }
        }

        return count;
    }

    private static int FindCheckpoint(ReadOnlySpan<byte> checkpoints, uint target)
    {
        var low = 0;
        var high = checkpoints.Length / 8 - 1;
        var best = 0;
        while (low <= high)
        {
            var mid = low + ((high - low) >> 1);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(checkpoints.Slice(mid * 8, 4));
            if (value <= target)
            {
                best = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return best;
    }
}

internal readonly ref struct PostingsView
{
    public PostingsView(bool IsCheckpointed, ReadOnlySpan<byte> Data, ReadOnlySpan<byte> Checkpoints)
    {
        this.IsCheckpointed = IsCheckpointed;
        this.Data = Data;
        this.Checkpoints = Checkpoints;
    }

    public bool IsCheckpointed { get; }
    public ReadOnlySpan<byte> Data { get; }
    public ReadOnlySpan<byte> Checkpoints { get; }
}
