using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Running;
using Lokad.Sift;
using Lokad.Sift.LowLevel;
using Lokad.Sift.Storage;


internal static class PostingsBenchmarkData
{
    public static uint[] BuildArithmeticOrdinals(int count, int stride)
    {
        var values = new uint[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = (uint)(i * stride);
        }

        return values;
    }

    public static uint[] BuildOrdinalsForEncodedSizeTarget(int targetBytes)
    {
        var count = 512;
        while (true)
        {
            var ordinals = BuildArithmeticOrdinals(count, 3);
            var payload = PostingsCodec.Encode(ordinals);
            if (payload.Length >= targetBytes)
            {
                return ordinals;
            }

            count += 128;
        }
    }

    public static uint[] BuildSubsetOrdinals(ReadOnlySpan<uint> source, int step)
    {
        if (source.IsEmpty)
        {
            return [];
        }

        var count = (source.Length + step - 1) / step;
        var values = new uint[count];
        var index = 0;
        for (var i = 0; i < source.Length; i += step)
        {
            values[index++] = source[i];
        }

        return values;
    }

    public static uint[] BuildDisjointOrdinals(ReadOnlySpan<uint> source, int count)
    {
        if (source.IsEmpty || count <= 0)
        {
            return [];
        }

        var stride = source.Length > 1 ? source[1] - source[0] : 1u;
        var offset = source[0] + 1u;
        var values = new uint[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = offset + ((uint)i * stride);
        }

        return values;
    }

    public static uint[] BuildTailMissOrdinals(ReadOnlySpan<uint> source, int count)
    {
        if (source.IsEmpty || count <= 0)
        {
            return [];
        }

        var stride = source.Length > 1 ? source[1] - source[0] : 1u;
        var start = source[^1] + stride;
        var values = new uint[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = start + ((uint)i * stride);
        }

        return values;
    }
}

internal static class BlockMergeIntersection
{
    private const int ScalarBlockSize = 4;

    public static uint[] IntersectSorted(ReadOnlySpan<uint> left, ReadOnlySpan<uint> right)
    {
        if (left.IsEmpty || right.IsEmpty)
        {
            return [];
        }

        var result = new uint[Math.Min(left.Length, right.Length)];
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

        Array.Resize(ref result, count);
        return result;
    }

    public static uint[] IntersectSortedSimd(ReadOnlySpan<uint> left, ReadOnlySpan<uint> right)
    {
        if (left.IsEmpty || right.IsEmpty)
        {
            return [];
        }

        if (!Vector.IsHardwareAccelerated || Vector<uint>.Count < 4)
        {
            return IntersectSorted(left, right);
        }

        var blockSize = Vector<uint>.Count;
        var result = new uint[Math.Min(left.Length, right.Length)];
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

        Array.Resize(ref result, count);
        return result;
    }
}

internal static class AdaptiveIntersection
{
    private const int TinyListThreshold = 256;
    private const int SimdBlockThreshold = 1024;

    public static uint[] IntersectSorted(ReadOnlySpan<uint> left, ReadOnlySpan<uint> right, ReadOnlySpan<byte> checkpointedPayload, int rightCount)
    {
        return IntersectCore(left, right, checkpointedPayload, rightCount, useSimd: false);
    }

    public static uint[] IntersectSortedSimd(ReadOnlySpan<uint> left, ReadOnlySpan<uint> right, ReadOnlySpan<byte> checkpointedPayload, int rightCount)
    {
        return IntersectCore(left, right, checkpointedPayload, rightCount, useSimd: true);
    }

    private static uint[] IntersectCore(ReadOnlySpan<uint> left, ReadOnlySpan<uint> right, ReadOnlySpan<byte> checkpointedPayload, int rightCount, bool useSimd)
    {
        if (left.IsEmpty || right.IsEmpty)
        {
            return [];
        }

        if (left[0] > right[^1] || right[0] > left[^1])
        {
            return [];
        }

        var leftLength = left.Length;
        var smaller = Math.Min(leftLength, rightCount);
        if (smaller <= TinyListThreshold)
        {
            return ScalarIntersection.IntersectSorted(left, right);
        }

        if (smaller < SimdBlockThreshold)
        {
            return BlockMergeIntersection.IntersectSorted(left, right);
        }

        return useSimd
            ? BlockMergeIntersection.IntersectSortedSimd(left, right)
            : BlockMergeIntersection.IntersectSorted(left, right);
    }
}

internal static class CheckpointedBlockIntersection
{
    private const int DecodedBlockSize = 128;

    public static uint[] IntersectSorted(ReadOnlySpan<uint> left, ReadOnlySpan<byte> checkpointedPayload)
    {
        return IntersectCore(left, checkpointedPayload, useSimd: false);
    }

    public static uint[] IntersectSortedSimd(ReadOnlySpan<uint> left, ReadOnlySpan<byte> checkpointedPayload)
    {
        return IntersectCore(left, checkpointedPayload, useSimd: true);
    }

    public static uint[] IntersectSortedAdjacent2ShuffleSimd(ReadOnlySpan<uint> left, ReadOnlySpan<byte> checkpointedPayload)
    {
        return IntersectCoreAdjacent(left, checkpointedPayload, AdjacentDecodeMode.Adjacent2Shuffle);
    }

    public static uint[] IntersectSortedAdjacent4ShuffleSimd(ReadOnlySpan<uint> left, ReadOnlySpan<byte> checkpointedPayload)
    {
        return IntersectCoreAdjacent(left, checkpointedPayload, AdjacentDecodeMode.Adjacent4Shuffle);
    }

