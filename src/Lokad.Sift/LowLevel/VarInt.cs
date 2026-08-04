namespace Lokad.Sift.LowLevel;

internal static class VarInt
{
    public static int WriteUInt32(Span<byte> buffer, uint value)
    {
        var written = 0;
        while (value >= 0x80)
        {
            buffer[written++] = (byte)(value | 0x80);
            value >>= 7;
        }

        buffer[written++] = (byte)value;
        return written;
    }

    public static uint ReadUInt32(ReadOnlySpan<byte> buffer, ref int offset)
    {
        var cursor = offset;
        if ((uint)cursor > (uint)buffer.Length)
        {
            throw new InvalidDataException("The variable integer offset is outside the payload.");
        }

        uint value = 0;
        for (var shift = 0; shift <= 28; shift += 7)
        {
            if (cursor == buffer.Length)
            {
                throw new InvalidDataException("The variable integer is truncated.");
            }

            var current = buffer[cursor++];
            if (shift == 28 && (current & 0xF0) != 0)
            {
                throw new InvalidDataException("The variable integer exceeds UInt32.");
            }

            value |= (uint)(current & 0x7Fu) << shift;
            if ((current & 0x80) == 0)
            {
                offset = cursor;
                return value;
            }
        }

        throw new InvalidDataException("The variable integer exceeds UInt32.");
    }

    public static int GetUInt32Length(uint value)
    {
        var length = 1;
        while (value >= 0x80)
        {
            length++;
            value >>= 7;
        }

        return length;
    }
}
