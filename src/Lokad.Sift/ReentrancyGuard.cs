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
            if (scope.IsActive && ReferenceEquals(scope.Owner, owner))
            {
                return true;
            }
        }

        return false;
    }

    internal sealed class Scope(object owner, Scope? parent) : IDisposable
    {
        private int _active = 1;

        internal object Owner { get; } = owner;
        internal Scope? Parent { get; } = parent;
        internal bool IsActive => Volatile.Read(ref _active) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _active, 0) == 0)
            {
                return;
            }

            // Child execution contexts can retain this node after the owning operation exits.
            // Marking it inactive keeps those inherited stacks from reporting stale ownership.
            if (ReferenceEquals(Current.Value, this))
            {
                Current.Value = Parent;
            }
        }
    }
}