    private static uint[] IntersectCore(ReadOnlySpan<uint> left, ReadOnlySpan<byte> checkpointedPayload, bool useSimd)
    {
        if (left.IsEmpty || checkpointedPayload.IsEmpty)
        {
            return [];
        }

        var view = PostingsCodec.Parse(checkpointedPayload);
        if (view.Data.IsEmpty)
        {
            return [];
        }

        if (TryGetApproximateRange(view, out var first, out var last) &&
            (left[0] > last || left[^1] < first))
        {
            return [];
        }

        var result = new uint[left.Length];
        var resultCount = 0;
        var rightBuffer = ArrayPool<uint>.Shared.Rent(DecodedBlockSize);
        try
        {
            var leftIndex = AdvanceLeft(left, 0, first);
            uint current = 0;
            var offset = 0;
            while (leftIndex < left.Length && offset < view.Data.Length)
            {
                var decodedCount = DecodeBlock(view.Data, ref offset, ref current, rightBuffer);
                if (decodedCount == 0)
                {
                    break;
                }

                var rightFirst = rightBuffer[0];
                var rightLast = rightBuffer[decodedCount - 1];
                leftIndex = AdvanceLeft(left, leftIndex, rightFirst);
                if (leftIndex >= left.Length)
                {
                    break;
                }

                if (left[leftIndex] > rightLast)
                {
                    continue;
                }

                var leftEnd = leftIndex;
                while (leftEnd < left.Length && left[leftEnd] <= rightLast)
                {
                    leftEnd++;
                }

                if (leftEnd > leftIndex)
                {
                    resultCount = useSimd
                        ? AppendIntersectSortedBlockMergeSimd(left.Slice(leftIndex, leftEnd - leftIndex), rightBuffer.AsSpan(0, decodedCount), result, resultCount)
                        : AppendIntersectSortedBlockMerge(left.Slice(leftIndex, leftEnd - leftIndex), rightBuffer.AsSpan(0, decodedCount), result, resultCount);
                    leftIndex = leftEnd;
                }
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(rightBuffer);
        }

        Array.Resize(ref result, resultCount);
        return result;
    }

    private static uint[] IntersectCoreAdjacent(ReadOnlySpan<uint> left, ReadOnlySpan<byte> checkpointedPayload, AdjacentDecodeMode mode)
    {
        if (left.IsEmpty || checkpointedPayload.IsEmpty)
        {
            return [];
        }

        var view = PostingsCodec.Parse(checkpointedPayload);
        if (view.Data.IsEmpty)
        {
            return [];
        }

        if (TryGetApproximateRange(view, out var first, out var last) &&
            (left[0] > last || left[^1] < first))
        {
            return [];
        }

        var result = new uint[left.Length];
        var resultCount = 0;
        var rightBuffer = ArrayPool<uint>.Shared.Rent(DecodedBlockSize);
        try
        {
            var leftIndex = AdvanceLeft(left, 0, first);
            uint current = 0;
            var offset = 0;
            while (leftIndex < left.Length && offset < view.Data.Length)
            {
                var decodedCount = AdjacentVarIntDecode.DecodeBlockDeltaU32(view.Data, ref offset, ref current, rightBuffer, DecodedBlockSize, mode);
                if (decodedCount == 0)
                {
                    break;
                }

                var rightFirst = rightBuffer[0];
                var rightLast = rightBuffer[decodedCount - 1];
                leftIndex = AdvanceLeft(left, leftIndex, rightFirst);
                if (leftIndex >= left.Length)
                {
                    break;
                }

                if (left[leftIndex] > rightLast)
                {
                    continue;
                }

                var leftEnd = leftIndex;
                while (leftEnd < left.Length && left[leftEnd] <= rightLast)
                {
                    leftEnd++;
                }

                if (leftEnd > leftIndex)
                {
                    resultCount = AppendIntersectSortedBlockMergeSimd(left.Slice(leftIndex, leftEnd - leftIndex), rightBuffer.AsSpan(0, decodedCount), result, resultCount);
                    leftIndex = leftEnd;
                }
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(rightBuffer);
        }

        Array.Resize(ref result, resultCount);
        return result;
    }

    private static int DecodeBlock(ReadOnlySpan<byte> data, ref int offset, ref uint current, uint[] buffer)
    {
        var count = 0;
        while (offset < data.Length && count < DecodedBlockSize)
        {
            current += VarInt.ReadUInt32(data, ref offset);
            buffer[count++] = current;
        }

        return count;
    }

    private static int AdvanceLeft(ReadOnlySpan<uint> left, int start, uint target)
    {
        while (start < left.Length && left[start] < target)
        {
            start++;
        }

        return start;
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

    private static int AppendIntersectSortedBlockMerge(ReadOnlySpan<uint> left, ReadOnlySpan<uint> right, uint[] destination, int destinationCount)
    {
        var ai = 0;
        var bi = 0;
        while (ai < left.Length && bi < right.Length)
        {
            var aCount = Math.Min(4, left.Length - ai);
            var bCount = Math.Min(4, right.Length - bi);

            for (var i = 0; i < aCount; i++)
            {
                var value = left[ai + i];
                for (var j = 0; j < bCount; j++)
                {
                    if (value == right[bi + j])
                    {
                        destination[destinationCount++] = value;
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

        return destinationCount;
    }

    private static int AppendIntersectSortedBlockMergeSimd(ReadOnlySpan<uint> left, ReadOnlySpan<uint> right, uint[] destination, int destinationCount)
    {
        if (!Vector.IsHardwareAccelerated || Vector<uint>.Count < 4)
        {
            return AppendIntersectSortedBlockMerge(left, right, destination, destinationCount);
        }

        var blockSize = Vector<uint>.Count;
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
                        destination[destinationCount++] = value;
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
                            destination[destinationCount++] = value;
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

        return destinationCount;
    }
}

internal enum AdjacentDecodeMode
{
    Adjacent2Shuffle,
    Adjacent4Shuffle
}

internal static class AdjacentVarIntDecode
{
    private static readonly Vector128<byte>[] s_twoShuffleMasks = BuildTwoShuffleMasks();
    private static readonly Vector128<byte>[] s_fourShuffleMasks = BuildFourShuffleMasks();

    public static uint[] DecodeCheckpointedU32Adjacent2(ReadOnlySpan<byte> payload)
    {
        return DecodeCheckpointedU32(payload, AdjacentDecodeMode.Adjacent2Shuffle);
    }

    public static uint[] DecodeCheckpointedU32Adjacent4(ReadOnlySpan<byte> payload)
    {
        return DecodeCheckpointedU32(payload, AdjacentDecodeMode.Adjacent4Shuffle);
    }

    private static uint[] DecodeCheckpointedU32(ReadOnlySpan<byte> payload, AdjacentDecodeMode mode)
    {
        if (payload.IsEmpty)
        {
            return [];
        }

        var view = PostingsCodec.Parse(payload);
        if (view.Data.IsEmpty)
        {
            return [];
        }

        var values = new uint[EstimateCount(view)];
        uint current = 0;
        var offset = 0;
        var count = 0;
        while (offset < view.Data.Length)
        {
            if (count == values.Length)
            {
                Array.Resize(ref values, Math.Max(4, values.Length * 2));
            }

            var remaining = values.Length - count;
            count += DecodeBlockDeltaU32(view.Data, ref offset, ref current, values.AsSpan(count), remaining, mode);
        }

        Array.Resize(ref values, count);
        return values;
    }

    public static int DecodeBlockDeltaU32(ReadOnlySpan<byte> data, ref int offset, ref uint current, Span<uint> destination, int maxCount, AdjacentDecodeMode mode)
    {
        var count = 0;
        if (Ssse3.IsSupported && Sse2.IsSupported)
        {
            while (count < maxCount && offset + 16 <= data.Length)
            {
                var written = 0;
                var consumed4 = 0;
                if (mode == AdjacentDecodeMode.Adjacent4Shuffle)
                {
                    written = TryDecodeFour(data.Slice(offset), ref current, destination.Slice(count, maxCount - count), out consumed4);
                }

                if (written > 0)
                {
                    count += written;
                    offset += consumed4;
                    continue;
                }

                written = TryDecodeTwo(data.Slice(offset), ref current, destination.Slice(count, maxCount - count), out var consumed2);
                if (written > 0)
                {
                    count += written;
                    offset += consumed2;
                    continue;
                }

                break;
            }
        }

        while (count < maxCount && offset < data.Length)
        {
            current += VarInt.ReadUInt32(data, ref offset);
            destination[count++] = current;
        }

        return count;
    }

    private static int TryDecodeTwo(ReadOnlySpan<byte> data, ref uint current, Span<uint> destination, out int consumed)
    {
        consumed = 0;
        if (destination.Length < 2 || data.Length < 16)
        {
            return 0;
        }

        var vector = MemoryMarshal.Read<Vector128<byte>>(data);
        var continuationMask = Sse2.MoveMask(vector);
        var stopMask = (~continuationMask) & 0x3FF;
        if (stopMask == 0)
        {
            return 0;
        }

        var firstLen = BitOperations.TrailingZeroCount((uint)stopMask) + 1;
        if ((uint)(firstLen - 1) >= 5u)
        {
            return 0;
        }

        var secondStopMask = stopMask >> firstLen;
        if (secondStopMask == 0)
        {
            return 0;
        }

        var secondLen = BitOperations.TrailingZeroCount((uint)secondStopMask) + 1;
        if ((uint)(secondLen - 1) >= 5u)
        {
            return 0;
        }

        consumed = firstLen + secondLen;
        var mask = s_twoShuffleMasks[(firstLen - 1) * 5 + (secondLen - 1)];
        var shuffled = Ssse3.Shuffle(vector, mask);
        Span<byte> packed = stackalloc byte[16];
        MemoryMarshal.Write(packed, in shuffled);

        current += DecodeU32ByLength(packed, firstLen);
        destination[0] = current;
        current += DecodeU32ByLength(packed.Slice(8), secondLen);
        destination[1] = current;
        return 2;
    }

    private static int TryDecodeFour(ReadOnlySpan<byte> data, ref uint current, Span<uint> destination, out int consumed)
    {
        consumed = 0;
        if (destination.Length < 4 || data.Length < 16)
        {
            return 0;
        }

        var vector = MemoryMarshal.Read<Vector128<byte>>(data);
        var continuationMask = Sse2.MoveMask(vector);
        var stopMask = (~continuationMask) & 0xFFFF;
        if (stopMask == 0)
        {
            return 0;
        }

        var firstLen = BitOperations.TrailingZeroCount((uint)stopMask) + 1;
        if ((uint)(firstLen - 1) >= 4u)
        {
            return 0;
        }

        stopMask >>= firstLen;
        if (stopMask == 0)
        {
            return 0;
        }

        var secondLen = BitOperations.TrailingZeroCount((uint)stopMask) + 1;
        if ((uint)(secondLen - 1) >= 4u)
        {
            return 0;
        }

        stopMask >>= secondLen;
        if (stopMask == 0)
        {
            return 0;
        }

        var thirdLen = BitOperations.TrailingZeroCount((uint)stopMask) + 1;
        if ((uint)(thirdLen - 1) >= 4u)
        {
            return 0;
        }

        stopMask >>= thirdLen;
        if (stopMask == 0)
        {
            return 0;
        }

        var fourthLen = BitOperations.TrailingZeroCount((uint)stopMask) + 1;
        if ((uint)(fourthLen - 1) >= 4u)
        {
            return 0;
        }

        consumed = firstLen + secondLen + thirdLen + fourthLen;
        if (consumed > 16)
        {
            return 0;
        }

        var maskIndex = (((firstLen - 1) * 4 + (secondLen - 1)) * 4 + (thirdLen - 1)) * 4 + (fourthLen - 1);
        var mask = s_fourShuffleMasks[maskIndex];
        var shuffled = Ssse3.Shuffle(vector, mask);
        Span<byte> packed = stackalloc byte[16];
        MemoryMarshal.Write(packed, in shuffled);

        current += DecodeU32ByLength(packed, firstLen);
        destination[0] = current;
        current += DecodeU32ByLength(packed.Slice(4), secondLen);
        destination[1] = current;
        current += DecodeU32ByLength(packed.Slice(8), thirdLen);
        destination[2] = current;
        current += DecodeU32ByLength(packed.Slice(12), fourthLen);
        destination[3] = current;
        return 4;
    }

    private static Vector128<byte>[] BuildTwoShuffleMasks()
    {
        var masks = new Vector128<byte>[25];
        Span<byte> bytes = stackalloc byte[16];
        for (var firstLen = 1; firstLen <= 5; firstLen++)
        {
            for (var secondLen = 1; secondLen <= 5; secondLen++)
            {
                bytes.Fill(0x80);
                for (var i = 0; i < firstLen; i++)
                {
                    bytes[i] = (byte)i;
                }

                for (var i = 0; i < secondLen; i++)
                {
                    bytes[8 + i] = (byte)(firstLen + i);
                }

                masks[(firstLen - 1) * 5 + (secondLen - 1)] = MemoryMarshal.Read<Vector128<byte>>(bytes);
            }
        }

        return masks;
    }

    private static Vector128<byte>[] BuildFourShuffleMasks()
    {
        var masks = new Vector128<byte>[256];
        Span<byte> bytes = stackalloc byte[16];
        for (var firstLen = 1; firstLen <= 4; firstLen++)
        {
            for (var secondLen = 1; secondLen <= 4; secondLen++)
            {
                for (var thirdLen = 1; thirdLen <= 4; thirdLen++)
                {
                    for (var fourthLen = 1; fourthLen <= 4; fourthLen++)
                    {
                        bytes.Fill(0x80);
                        var source = 0;
                        for (var i = 0; i < firstLen; i++)
                        {
                            bytes[i] = (byte)(source + i);
                        }

                        source += firstLen;
                        for (var i = 0; i < secondLen; i++)
                        {
                            bytes[4 + i] = (byte)(source + i);
                        }

                        source += secondLen;
                        for (var i = 0; i < thirdLen; i++)
                        {
                            bytes[8 + i] = (byte)(source + i);
                        }

                        source += thirdLen;
                        for (var i = 0; i < fourthLen; i++)
                        {
                            bytes[12 + i] = (byte)(source + i);
                        }

                        var index = (((firstLen - 1) * 4 + (secondLen - 1)) * 4 + (thirdLen - 1)) * 4 + (fourthLen - 1);
                        masks[index] = MemoryMarshal.Read<Vector128<byte>>(bytes);
                    }
                }
            }
        }

        return masks;
    }

    private static uint DecodeU32ByLength(ReadOnlySpan<byte> data, int length)
    {
        return length switch
        {
            1 => (uint)(data[0] & 0x7F),
            2 => (uint)(data[0] & 0x7F) | ((uint)(data[1] & 0x7F) << 7),
            3 => (uint)(data[0] & 0x7F) | ((uint)(data[1] & 0x7F) << 7) | ((uint)(data[2] & 0x7F) << 14),
            4 => (uint)(data[0] & 0x7F) | ((uint)(data[1] & 0x7F) << 7) | ((uint)(data[2] & 0x7F) << 14) | ((uint)(data[3] & 0x7F) << 21),
            5 => (uint)(data[0] & 0x7F) | ((uint)(data[1] & 0x7F) << 7) | ((uint)(data[2] & 0x7F) << 14) | ((uint)(data[3] & 0x7F) << 21) | ((uint)(data[4] & 0x0F) << 28),
            _ => throw new ArgumentOutOfRangeException(nameof(length))
        };
    }

    private static int EstimateCount(PostingsView view)
    {
        if (view.IsCheckpointed && view.Checkpoints.Length >= 8)
        {
            var checkpointCount = view.Checkpoints.Length / 8;
            return Math.Max(4, checkpointCount * PostingsCodec.CheckpointInterval);
        }

        return Math.Max(4, view.Data.Length);
    }
}

internal static class ScalarIntersection
{
    public static uint[] IntersectSorted(ReadOnlySpan<uint> left, ReadOnlySpan<uint> right)
    {
        if (left.IsEmpty || right.IsEmpty)
        {
            return [];
        }

        var result = new uint[Math.Min(left.Length, right.Length)];
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

        Array.Resize(ref result, count);
        return result;
    }
}

[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "RatioSD")]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class PostingsCodecBenchmarks
{
    private uint[] _ordinals = [];
    private byte[] _checkpointedPayload = [];
    private byte[] _blockPayload = [];
    private byte[] _packedBlockPayload = [];
    private byte[] _bp128Payload = [];
    private byte[] _streamVBytePayload = [];
    private uint[] _left = [];

    [ParamsSource(nameof(Cases))]
    public string CaseName { get; set; } = string.Empty;

    public IEnumerable<string> Cases => ["Target1K", "Target2K", "Target4K", "Target8K", "Large200K", "Large200KWidth8"];

    [GlobalSetup]
    public void Setup()
    {
        _ordinals = CaseName switch
        {
            "Target1K" => PostingsBenchmarkData.BuildOrdinalsForEncodedSizeTarget(1_024),
            "Target2K" => PostingsBenchmarkData.BuildOrdinalsForEncodedSizeTarget(2_048),
            "Target4K" => PostingsBenchmarkData.BuildOrdinalsForEncodedSizeTarget(4_096),
            "Target8K" => PostingsBenchmarkData.BuildOrdinalsForEncodedSizeTarget(8_192),
            "Large200K" => PostingsBenchmarkData.BuildArithmeticOrdinals(200_000, 3),
            "Large200KWidth8" => PostingsBenchmarkData.BuildArithmeticOrdinals(200_000, 2),
            _ => throw new InvalidOperationException($"Unknown postings benchmark case '{CaseName}'.")
        };
        _checkpointedPayload = PostingsCodec.Encode(_ordinals);
        _blockPayload = BlockPostingsCodec.Encode(_ordinals);
        _packedBlockPayload = PackedBlockPostingsCodec.Encode(_ordinals);
        _bp128Payload = Bp128PostingsCodec.Encode(_ordinals);
        _streamVBytePayload = StreamVBytePostingsCodec.Encode(_ordinals);
        _left = PostingsBenchmarkData.BuildArithmeticOrdinals(Math.Max(64, _ordinals.Length / 2), 5);
    }

    [Benchmark]
    public byte[] EncodeCheckpointed() => PostingsCodec.Encode(_ordinals);

    [Benchmark]
    public byte[] EncodeBlock() => BlockPostingsCodec.Encode(_ordinals);

    [Benchmark]
    public byte[] EncodePackedBlock() => PackedBlockPostingsCodec.Encode(_ordinals);

    [Benchmark]
    public byte[] EncodeBp128() => Bp128PostingsCodec.Encode(_ordinals);

    [Benchmark]
    public byte[] EncodeStreamVByte() => StreamVBytePostingsCodec.Encode(_ordinals);

    [Benchmark]
    public uint[] DecodeCheckpointed() => PostingsCodec.Decode(_checkpointedPayload);

    [Benchmark]
    public uint[] DecodeBlock() => BlockPostingsCodec.Decode(_blockPayload);

    [Benchmark]
    public uint[] DecodePackedBlock() => PackedBlockPostingsCodec.Decode(_packedBlockPayload);

    [Benchmark]
    public uint[] DecodeBp128() => Bp128PostingsCodec.Decode(_bp128Payload);

    [Benchmark]
    public uint[] DecodeStreamVByte() => StreamVBytePostingsCodec.Decode(_streamVBytePayload);

    [Benchmark]
    public uint[] IntersectCheckpointed() => PostingsCodec.IntersectSorted(_left, _checkpointedPayload, _ordinals.Length);

    [Benchmark]
    public uint[] IntersectBlock() => BlockPostingsCodec.IntersectSorted(_left, _blockPayload);

    [Benchmark]
    public uint[] IntersectPackedBlock() => PackedBlockPostingsCodec.IntersectSorted(_left, _packedBlockPayload);

    [Benchmark]
    public uint[] IntersectBp128() => Bp128PostingsCodec.IntersectSorted(_left, _bp128Payload);

    [Benchmark]
    public uint[] IntersectStreamVByte() => StreamVBytePostingsCodec.IntersectSorted(_left, _streamVBytePayload);

    [Benchmark]
    public uint[] IntersectAdaptive() => AdaptiveIntersection.IntersectSorted(_left, _ordinals, _checkpointedPayload, _ordinals.Length);

    [Benchmark]
    public uint[] IntersectAdaptiveSimd() => AdaptiveIntersection.IntersectSortedSimd(_left, _ordinals, _checkpointedPayload, _ordinals.Length);
}

[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "RatioSD")]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class PostingsIntersectionBenchmarks
{
    private uint[] _ordinals = [];
    private byte[] _checkpointedPayload = [];
    private byte[] _blockPayload = [];
    private byte[] _bp128Payload = [];
    private byte[] _streamVBytePayload = [];
    private uint[] _left = [];

    [ParamsSource(nameof(Cases))]
    public string CaseName { get; set; } = string.Empty;

    [ParamsSource(nameof(LeftShapes))]
    public string LeftShape { get; set; } = string.Empty;

    public IEnumerable<string> Cases => ["Target8K", "Large200K", "Large200KWidth8"];

    public IEnumerable<string> LeftShapes => ["SubsetHalf", "SubsetSparse", "DenseMixed", "Disjoint", "TailMiss"];

    [GlobalSetup]
    public void Setup()
    {
        _ordinals = CaseName switch
        {
            "Target8K" => PostingsBenchmarkData.BuildOrdinalsForEncodedSizeTarget(8_192),
            "Large200K" => PostingsBenchmarkData.BuildArithmeticOrdinals(200_000, 3),
            "Large200KWidth8" => PostingsBenchmarkData.BuildArithmeticOrdinals(200_000, 2),
            _ => throw new InvalidOperationException($"Unknown postings benchmark case '{CaseName}'.")
        };

        _checkpointedPayload = PostingsCodec.Encode(_ordinals);
        _blockPayload = BlockPostingsCodec.Encode(_ordinals);
        _bp128Payload = Bp128PostingsCodec.Encode(_ordinals);
        _streamVBytePayload = StreamVBytePostingsCodec.Encode(_ordinals);
        _left = LeftShape switch
        {
            "SubsetHalf" => PostingsBenchmarkData.BuildSubsetOrdinals(_ordinals, 2),
            "SubsetSparse" => PostingsBenchmarkData.BuildSubsetOrdinals(_ordinals, 16),
            "DenseMixed" => PostingsBenchmarkData.BuildArithmeticOrdinals(Math.Max(64, (_ordinals.Length * 4) / 5), 4),
            "Disjoint" => PostingsBenchmarkData.BuildDisjointOrdinals(_ordinals, Math.Max(64, _ordinals.Length / 2)),
            "TailMiss" => PostingsBenchmarkData.BuildTailMissOrdinals(_ordinals, Math.Max(64, _ordinals.Length / 2)),
            _ => throw new InvalidOperationException($"Unknown left shape '{LeftShape}'.")
        };
    }

    [Benchmark]
    public uint[] IntersectCheckpointed() => PostingsCodec.IntersectSorted(_left, _checkpointedPayload, _ordinals.Length);

    [Benchmark]
    public uint[] IntersectBlock() => BlockPostingsCodec.IntersectSorted(_left, _blockPayload);

    [Benchmark]
    public uint[] IntersectBp128() => Bp128PostingsCodec.IntersectSorted(_left, _bp128Payload);

    [Benchmark]
    public uint[] IntersectStreamVByte() => StreamVBytePostingsCodec.IntersectSorted(_left, _streamVBytePayload);

    [Benchmark]
    public uint[] IntersectAdaptive() => AdaptiveIntersection.IntersectSorted(_left, _ordinals, _checkpointedPayload, _ordinals.Length);

    [Benchmark]
    public uint[] IntersectAdaptiveSimd() => AdaptiveIntersection.IntersectSortedSimd(_left, _ordinals, _checkpointedPayload, _ordinals.Length);
}

internal static class StreamVBytePostingsCodec
{
    public static byte[] Encode(ReadOnlySpan<uint> ordinals)
    {
        if (ordinals.IsEmpty)
        {
            return [];
        }

        var count = ordinals.Length;
        var controlCount = (count + 3) >> 2;
        var payload = new ArrayBufferWriter<byte>(controlCount + (count * sizeof(uint)));
        var header = payload.GetSpan(4);
        BinaryPrimitives.WriteInt32LittleEndian(header, count);
        payload.Advance(4);

        var control = payload.GetSpan(controlCount);
        control.Slice(0, controlCount).Clear();
        payload.Advance(controlCount);

        var previous = 0u;
        for (var group = 0; group < controlCount; group++)
        {
            byte controlByte = 0;
            for (var lane = 0; lane < 4; lane++)
            {
                var index = (group << 2) + lane;
                if (index >= count)
                {
                    break;
                }

                var delta = ordinals[index] - previous;
                previous = ordinals[index];
                var byteCount = RequiredByteCount(delta);
                controlByte |= (byte)((byteCount - 1) << (lane * 2));
                var span = payload.GetSpan(byteCount);
                WriteLittleEndian(span, delta, byteCount);
                payload.Advance(byteCount);
            }

            control[group] = controlByte;
        }

        return payload.WrittenSpan.ToArray();
    }

    public static uint[] Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return [];
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(0, 4));
        if (count <= 0)
        {
            return [];
        }

        var controlCount = (count + 3) >> 2;
        var control = payload.Slice(4, controlCount);
        var data = payload.Slice(4 + controlCount);
        var result = GC.AllocateUninitializedArray<uint>(count);
        var cursor = 0;
        var previous = 0u;
        for (var group = 0; group < controlCount; group++)
        {
            var controlByte = control[group];
            for (var lane = 0; lane < 4; lane++)
            {
                var index = (group << 2) + lane;
                if (index >= count)
                {
                    break;
                }

                var byteCount = ((controlByte >> (lane * 2)) & 0x3) + 1;
                var delta = ReadLittleEndian(data.Slice(cursor, byteCount), byteCount);
                cursor += byteCount;
                previous += delta;
                result[index] = previous;
            }
        }

        return result;
    }

    public static uint[] IntersectSorted(ReadOnlySpan<uint> left, ReadOnlySpan<byte> payload)
    {
        if (left.IsEmpty || payload.IsEmpty)
        {
            return [];
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(0, 4));
        if (count <= 0)
        {
            return [];
        }

        var controlCount = (count + 3) >> 2;
        var control = payload.Slice(4, controlCount);
        var data = payload.Slice(4 + controlCount);
        var result = new uint[Math.Min(left.Length, count)];
        var leftIndex = 0;
        var matchCount = 0;
        var cursor = 0;
        var previous = 0u;
        for (var group = 0; group < controlCount && leftIndex < left.Length; group++)
        {
            var controlByte = control[group];
            for (var lane = 0; lane < 4 && leftIndex < left.Length; lane++)
            {
                var index = (group << 2) + lane;
                if (index >= count)
                {
                    break;
                }

                var byteCount = ((controlByte >> (lane * 2)) & 0x3) + 1;
                var delta = ReadLittleEndian(data.Slice(cursor, byteCount), byteCount);
                cursor += byteCount;
                previous += delta;

                while (leftIndex < left.Length && left[leftIndex] < previous)
                {
                    leftIndex++;
                }

                if (leftIndex >= left.Length)
                {
                    break;
                }

                if (left[leftIndex] == previous)
                {
                    result[matchCount++] = previous;
                    leftIndex++;
                }
            }
        }

        Array.Resize(ref result, matchCount);
        return result;
    }

    private static int RequiredByteCount(uint value)
    {
        if (value <= byte.MaxValue)
        {
            return 1;
        }

        if (value <= ushort.MaxValue)
        {
            return 2;
        }

        return value <= 0x00FF_FFFFu ? 3 : 4;
    }

    private static void WriteLittleEndian(Span<byte> destination, uint value, int byteCount)
    {
        for (var i = 0; i < byteCount; i++)
        {
            destination[i] = (byte)(value >> (i * 8));
        }
    }

    private static uint ReadLittleEndian(ReadOnlySpan<byte> source, int byteCount)
    {
        var value = 0u;
        for (var i = 0; i < byteCount; i++)
        {
            value |= (uint)source[i] << (i * 8);
        }

        return value;
    }
}

internal static class Bp128PostingsCodec
{
    private const int BlockSize = 128;

    public static byte[] Encode(ReadOnlySpan<uint> ordinals)
    {
        if (ordinals.IsEmpty)
        {
            return [];
        }

        var payload = new ArrayBufferWriter<byte>(EstimateCapacity(ordinals.Length));
        var deltas = new uint[BlockSize - 1];
        var words = new uint[BlockSize * sizeof(uint)];
        for (var offset = 0; offset < ordinals.Length; offset += BlockSize)
        {
            var count = Math.Min(BlockSize, ordinals.Length - offset);
            var first = ordinals[offset];
            var maxDelta = 0u;
            for (var i = 1; i < count; i++)
            {
                var delta = ordinals[offset + i] - first;
                deltas[i - 1] = delta;
                maxDelta |= delta;
            }

            var bitWidth = maxDelta == 0 ? 0 : BitOperations.Log2(maxDelta) + 1;
            var wordCount = ((count - 1) * bitWidth + 31) >> 5;
            Array.Clear(words, 0, wordCount);
            PackWords(deltas.AsSpan(0, count - 1), bitWidth, words.AsSpan(0, wordCount));

            var header = payload.GetSpan(8);
            BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)count);
            header[2] = (byte)bitWidth;
            header[3] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4, 4), first);
            payload.Advance(8);

            var body = payload.GetSpan(wordCount * sizeof(uint));
            MemoryMarshal.Cast<byte, uint>(body.Slice(0, wordCount * sizeof(uint))).Clear();
            words.AsSpan(0, wordCount).CopyTo(MemoryMarshal.Cast<byte, uint>(body));
            payload.Advance(wordCount * sizeof(uint));
        }

