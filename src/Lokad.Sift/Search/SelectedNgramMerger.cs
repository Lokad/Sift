namespace Lokad.Sift.Search;

internal static class SelectedNgramMerger
{
    public static SelectedNgram[] Merge(
        IReadOnlyList<SelectedNgram> left,
        IReadOnlyList<SelectedNgram> right)
    {
        if (left.Count == 0)
        {
            return [.. right];
        }

        if (right.Count == 0)
        {
            return [.. left];
        }

        var merged = new Dictionary<(uint PackedValue, int ByteLength), int>();
        AddRange(merged, left);
        AddRange(merged, right);
        return Materialize(merged);
    }

    public static void Add(
        Dictionary<(uint PackedValue, int ByteLength), int> target,
        SelectedNgram gram)
    {
        var key = (gram.PackedValue, gram.ByteLength);
        target.TryGetValue(key, out var documentFrequency);
        target[key] = checked(documentFrequency + gram.DocumentFrequency);
    }

    public static void Add(
        Dictionary<(uint PackedValue, int ByteLength), int> target,
        SelectedGram gram)
    {
        var key = ((uint)gram.Gram, gram.GramLength);
        target.TryGetValue(key, out var documentFrequency);
        target[key] = checked(documentFrequency + gram.DocFreq);
    }

    public static SelectedNgram[] Materialize(
        Dictionary<(uint PackedValue, int ByteLength), int> source)
    {
        return source
            .Select(static pair => new SelectedNgram(
                pair.Key.PackedValue,
                pair.Key.ByteLength,
                pair.Value))
            .OrderBy(static gram => gram.ByteLength)
            .ThenBy(static gram => gram.PackedValue)
            .ToArray();
    }

    private static void AddRange(
        Dictionary<(uint PackedValue, int ByteLength), int> target,
        IReadOnlyList<SelectedNgram> source)
    {
        for (var i = 0; i < source.Count; i++)
        {
            Add(target, source[i]);
        }
    }
}
