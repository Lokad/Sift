using Lokad.Sift.Storage;

namespace Lokad.Sift;

public static class IndexingProcessMetrics
{
    // Native fixed-domain workspaces currently retained by active Sift instances.
    public static long NativeWorkspaceBytes => DirectIndexedGramBuilder.CurrentNativeWorkspaceBytes;
}