        return payload.WrittenSpan.ToArray();
    }

    public static uint[] Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return [];
        }

        var values = new List<uint>(payload.Length / 2);
        var unpacked = new uint[BlockSize - 1];
        var cursor = 0;
        while (cursor < payload.Length)
        {
            var count = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
            var bitWidth = payload[cursor + 2];
            var first = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor + 4, 4));
            cursor += 8;
            var wordCount = ((count - 1) * bitWidth + 31) >> 5;
            UnpackWords(MemoryMarshal.Cast<byte, uint>(payload.Slice(cursor, wordCount * sizeof(uint))), bitWidth, unpacked.AsSpan(0, count - 1));
            cursor += wordCount * sizeof(uint);

            values.Add(first);
            for (var i = 0; i < count - 1; i++)
            {
                values.Add(first + unpacked[i]);
            }
        }

        return values.ToArray();
    }

    public static uint[] IntersectSorted(ReadOnlySpan<uint> left, ReadOnlySpan<byte> payload)
    {
        if (left.IsEmpty || payload.IsEmpty)
        {
            return [];
        }

        var result = new uint[left.Length];
        var matchCount = 0;
        var leftIndex = 0;
        var cursor = 0;
        var unpacked = new uint[BlockSize - 1];
        while (leftIndex < left.Length && cursor < payload.Length)
        {
            var blockCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
            var bitWidth = payload[cursor + 2];
            var first = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor + 4, 4));
            cursor += 8;
            var wordCount = ((blockCount - 1) * bitWidth + 31) >> 5;
            UnpackWords(MemoryMarshal.Cast<byte, uint>(payload.Slice(cursor, wordCount * sizeof(uint))), bitWidth, unpacked.AsSpan(0, blockCount - 1));
            cursor += wordCount * sizeof(uint);

            var last = blockCount == 1 ? first : first + unpacked[blockCount - 2];
            while (leftIndex < left.Length && left[leftIndex] < first)
            {
                leftIndex++;
            }

            if (leftIndex >= left.Length)
            {
                break;
            }

            if (left[leftIndex] > last)
            {
                continue;
            }

            if (left[leftIndex] == first)
            {
                result[matchCount++] = first;
                leftIndex++;
                if (leftIndex >= left.Length)
                {
                    break;
                }
            }

            for (var i = 0; i < blockCount - 1 && leftIndex < left.Length; i++)
            {
                var ordinal = first + unpacked[i];
                while (leftIndex < left.Length && left[leftIndex] < ordinal)
                {
                    leftIndex++;
                }

                if (leftIndex >= left.Length)
                {
                    break;
                }

                if (left[leftIndex] == ordinal)
                {
                    result[matchCount++] = ordinal;
                    leftIndex++;
                }
            }
        }

        Array.Resize(ref result, matchCount);
        return result;
    }

    private static int EstimateCapacity(int ordinalCount)
    {
        var blocks = (ordinalCount + BlockSize - 1) / BlockSize;
        return blocks * (8 + BlockSize * sizeof(uint));
    }

    private static void PackWords(ReadOnlySpan<uint> values, int bitWidth, Span<uint> words)
    {
        if (bitWidth == 0 || values.IsEmpty)
        {
            return;
        }

        var bitOffset = 0;
        foreach (var value in values)
        {
            var wordIndex = bitOffset >> 5;
            var bitIndex = bitOffset & 31;
            words[wordIndex] |= value << bitIndex;
            if (bitIndex + bitWidth > 32)
            {
                words[wordIndex + 1] |= value >> (32 - bitIndex);
            }

            bitOffset += bitWidth;
        }
    }

    private static void UnpackWords(ReadOnlySpan<uint> words, int bitWidth, Span<uint> destination)
    {
        if (destination.IsEmpty)
        {
            return;
        }

        if (bitWidth == 0)
        {
            destination.Clear();
            return;
        }

        if (bitWidth == 32)
        {
            words[..destination.Length].CopyTo(destination);
            return;
        }

        var mask = bitWidth == 32 ? uint.MaxValue : (1u << bitWidth) - 1u;
        var bitOffset = 0;
        for (var i = 0; i < destination.Length; i++)
        {
            var wordIndex = bitOffset >> 5;
            var bitIndex = bitOffset & 31;
            var value = words[wordIndex] >> bitIndex;
            if (bitIndex + bitWidth > 32)
            {
                value |= words[wordIndex + 1] << (32 - bitIndex);
            }

            destination[i] = value & mask;
            bitOffset += bitWidth;
        }
    }
}

