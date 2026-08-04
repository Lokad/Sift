using Lokad.Sift;

namespace Lokad.Sift.Tests;

[Collection("BenchmarkSmoke")]
public sealed class IndexingWorkspaceTests
{
    [Fact]
    public async Task MutationReleasesNativeIndexingWorkspaceByDefault()
    {
        Assert.Equal(0, SiftProcessMetrics.NativeIndexWorkspaceBytes);
        using var index = SiftIndex.CreateInMemory();

        var build = await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha beta gamma")]));

        Assert.Equal(0, build.PeakManagedBytes);
        Assert.Equal(0, SiftProcessMetrics.NativeIndexWorkspaceBytes);
    }

    [Fact]
    public async Task RetainedWorkspaceIsReleasedWhenOptedInIndexIsDisposed()
    {
        Assert.Equal(0, SiftProcessMetrics.NativeIndexWorkspaceBytes);
        var index = SiftIndex.CreateInMemory(new SiftOptions { RetainIndexingWorkspace = true });
        try
        {
            await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha beta gamma")]));
            Assert.True(SiftProcessMetrics.NativeIndexWorkspaceBytes >= 128L * 1024 * 1024);

            index.Dispose();
            Assert.Equal(0, SiftProcessMetrics.NativeIndexWorkspaceBytes);
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
