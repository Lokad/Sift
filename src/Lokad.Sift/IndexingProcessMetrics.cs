using Lokad.Sift.Storage;

namespace Lokad.Sift;

public static class IndexingProcessMetrics
{
    // Native fixed-domain workspaces currently retained by mutations or opted-in Sift instances.
    public static long NativeWorkspaceBytes => DirectIndexedGramBuilder.CurrentNativeWorkspaceBytes;
}