internal static class PackedBlockPostingsCodec
{
    private const int BlockSize = 128;

    public static byte[] Encode(ReadOnlySpan<uint> ordinals)
    {
        if (ordinals.Length == 0)
        {
            return [];
        }

        var payload = new ArrayBufferWriter<byte>(EstimateCapacity(ordinals.Length));
        var blockBuffer = new uint[BlockSize - 1];
        for (var offset = 0; offset < ordinals.Length; offset += BlockSize)
        {
            var count = Math.Min(BlockSize, ordinals.Length - offset);
            var first = ordinals[offset];
            var maxDelta = 0u;
            for (var i = 1; i < count; i++)
            {
                var delta = ordinals[offset + i] - first;
                blockBuffer[i - 1] = delta;
                maxDelta |= delta;
            }

            var bitWidth = maxDelta == 0 ? 0 : BitOperations.Log2(maxDelta) + 1;
            var packedBytes = ((count - 1) * bitWidth + 7) >> 3;

            var header = payload.GetSpan(8);
            BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)count);
            header[2] = (byte)bitWidth;
            header[3] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4, 4), first);
            payload.Advance(8);

            var body = payload.GetSpan(packedBytes);
            body.Slice(0, packedBytes).Clear();
            Pack(blockBuffer.AsSpan(0, count - 1), bitWidth, body);
            payload.Advance(packedBytes);
        }

        return payload.WrittenSpan.ToArray();
    }

    public static uint[] Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return [];
        }

        var values = new List<uint>(payload.Length / 2);
        var unpacked = new uint[BlockSize - 1];
        var cursor = 0;
        while (cursor < payload.Length)
        {
            var count = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
            var bitWidth = payload[cursor + 2];
            var first = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor + 4, 4));
            cursor += 8;
            var packedBytes = ((count - 1) * bitWidth + 7) >> 3;
            Unpack(payload.Slice(cursor, packedBytes), bitWidth, unpacked.AsSpan(0, count - 1));
            cursor += packedBytes;

            values.Add(first);
            for (var i = 0; i < count - 1; i++)
            {
                values.Add(first + unpacked[i]);
            }
        }

        return values.ToArray();
    }

    public static uint[] IntersectSorted(ReadOnlySpan<uint> left, ReadOnlySpan<byte> payload)
    {
        if (left.IsEmpty || payload.IsEmpty)
        {
            return [];
        }

        var result = new uint[left.Length];
        var count = 0;
        var leftIndex = 0;
        var cursor = 0;
        var unpacked = new uint[BlockSize - 1];
        while (leftIndex < left.Length && cursor < payload.Length)
        {
            var blockCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
            var bitWidth = payload[cursor + 2];
            var first = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor + 4, 4));
            cursor += 8;
            var packedBytes = ((blockCount - 1) * bitWidth + 7) >> 3;
            Unpack(payload.Slice(cursor, packedBytes), bitWidth, unpacked.AsSpan(0, blockCount - 1));
            cursor += packedBytes;

            var last = blockCount == 1 ? first : first + unpacked[blockCount - 2];
            while (leftIndex < left.Length && left[leftIndex] < first)
            {
                leftIndex++;
            }

            if (leftIndex >= left.Length)
            {
                break;
            }

            if (left[leftIndex] > last)
            {
                continue;
            }

            if (left[leftIndex] == first)
            {
                result[count++] = first;
                leftIndex++;
                if (leftIndex >= left.Length)
                {
                    break;
                }
            }

            for (var i = 0; i < blockCount - 1 && leftIndex < left.Length; i++)
            {
                var ordinal = first + unpacked[i];
                while (leftIndex < left.Length && left[leftIndex] < ordinal)
                {
                    leftIndex++;
                }

                if (leftIndex >= left.Length)
                {
                    break;
                }

                if (left[leftIndex] == ordinal)
                {
                    result[count++] = ordinal;
                    leftIndex++;
                }
            }
        }

        Array.Resize(ref result, count);
        return result;
    }

    private static int EstimateCapacity(int ordinalCount)
    {
        var blocks = (ordinalCount + BlockSize - 1) / BlockSize;
        return blocks * (8 + (BlockSize - 1) * sizeof(uint));
    }

    private static void Pack(ReadOnlySpan<uint> values, int bitWidth, Span<byte> destination)
    {
        if (bitWidth == 0 || values.IsEmpty)
        {
            return;
        }

        var bitOffset = 0;
        foreach (var value in values)
        {
            WriteBits(destination, bitOffset, value, bitWidth);
            bitOffset += bitWidth;
        }
    }

    private static void Unpack(ReadOnlySpan<byte> source, int bitWidth, Span<uint> destination)
    {
        if (destination.IsEmpty)
        {
            return;
        }

        if (bitWidth == 0)
        {
            destination.Clear();
            return;
        }

        var bitOffset = 0;
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = ReadBits(source, bitOffset, bitWidth);
            bitOffset += bitWidth;
        }
    }

    private static void WriteBits(Span<byte> destination, int bitOffset, uint value, int bitWidth)
    {
        var remaining = bitWidth;
        var localOffset = bitOffset;
        var shifted = value;
        while (remaining > 0)
        {
            var byteIndex = localOffset >> 3;
            var bitIndex = localOffset & 7;
            var bitsThisByte = Math.Min(remaining, 8 - bitIndex);
            var mask = (1u << bitsThisByte) - 1u;
            destination[byteIndex] |= (byte)((shifted & mask) << bitIndex);
            shifted >>= bitsThisByte;
            localOffset += bitsThisByte;
            remaining -= bitsThisByte;
        }
    }

    private static uint ReadBits(ReadOnlySpan<byte> source, int bitOffset, int bitWidth)
    {
        var remaining = bitWidth;
        var localOffset = bitOffset;
        var result = 0u;
        var shift = 0;
        while (remaining > 0)
        {
            var byteIndex = localOffset >> 3;
            var bitIndex = localOffset & 7;
            var bitsThisByte = Math.Min(remaining, 8 - bitIndex);
            var mask = (1u << bitsThisByte) - 1u;
            var chunk = (uint)((source[byteIndex] >> bitIndex) & mask);
            result |= chunk << shift;
            localOffset += bitsThisByte;
            remaining -= bitsThisByte;
            shift += bitsThisByte;
        }

        return result;
    }
}

