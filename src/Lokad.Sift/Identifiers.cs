namespace Lokad.Sift;

public readonly record struct CorpusId(Guid Value) : IComparable<CorpusId>
{
    public static CorpusId Empty => new(Guid.Empty);

    internal static CorpusId New() => new(Guid.NewGuid());

    public int CompareTo(CorpusId other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString();
}

public readonly record struct ContentRevision(ulong Value) : IComparable<ContentRevision>
{
    internal ContentRevision Next() => new(checked(Value + 1UL));

    public int CompareTo(ContentRevision other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString();
}

public readonly record struct StorageGeneration(ulong Value) : IComparable<StorageGeneration>
{
    internal StorageGeneration Next() => new(checked(Value + 1UL));

    public int CompareTo(StorageGeneration other) => Value.CompareTo(other.Value);

    public string ToString(string format) => Value.ToString(format);

    public override string ToString() => Value.ToString();
}

public readonly record struct SegmentId(ulong Value) : IComparable<SegmentId>
{
    public int CompareTo(SegmentId other) => Value.CompareTo(other.Value);

    public string ToString(string format) => Value.ToString(format);

    public override string ToString() => Value.ToString();
}

public readonly record struct DocumentOrdinal(uint Value) : IComparable<DocumentOrdinal>
{
    public int CompareTo(DocumentOrdinal other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString();
}
