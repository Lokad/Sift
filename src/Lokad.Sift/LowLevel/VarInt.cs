namespace Lokad.Sift.LowLevel;

public static class VarInt
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
        uint value = 0;
        var shift = 0;
        while (true)
        {
            var current = buffer[offset++];
            value |= (uint)(current & 0x7Fu) << shift;
            if ((current & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
        }
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
