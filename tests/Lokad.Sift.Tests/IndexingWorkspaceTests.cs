using Lokad.Sift;

namespace Lokad.Sift.Tests;

[Collection("BenchmarkSmoke")]
public sealed class IndexingWorkspaceTests
{
    [Fact]
    public async Task LastSiftDisposeReleasesNativeIndexingWorkspaces()
    {
        Assert.Equal(0, IndexingProcessMetrics.NativeWorkspaceBytes);
        var first = SiftIndex.CreateInMemory();
        var second = SiftIndex.CreateInMemory();
        try
        {
            await first.Build(new InMemoryDocumentSource([("src/a.txt", "alpha beta gamma")]));
            Assert.True(IndexingProcessMetrics.NativeWorkspaceBytes >= 128L * 1024 * 1024);

            first.Dispose();
            Assert.True(IndexingProcessMetrics.NativeWorkspaceBytes >= 128L * 1024 * 1024);

            second.Dispose();
            Assert.Equal(0, IndexingProcessMetrics.NativeWorkspaceBytes);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }
    }
}
