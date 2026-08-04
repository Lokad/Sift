namespace Lokad.Sift;

public readonly record struct CorpusId(Guid Value)
{
    public static CorpusId Empty => new(Guid.Empty);

    public override string ToString() => Value.ToString();
}

public readonly record struct ContentRevision(ulong Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct StorageGeneration(ulong Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct SegmentId(ulong Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct DocumentOrdinal(uint Value)
{
    public override string ToString() => Value.ToString();
}