internal static class BlockPostingsCodec
{
    private const int BlockSize = 128;

    public static byte[] Encode(ReadOnlySpan<uint> ordinals)
    {
        if (ordinals.Length == 0)
        {
            return [];
        }

        var totalBytes = 0;
        for (var offset = 0; offset < ordinals.Length; offset += BlockSize)
        {
            var count = Math.Min(BlockSize, ordinals.Length - offset);
            totalBytes += 2 + 4 + 4 + ((count - 1) * 4);
        }

        var payload = GC.AllocateUninitializedArray<byte>(totalBytes);
        var cursor = 0;
        for (var offset = 0; offset < ordinals.Length; offset += BlockSize)
        {
            var count = Math.Min(BlockSize, ordinals.Length - offset);
            var first = ordinals[offset];
            var last = ordinals[offset + count - 1];
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(cursor, 2), (ushort)count);
            cursor += 2;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(cursor, 4), first);
            cursor += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(cursor, 4), last);
            cursor += 4;
            for (var i = 1; i < count; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(cursor, 4), ordinals[offset + i] - first);
                cursor += 4;
            }
        }

        return payload;
    }

    public static uint[] Decode(ReadOnlySpan<byte> payload)
    {
        var values = new List<uint>(payload.Length / 4);
        var cursor = 0;
        while (cursor < payload.Length)
        {
            var count = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
            cursor += 2;
            var first = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor, 4));
            cursor += 4;
            _ = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor, 4));
            cursor += 4;
            values.Add(first);
            for (var i = 1; i < count; i++)
            {
                values.Add(first + BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor, 4)));
                cursor += 4;
            }
        }

        return values.ToArray();
    }

    public static uint[] IntersectSorted(ReadOnlySpan<uint> left, ReadOnlySpan<byte> payload)
    {
        if (left.Length == 0 || payload.Length == 0)
        {
            return [];
        }

        var result = new uint[left.Length];
        var count = 0;
        var leftIndex = 0;
        var cursor = 0;
        while (leftIndex < left.Length && cursor < payload.Length)
        {
            var blockCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
            cursor += 2;
            var first = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor, 4));
            cursor += 4;
            var last = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor, 4));
            cursor += 4;
            var blockCursor = cursor;
            var blockEnd = cursor + ((blockCount - 1) * 4);

            while (leftIndex < left.Length && left[leftIndex] < first)
            {
                leftIndex++;
            }

            if (leftIndex >= left.Length)
            {
                break;
            }

            if (left[leftIndex] > last)
            {
                cursor = blockEnd;
                continue;
            }

            if (left[leftIndex] == first)
            {
                result[count++] = first;
                leftIndex++;
                if (leftIndex >= left.Length)
                {
                    break;
                }
            }

            for (var i = 1; i < blockCount && leftIndex < left.Length; i++)
            {
                var ordinal = first + BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(blockCursor, 4));
                blockCursor += 4;
                while (leftIndex < left.Length && left[leftIndex] < ordinal)
                {
                    leftIndex++;
                }

                if (leftIndex >= left.Length)
                {
                    break;
                }

                if (left[leftIndex] == ordinal)
                {
                    result[count++] = ordinal;
                    leftIndex++;
                }
            }

            cursor = blockEnd;
        }

        Array.Resize(ref result, count);
        return result;
    }
}

