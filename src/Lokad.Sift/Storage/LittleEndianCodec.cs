using System.Buffers.Binary;
namespace Lokad.Sift.Storage;

internal static class LittleEndianCodec
{
    public static void WriteInt32(Span<byte> buffer, int value) => BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
    public static void WriteUInt16(Span<byte> buffer, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
    public static void WriteInt64(Span<byte> buffer, long value) => BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
    public static void WriteUInt64(Span<byte> buffer, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);

    public static int ReadInt32(ReadOnlySpan<byte> buffer) => BinaryPrimitives.ReadInt32LittleEndian(buffer);
    public static long ReadInt64(ReadOnlySpan<byte> buffer) => BinaryPrimitives.ReadInt64LittleEndian(buffer);
    public static ulong ReadUInt64(ReadOnlySpan<byte> buffer) => BinaryPrimitives.ReadUInt64LittleEndian(buffer);
}
