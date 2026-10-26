namespace Lokad.Sift;

internal static class ReentrancyGuard
{
    private static readonly AsyncLocal<Scope?> Current = new();

    public static Scope Enter(object owner)
    {
        var scope = new Scope(owner, Current.Value);
        Current.Value = scope;
        return scope;
    }

    public static bool IsActive(object owner)
    {
        for (var scope = Current.Value; scope is not null; scope = scope.Parent)
        {
            if (scope.Owner is { } activeOwner && ReferenceEquals(activeOwner, owner))
            {
                return true;
            }
        }

        return false;
    }

    internal sealed class Scope(object owner, Scope? parent) : IDisposable
    {
        private object? _owner = owner;

        internal object? Owner => Volatile.Read(ref _owner);
        internal Scope? Parent { get; } = parent;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is null)
            {
                return;
            }

            // Child execution contexts can retain this node after the owning operation exits.
            // Clearing ownership both deactivates the guard and stops inherited
            // contexts from keeping a completed operation's index/snapshot alive.
            if (ReferenceEquals(Current.Value, this))
            {
                Current.Value = Parent;
            }
        }
    }
}
