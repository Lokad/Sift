namespace Lokad.Sift.Storage;

internal readonly struct DeadOrdinalSet : IEquatable<DeadOrdinalSet>
{
    private readonly uint[]? _ordinals;

    private DeadOrdinalSet(uint[] ordinals)
    {
        _ordinals = ordinals;
    }

    public static DeadOrdinalSet Empty => default;
    public int Length => _ordinals?.Length ?? 0;
    public uint this[int index] => (_ordinals ?? Array.Empty<uint>())[index];
    public ReadOnlySpan<uint> Span => _ordinals is null ? ReadOnlySpan<uint>.Empty : _ordinals;

    public bool Contains(uint ordinal) => Span.BinarySearch(ordinal) >= 0;

    public static DeadOrdinalSet FromOwned(uint[] ordinals)
    {
        for (var i = 1; i < ordinals.Length; i++)
        {
            if (ordinals[i] <= ordinals[i - 1])
            {
                throw new ArgumentException("Dead document ordinals must be strictly increasing.", nameof(ordinals));
            }
        }

        return ordinals.Length == 0 ? Empty : new DeadOrdinalSet(ordinals);
    }

    public Enumerator GetEnumerator() => new(_ordinals ?? Array.Empty<uint>());

    public bool Equals(DeadOrdinalSet other) => Span.SequenceEqual(other.Span);
    public override bool Equals(object? obj) => obj is DeadOrdinalSet other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var ordinal in Span)
        {
            hash.Add(ordinal);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(DeadOrdinalSet left, DeadOrdinalSet right) => left.Equals(right);
    public static bool operator !=(DeadOrdinalSet left, DeadOrdinalSet right) => !left.Equals(right);

    public struct Enumerator
    {
        private readonly uint[] _ordinals;
        private int _index = -1;

        public Enumerator(uint[] ordinals)
        {
            _ordinals = ordinals;
        }

        public readonly uint Current => _ordinals[_index];

        public bool MoveNext()
        {
            _index++;
            return _index < _ordinals.Length;
        }
    }
}

internal readonly record struct SegmentManifestEntry(
    SegmentId SegmentId,
    int DocumentCount,
    long ContentBytes,
    DeadOrdinalSet DeadOrdinals,
    long DeadContentBytes)
{
    public int LiveDocumentCount => DocumentCount - DeadOrdinals.Length;
}

internal sealed record CorpusManifest(
    CorpusId CorpusId,
    ContentRevision ContentRevision,
    StorageGeneration Generation,
    byte[] CommitMetadata,
    IReadOnlyList<SegmentManifestEntry> Segments)
{
    public CorpusManifest(
        CorpusId corpusId,
        StorageGeneration generation,
        IReadOnlyList<SegmentManifestEntry> segments)
        : this(corpusId, new ContentRevision(generation.Value), generation, [], segments)
    {
    }

    public CorpusManifest CopyForPublication()
    {
        return new CorpusManifest(
            CorpusId,
            ContentRevision,
            Generation,
            [.. CommitMetadata],
            [.. Segments]);
    }
}
