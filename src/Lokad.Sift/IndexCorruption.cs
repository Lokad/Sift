namespace Lokad.Sift;

internal static class IndexCorruption
{
    public static bool IsIndexCorruption(Exception exception)
    {
        return exception is InvalidDataException or
            FileNotFoundException or
            DirectoryNotFoundException or
            EndOfStreamException;
    }
}
