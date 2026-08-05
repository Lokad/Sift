using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Lokad.Sift.Search;

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

internal sealed class GuardedHitCollector(IHitCollector inner) : IHitCollector
{
    public bool OnHit(in RawHit hit)
    {
        try
        {
            return inner.OnHit(hit);
        }
        catch (Exception exception)
        {
            throw new CollectorFailureException(ExceptionDispatchInfo.Capture(exception));
        }
    }
}

internal sealed class HitOwnerCollector(object owner, IHitCollector inner) : IHitCollector
{
    public bool OnHit(in RawHit hit)
    {
        var ownedHit = hit.WithOwner(owner);
        return inner.OnHit(ownedHit);
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