internal sealed class PostingsStatsAccumulator
{
    private static readonly (string Key, int MinInclusive, int MaxExclusive)[] EncodedByteBucketDefinitions =
    [
        ("1-31", 1, 32),
        ("32-127", 32, 128),
        ("128-511", 128, 512),
        ("512-2047", 512, 2048),
        ("2048-8191", 2048, 8192),
        ("8192+", 8192, int.MaxValue)
    ];

    private static readonly (string Key, int MinInclusive, int MaxExclusive)[] DocumentFrequencyBucketDefinitions =
    [
        ("1-31", 1, 32),
        ("32-127", 32, 128),
        ("128-511", 128, 512),
        ("512-2047", 512, 2048),
        ("2048-8191", 2048, 8192),
        ("8192+", 8192, int.MaxValue)
    ];

    private static readonly int[] Thresholds = [1024, 2048, 4096, 8192, 16384, 32768];

    private readonly long[] _encodedByteBucketCounts = new long[EncodedByteBucketDefinitions.Length];
    private readonly long[] _encodedByteBucketBytes = new long[EncodedByteBucketDefinitions.Length];
    private readonly long[] _docFreqBucketCounts = new long[DocumentFrequencyBucketDefinitions.Length];
    private readonly long[] _docFreqBucketBytes = new long[DocumentFrequencyBucketDefinitions.Length];
    private readonly long[] _thresholdCounts = new long[Thresholds.Length];
    private readonly long[] _thresholdBytes = new long[Thresholds.Length];
    private readonly long[] _thresholdBp128Bytes = new long[Thresholds.Length];
    private readonly long[] _thresholdStreamVByteBytes = new long[Thresholds.Length];
    private readonly Dictionary<string, RegionTotals> _regions = new(StringComparer.Ordinal);
    private long _totalPostingLists;
    private long _totalEncodedBytes;
    private long _totalBp128Bytes;
    private long _totalStreamVByteBytes;
    private long _checkpointedPostingLists;
    private long _checkpointedEncodedBytes;

