using System.Text;
using Lokad.Sift;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Tests;

internal static class TestHelpers
{
    public static CorpusStorage GetStorage(SiftIndex index) => (CorpusStorage)index.Storage;

    public static IEnumerable<(string Path, int Line, int Column, string MatchText)> FindAll(string path, string content, string pattern, StringComparison comparison)
    {
        var index = 0;
        while (true)
        {
            index = content.IndexOf(pattern, index, comparison);
            if (index < 0)
            {
                yield break;
            }

            var line = 1;
            var lineStart = 0;
            for (var i = 0; i < index; i++)
            {
                if (content[i] == '\n')
                {
                    line++;
                    lineStart = i + 1;
                }
            }

            yield return (path, line, index - lineStart + 1, content.Substring(index, pattern.Length));
            index++;
        }
    }

    public static int EncodeGram(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var value = 0;
        foreach (var current in bytes)
        {
            value = (value << 8) | current;
        }

        return value;
    }
}

internal sealed class HitBuffer : IHitCollector
{
    public List<RawHit> Hits { get; } = [];

    public bool OnHit(in RawHit hit)
    {
        Hits.Add(hit);
        return true;
    }
}

internal sealed class CorpusFixture : IDisposable
{
    private readonly string _root;

    public CorpusFixture(params (string Path, string Content)[] documents)
    {
        _root = Path.Combine(Path.GetTempPath(), "sift-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        CorpusPath = Path.Combine(_root, "corpus");
        Documents = documents;
    }

    public string CorpusPath { get; }
    public IReadOnlyList<(string Path, string Content)> Documents { get; }

    public IDocumentSource CreateSource() => new InMemoryDocumentSource(Documents);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

internal sealed class InMemoryDocumentSource : IDocumentSource
{
    private readonly IReadOnlyList<(string Path, string Content)> _documents;
    private int _index;

    public InMemoryDocumentSource(IReadOnlyList<(string Path, string Content)> documents)
    {
        _documents = documents;
    }

    public bool TryReadNext(out SubmittedDocument document)
    {
        if (_index >= _documents.Count)
        {
            document = default;
            return false;
        }

        var next = _documents[_index++];
        document = new SubmittedDocument(next.Path, Encoding.UTF8.GetBytes(next.Content));
        return true;
    }
}




