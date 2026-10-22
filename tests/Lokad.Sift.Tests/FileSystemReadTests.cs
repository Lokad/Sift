namespace Lokad.Sift.Tests;

public sealed class FileSystemReadTests
{
    [Theory]
    [InlineData("", 0, "")]
    [InlineData("abc", 3, "abc")]
    [InlineData("abcd", 4, "abcd")]
    [InlineData("ab", 4, "ab")]
    [InlineData("abcd", 1, "abcd")]
    [InlineData("abcdefg", 1, "abcde")]
    public void ReadsPartialShrinkingAndGrowingFilesWithinTheBound(string actual, long reportedLength, string expected)
    {
        using var source = new FileSystemDocumentSource(".", ".sift", 4);
        using var stream = new ChangingStream(actual, reportedLength);
        var bytes = source.ReadContent(stream, out var observed);
        Assert.Equal(reportedLength, observed);
        Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(bytes.Span));
        Assert.InRange(stream.BytesRead, 0, 5);
        Assert.InRange(stream.MaximumReadRequest, 0, 5);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(2147483648L)]
    [InlineData(long.MaxValue)]
    public void KnownOversizedFilesDoNotReadTheirBodies(long length)
    {
        using var source = new FileSystemDocumentSource(".", ".sift", 4);
        using var stream = new ChangingStream("", length);
        var content = source.ReadContent(stream, out var observed);
        Assert.Equal(length, observed);
        Assert.Equal(5, content.Length);
        Assert.Equal(0, stream.BytesRead);
        Assert.Equal(0, stream.ReadCalls);
    }

    private sealed class ChangingStream(string actual, long reportedLength)
        : MemoryStream(System.Text.Encoding.UTF8.GetBytes(actual))
    {
        public int BytesRead { get; private set; }
        public int ReadCalls { get; private set; }
        public int MaximumReadRequest { get; private set; }
        public override long Length => reportedLength;
        public override int Read(Span<byte> buffer)
        {
            ReadCalls++;
            MaximumReadRequest = Math.Max(MaximumReadRequest, buffer.Length);
            var count = base.Read(buffer[..Math.Min(buffer.Length, 2)]);
            BytesRead += count;
            return count;
        }
    }
}
