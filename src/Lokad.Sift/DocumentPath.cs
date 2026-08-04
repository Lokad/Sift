namespace Lokad.Sift;

internal static class DocumentPath
{
    public static string Normalize(string path)
    {
        if (path.Length == 0 || path.AsSpan().Trim().IsEmpty)
        {
            throw new ArgumentException("Document path cannot be empty or whitespace.", nameof(path));
        }

        path = path.Replace('\\', '/');
        if (path[0] == '/' ||
            path.Length >= 2 &&
            path[1] == ':' &&
            path[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
        {
            throw new ArgumentException("Document path must be relative.", nameof(path));
        }

        var segmentStart = 0;
        for (var i = 0; i <= path.Length; i++)
        {
            if (i < path.Length && path[i] != '/')
            {
                continue;
            }

            var segment = path.AsSpan(segmentStart, i - segmentStart);
            if (segment.Length == 0 || segment.SequenceEqual(".") || segment.SequenceEqual(".."))
            {
                throw new ArgumentException("Document path contains invalid segments.", nameof(path));
            }

            segmentStart = i + 1;
        }

        return path;
    }
}
