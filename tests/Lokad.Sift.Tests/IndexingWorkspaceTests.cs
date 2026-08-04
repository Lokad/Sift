using Lokad.Sift;

namespace Lokad.Sift.Tests;

[Collection("BenchmarkSmoke")]
public sealed class IndexingWorkspaceTests
{
    [Fact]
    public async Task MutationReleasesNativeIndexingWorkspaceByDefault()
    {
        Assert.Equal(0, IndexingProcessMetrics.NativeWorkspaceBytes);
        using var index = SiftIndex.CreateInMemory();

        var build = await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha beta gamma")]));

        Assert.Equal(0, build.PeakManagedBytes);
        Assert.Equal(0, IndexingProcessMetrics.NativeWorkspaceBytes);
    }

    [Fact]
    public async Task RetainedWorkspaceIsReleasedWhenOptedInIndexIsDisposed()
    {
        Assert.Equal(0, IndexingProcessMetrics.NativeWorkspaceBytes);
        var index = SiftIndex.CreateInMemory(new SiftOptions { RetainIndexingWorkspace = true });
        try
        {
            await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha beta gamma")]));
            Assert.True(IndexingProcessMetrics.NativeWorkspaceBytes >= 128L * 1024 * 1024);

            index.Dispose();
            Assert.Equal(0, IndexingProcessMetrics.NativeWorkspaceBytes);
        }
        finally
        {
            index.Dispose();
        }
    }

    [Fact]
    public async Task ManagedMemoryMeasurementIsOptIn()
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions { MeasurePeakManagedMemory = true });

        var build = await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha beta gamma")]));

        Assert.True(build.PeakManagedBytes > 0);
    }
}
