using System.Buffers.Binary;

namespace Lokad.Sift.LowLevel;

internal static class LineMap
{
    public const int AnchorStride = 128;

    public static int[] BuildLineStarts(ReadOnlySpan<byte> content)
    {
        var starts = new List<int>(16) { 0 };
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == (byte)'\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts.ToArray();
    }

    public static byte[] EncodeLineIndex(ReadOnlySpan<int> lineStarts)
    {
        var lineCount = lineStarts.Length;
        var anchorCount = lineCount == 0 ? 0 : ((lineCount - 1) / AnchorStride) + 1;
        var bytes = GC.AllocateUninitializedArray<byte>(8 + anchorCount * 8 + lineCount * 4);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), lineCount);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), anchorCount);

        var offset = 8;
        for (var anchorIndex = 0; anchorIndex < anchorCount; anchorIndex++)
        {
            var lineIndex = anchorIndex * AnchorStride;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), lineIndex);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 4, 4), lineStarts[lineIndex]);
            offset += 8;
        }

        for (var i = 0; i < lineCount; i++)
        {
            var anchorBase = (i / AnchorStride) * AnchorStride;
            var delta = lineStarts[i] - lineStarts[anchorBase];
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), delta);
            offset += 4;
        }

        return bytes;
    }

    public static byte[] EncodeLineIndex(ReadOnlySpan<byte> content, out int lineCount)
    {
        lineCount = 1;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == (byte)'\n')
            {
                lineCount++;
            }
        }

        var anchorCount = ((lineCount - 1) / AnchorStride) + 1;
        var bytes = GC.AllocateUninitializedArray<byte>(8 + anchorCount * 8 + lineCount * 4);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), lineCount);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), anchorCount);

        var anchorsOffset = 8;
        var deltasOffset = anchorsOffset + anchorCount * 8;
        var lineIndex = 0;
        var anchorStart = 0;
        WriteLineStart(0);
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == (byte)'\n')
            {
                WriteLineStart(i + 1);
            }
        }

        return bytes;

        void WriteLineStart(int lineStart)
        {
            if (lineIndex % AnchorStride == 0)
            {
                anchorStart = lineStart;
                var anchorOffset = anchorsOffset + lineIndex / AnchorStride * 8;
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(anchorOffset, 4), lineIndex);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(anchorOffset + 4, 4), anchorStart);
            }

            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(deltasOffset + lineIndex * 4, 4),
                lineStart - anchorStart);
            lineIndex++;
        }
    }

    public static int[] DecodeLineIndex(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length < 8)
        {
            return [0];
        }

        var lineCount = BinaryPrimitives.ReadInt32LittleEndian(encoded[..4]);
        var anchorCount = BinaryPrimitives.ReadInt32LittleEndian(encoded.Slice(4, 4));
        var anchorsOffset = 8;
        var deltasOffset = anchorsOffset + anchorCount * 8;
        var starts = new int[lineCount];

        for (var i = 0; i < lineCount; i++)
        {
            var anchorIndex = i / AnchorStride;
            var anchorOffset = anchorsOffset + anchorIndex * 8;
            var anchorValue = BinaryPrimitives.ReadInt32LittleEndian(encoded.Slice(anchorOffset + 4, 4));
            var deltaOffset = deltasOffset + i * 4;
            starts[i] = anchorValue + BinaryPrimitives.ReadInt32LittleEndian(encoded.Slice(deltaOffset, 4));
        }

        return starts;
    }

    public static int GetLineCount(ReadOnlySpan<byte> encoded)
    {
        return encoded.Length >= 4
            ? Math.Max(1, BinaryPrimitives.ReadInt32LittleEndian(encoded[..4]))
            : 1;
    }

    public static int GetLineFromByteOffset(ReadOnlySpan<int> lineStarts, int byteOffset)
    {
        var index = lineStarts.BinarySearch(byteOffset);
        if (index >= 0)
        {
            return index + 1;
        }

        return ~index;
    }

    public static int GetLineStart(ReadOnlySpan<int> lineStarts, int oneBasedLine)
    {
        return lineStarts[Math.Max(0, oneBasedLine - 1)];
    }

    public static int GetLineEndExclusive(ReadOnlySpan<int> lineStarts, int oneBasedLine, int contentLength)
    {
        var index = Math.Max(0, oneBasedLine - 1);
        return index + 1 < lineStarts.Length ? lineStarts[index + 1] : contentLength;
    }

    public static int GetLineFromByteOffset(ReadOnlySpan<byte> encoded, int byteOffset)
    {
        var lineCount = GetLineCount(encoded);
        var low = 0;
        var high = lineCount - 1;
        while (low <= high)
        {
            var mid = low + ((high - low) >> 1);
            var current = GetLineStart(encoded, mid + 1);
            if (current == byteOffset)
            {
                return mid + 1;
            }

            if (current < byteOffset)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return Math.Max(1, low);
    }

    public static int GetLineStart(ReadOnlySpan<byte> encoded, int oneBasedLine)
    {
        if (encoded.Length < 8)
        {
            return 0;
        }

        var lineCount = BinaryPrimitives.ReadInt32LittleEndian(encoded[..4]);
        var clampedLine = Math.Clamp(oneBasedLine, 1, Math.Max(1, lineCount));
        var zeroBasedLine = clampedLine - 1;
        var anchorCount = BinaryPrimitives.ReadInt32LittleEndian(encoded.Slice(4, 4));
        var anchorsOffset = 8;
        var deltasOffset = anchorsOffset + anchorCount * 8;
        var anchorIndex = zeroBasedLine / AnchorStride;
        var anchorOffset = anchorsOffset + anchorIndex * 8;
        var anchorValue = BinaryPrimitives.ReadInt32LittleEndian(encoded.Slice(anchorOffset + 4, 4));
        var deltaOffset = deltasOffset + zeroBasedLine * 4;
        return anchorValue + BinaryPrimitives.ReadInt32LittleEndian(encoded.Slice(deltaOffset, 4));
    }

    public static int GetLineEndExclusive(ReadOnlySpan<byte> encoded, int oneBasedLine, int contentLength)
    {
        var lineCount = GetLineCount(encoded);
        var clampedLine = Math.Clamp(oneBasedLine, 1, lineCount);
        return clampedLine < lineCount
            ? GetLineStart(encoded, clampedLine + 1)
            : contentLength;
    }
}
