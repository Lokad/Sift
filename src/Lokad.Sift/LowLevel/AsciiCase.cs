using System.Runtime.CompilerServices;

namespace Lokad.Sift.LowLevel;

internal static class AsciiCase
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Fold(byte value)
    {
        return value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;
    }
}