    public void Add(string region, int documentFrequency, int encodedBytes, int bp128Bytes, int streamVByteBytes)
    {
        _totalPostingLists++;
        _totalEncodedBytes += encodedBytes;
        _totalBp128Bytes += bp128Bytes;
        _totalStreamVByteBytes += streamVByteBytes;
        if (encodedBytes >= PostingsCodec.CheckpointThreshold)
        {
            _checkpointedPostingLists++;
            _checkpointedEncodedBytes += encodedBytes;
        }

        var encodedBucket = FindBucket(encodedBytes, EncodedByteBucketDefinitions);
        _encodedByteBucketCounts[encodedBucket]++;
        _encodedByteBucketBytes[encodedBucket] += encodedBytes;

        var docFreqBucket = FindBucket(documentFrequency, DocumentFrequencyBucketDefinitions);
        _docFreqBucketCounts[docFreqBucket]++;
        _docFreqBucketBytes[docFreqBucket] += encodedBytes;

        for (var i = 0; i < Thresholds.Length; i++)
        {
            if (encodedBytes >= Thresholds[i])
            {
                _thresholdCounts[i]++;
                _thresholdBytes[i] += encodedBytes;
                _thresholdBp128Bytes[i] += bp128Bytes;
                _thresholdStreamVByteBytes[i] += streamVByteBytes;
            }
        }

        ref var totals = ref CollectionsMarshal.GetValueRefOrAddDefault(_regions, region, out _);
        totals.PostingLists++;
        totals.EncodedBytes += encodedBytes;
        totals.Bp128Bytes += bp128Bytes;
        totals.StreamVByteBytes += streamVByteBytes;
        if (encodedBytes >= PostingsCodec.CheckpointThreshold)
        {
            totals.CheckpointedPostingLists++;
            totals.CheckpointedEncodedBytes += encodedBytes;
        }
    }

