namespace Lokad.Sift;

internal static class PublicResultCollection
{
    public static IReadOnlyList<T> CopyOf<T>(IReadOnlyList<T> values)
    {
        if (values.Count == 0)
        {
            return Array.Empty<T>();
        }

        var copy = new T[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            copy[i] = values[i];
        }

        return Array.AsReadOnly(copy);
    }

    public static IReadOnlyList<T> Own<T>(T[] values) =>
        values.Length == 0 ? Array.Empty<T>() : Array.AsReadOnly(values);
}
