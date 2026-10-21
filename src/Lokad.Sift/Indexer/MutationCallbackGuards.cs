using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Lokad.Sift.Indexer;

// Carry caller failures past the mutation's storage-corruption handler. The
// public mutation entry point rethrows the original exception and stack.
internal sealed class MutationCallbackFailureException(Exception exception) : Exception
{
    public ExceptionDispatchInfo DispatchInfo { get; } = ExceptionDispatchInfo.Capture(exception);
}

internal static class MutationCallbackGuards
{
    public static bool TryReadNext(IDocumentSource source, out SubmittedDocument document)
    {
        try { return source.TryReadNext(out document); }
        catch (Exception exception) when (IndexCorruption.IsIndexCorruption(exception))
        {
            throw new MutationCallbackFailureException(exception);
        }
    }

    public static byte[] GetCommitMetadata<TStats>(Func<TStats, ReadOnlyMemory<byte>> provider, TStats stats)
    {
        try { return provider(stats).ToArray(); }
        catch (Exception exception) when (IndexCorruption.IsIndexCorruption(exception))
        {
            throw new MutationCallbackFailureException(exception);
        }
    }

    public static async IAsyncEnumerable<SubmittedDocument> ReadAsync(
        IAsyncEnumerable<SubmittedDocument> source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IAsyncEnumerator<SubmittedDocument> enumerator;
        try { enumerator = source.GetAsyncEnumerator(cancellationToken); }
        catch (Exception exception) when (IndexCorruption.IsIndexCorruption(exception))
        {
            throw new MutationCallbackFailureException(exception);
        }

        try
        {
            while (true)
            {
                bool hasDocument;
                SubmittedDocument document;
                try
                {
                    hasDocument = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    document = hasDocument ? enumerator.Current : default;
                }
                catch (Exception exception) when (IndexCorruption.IsIndexCorruption(exception))
                {
                    throw new MutationCallbackFailureException(exception);
                }

                if (!hasDocument) yield break;
                yield return document;
            }
        }
        finally
        {
            try { await enumerator.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) when (IndexCorruption.IsIndexCorruption(exception))
            {
                throw new MutationCallbackFailureException(exception);
            }
        }
    }
}
