using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Lokad.Sift.Search;

internal sealed class OrderedHitCollector(Func<RawHit, bool> consume) : IHitCollector
{
    public bool OnHit(in RawHit hit) => consume(hit);
}

internal sealed class BoundedHitCollector(
    BlockingCollection<RawHit> hits,
    CancellationToken cancellationToken) : IHitCollector
{
    public bool OnHit(in RawHit hit)
    {
        try
        {
            hits.Add(hit, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}

internal sealed class GuardedOwnedHitCollector(
    object owner, IHitCollector inner, ValueStopwatch watch, long maxElapsedMilliseconds) : IHitCollector
{
    public bool DeadlineExceeded { get; private set; }

    public bool OnHit(in RawHit hit)
    {
        try
        {
            if (!inner.OnHit(hit.WithOwner(owner))) return false;
            // Cancellation timers can be delayed by thread-pool load. Enforce the
            // same absolute deadline before accepting another hit in every route.
            DeadlineExceeded = watch.ElapsedMilliseconds >= maxElapsedMilliseconds;
            return !DeadlineExceeded;
        }
        catch (Exception exception)
        {
            throw new CollectorFailureException(ExceptionDispatchInfo.Capture(exception));
        }
    }
}

internal sealed class GuardedDocumentKeyCollector(IDocumentKeyCollector inner) : IDocumentKeyCollector
{
    public bool OnDocument(DocumentKey key)
    {
        try
        {
            return inner.OnDocument(key);
        }
        catch (Exception exception)
        {
            throw new CollectorFailureException(ExceptionDispatchInfo.Capture(exception));
        }
    }
}

internal sealed class CollectorFailureException(ExceptionDispatchInfo dispatchInfo) : Exception
{
    public ExceptionDispatchInfo DispatchInfo { get; } = dispatchInfo;
}