    public PostingsStatsCorpusResult ToResult(string corpusId, string path, int segmentCount)
    {
        var encodedBuckets = new PostingsStatsBucket[EncodedByteBucketDefinitions.Length];
        for (var i = 0; i < encodedBuckets.Length; i++)
        {
            encodedBuckets[i] = new PostingsStatsBucket(
                EncodedByteBucketDefinitions[i].Key,
                _encodedByteBucketCounts[i],
                _encodedByteBucketBytes[i],
                Percent(_encodedByteBucketCounts[i], _totalPostingLists),
                Percent(_encodedByteBucketBytes[i], _totalEncodedBytes));
        }

        var docFreqBuckets = new PostingsStatsBucket[DocumentFrequencyBucketDefinitions.Length];
        for (var i = 0; i < docFreqBuckets.Length; i++)
        {
            docFreqBuckets[i] = new PostingsStatsBucket(
                DocumentFrequencyBucketDefinitions[i].Key,
                _docFreqBucketCounts[i],
                _docFreqBucketBytes[i],
                Percent(_docFreqBucketCounts[i], _totalPostingLists),
                Percent(_docFreqBucketBytes[i], _totalEncodedBytes));
        }

        var thresholds = new PostingsStatsThresholdSummary[Thresholds.Length];
        for (var i = 0; i < Thresholds.Length; i++)
        {
            thresholds[i] = new PostingsStatsThresholdSummary(
                Thresholds[i],
                _thresholdCounts[i],
                _thresholdBytes[i],
                _thresholdBp128Bytes[i],
                _thresholdStreamVByteBytes[i],
                Percent(_thresholdCounts[i], _totalPostingLists),
                Percent(_thresholdBytes[i], _totalEncodedBytes),
                InflationPercent(_thresholdBp128Bytes[i], _thresholdBytes[i]),
                InflationPercent(_thresholdStreamVByteBytes[i], _thresholdBytes[i]));
        }

        var regions = _regions
            .OrderByDescending(static pair => pair.Value.EncodedBytes)
            .Select(static pair => new PostingsStatsRegionSummary(
                pair.Key,
                pair.Value.PostingLists,
                pair.Value.EncodedBytes,
                pair.Value.Bp128Bytes,
                pair.Value.StreamVByteBytes,
                pair.Value.CheckpointedPostingLists,
                pair.Value.CheckpointedEncodedBytes,
                InflationPercent(pair.Value.Bp128Bytes, pair.Value.EncodedBytes),
                InflationPercent(pair.Value.StreamVByteBytes, pair.Value.EncodedBytes)))
            .ToArray();

        return new PostingsStatsCorpusResult(
            corpusId,
            path,
            segmentCount,
            _totalPostingLists,
            _totalEncodedBytes,
            _totalBp128Bytes,
            _totalStreamVByteBytes,
            _checkpointedPostingLists,
            _checkpointedEncodedBytes,
            InflationPercent(_totalBp128Bytes, _totalEncodedBytes),
            InflationPercent(_totalStreamVByteBytes, _totalEncodedBytes),
            encodedBuckets,
            docFreqBuckets,
            thresholds,
            regions);
    }

    private static int FindBucket(int value, (string Key, int MinInclusive, int MaxExclusive)[] definitions)
    {
        for (var i = 0; i < definitions.Length; i++)
        {
            if (value >= definitions[i].MinInclusive && value < definitions[i].MaxExclusive)
            {
                return i;
            }
        }

        return definitions.Length - 1;
    }

    private static double Percent(long part, long whole)
    {
        return whole == 0 ? 0d : (double)part / whole * 100d;
    }

    private static double InflationPercent(long alternativeBytes, long baselineBytes)
    {
        return baselineBytes == 0 ? 0d : ((double)alternativeBytes - baselineBytes) / baselineBytes * 100d;
    }

    private struct RegionTotals
    {
        public long PostingLists;
        public long EncodedBytes;
        public long Bp128Bytes;
        public long StreamVByteBytes;
        public long CheckpointedPostingLists;
        public long CheckpointedEncodedBytes;
    }
}
