using System.Text;
using System.Text.RegularExpressions;
using Lokad.Sift;

namespace Lokad.Sift.Search;

internal sealed class PathFilterMatcher
{
    internal const int CacheCapacity = 256;
    internal const int CacheCharacterCapacity = 256 * 1024;
    private static readonly BoundedLruCache<PathFilterCacheKey, PathFilterMatcher> Cache = new(
        CacheCapacity,
        CacheCharacterCapacity,
        static key => key.CharacterCount);
    private static readonly PathFilterMatcher Empty = new(new PathFilterCacheKey(new PathFilter()));
    private readonly string? _pathPrefix;
    private readonly byte[] _pathPrefixUtf8;
    private readonly GlobMatcher[] _includes;
    private readonly GlobMatcher[] _excludes;
    private readonly Regex? _pathRegex;
    private readonly string[] _accelerationFragments;
    private readonly int[] _pathTrigrams;
    internal static int CacheCount => Cache.Count;
    internal static int CacheCharacters => Cache.Weight;

    public static PathFilterMatcher Prepare(PathFilter filter)
    {
        if (filter.PathPrefix is null &&
            filter.PathRegex is null &&
            filter.IncludeGlobs.Count == 0 &&
            filter.ExcludeGlobs.Count == 0)
        {
            return Empty;
        }

        var key = new PathFilterCacheKey(filter);
        return Cache.GetOrAdd(
            key,
            static cacheKey => new PathFilterMatcher(cacheKey),
            out _);
    }

    private PathFilterMatcher(PathFilterCacheKey filter)
    {
        _pathPrefix = filter.PathPrefix is { } pathPrefix && !string.IsNullOrWhiteSpace(pathPrefix)
            ? pathPrefix.Replace('\\', '/')
            : null;
        _pathPrefixUtf8 = _pathPrefix is null ? [] : System.Text.Encoding.UTF8.GetBytes(_pathPrefix);
        _includes = CompileGlobs(filter.IncludeGlobs);
        _excludes = CompileGlobs(filter.ExcludeGlobs);
        _pathRegex = string.IsNullOrWhiteSpace(filter.PathRegex)
            ? null
            : CreateRegex(filter.PathRegex);
        _accelerationFragments = BuildAccelerationFragments(filter);
        _pathTrigrams = BuildPathTrigrams(_accelerationFragments);

        static int[] BuildPathTrigrams(string[] accelerationFragments)
        {
            if (accelerationFragments.Length == 0)
            {
                return [];
            }

            var grams = new HashSet<int>();
            foreach (var fragment in accelerationFragments)
            {
                if (fragment.Length < 3)
                {
                    continue;
                }

                var bytes = System.Text.Encoding.UTF8.GetBytes(fragment);
                for (var i = 0; i <= bytes.Length - 3; i++)
                {
                    var gram = (bytes[i] << 16) | (bytes[i + 1] << 8) | bytes[i + 2];
                    grams.Add(gram);
                }
            }

            return [.. grams];
        }
    }

    public string? PathPrefix => _pathPrefix;
    public bool UsesPathIndex => _accelerationFragments.Length > 0;
    public bool RequiresPathVerification => _includes.Length > 0 || _excludes.Length > 0 || _pathRegex is not null;
    public bool RequiresStringPathVerification => _pathRegex is not null || HasRegexGlob(_includes) || HasRegexGlob(_excludes);

    public bool IsMatch(string path)
    {
        if (_pathPrefix is not null && !path.StartsWith(_pathPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (_includes.Length > 0)
        {
            var included = false;
            for (var i = 0; i < _includes.Length; i++)
            {
                if (_includes[i].IsMatch(path))
                {
                    included = true;
                    break;
                }
            }

            if (!included)
            {
                return false;
            }
        }

        for (var i = 0; i < _excludes.Length; i++)
        {
            if (_excludes[i].IsMatch(path))
            {
                return false;
            }
        }

        return _pathRegex is null || _pathRegex.IsMatch(path);
    }

    public bool IsMatch(ReadOnlySpan<byte> pathUtf8)
    {
        if (_pathPrefixUtf8.Length > 0 && !pathUtf8.StartsWith(_pathPrefixUtf8))
        {
            return false;
        }

        if (_includes.Length > 0)
        {
            var included = false;
            for (var i = 0; i < _includes.Length; i++)
            {
                if (_includes[i].IsMatch(pathUtf8))
                {
                    included = true;
                    break;
                }
            }

            if (!included)
            {
                return false;
            }
        }

        for (var i = 0; i < _excludes.Length; i++)
        {
            if (_excludes[i].IsMatch(pathUtf8))
            {
                return false;
            }
        }

        return _pathRegex is null;
    }

    public int[] GetPathTrigrams() => _pathTrigrams;

    private static GlobMatcher[] CompileGlobs(IReadOnlyList<string> globs)
    {
        if (globs.Count == 0)
        {
            return Array.Empty<GlobMatcher>();
        }

        var compiled = new List<GlobMatcher>(globs.Count);
        foreach (var glob in globs)
        {
            if (glob.AsSpan().Trim().IsEmpty)
            {
                continue;
            }

            compiled.Add(GlobMatcher.Create(glob));
        }

        return compiled.ToArray();
    }

    internal static Regex CreateRegex(string pattern)
    {
        const RegexOptions baseOptions = RegexOptions.CultureInvariant;
        var timeout = TimeSpan.FromSeconds(1);
        try
        {
            return new Regex(pattern, baseOptions | RegexOptions.NonBacktracking, timeout);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return new Regex(pattern, baseOptions, timeout);
        }
    }

    internal static string GlobToRegex(string glob)
    {
        var builder = new StringBuilder(glob.Length + 2);
        builder.Append('^');
        for (var i = 0; i < glob.Length; i++)
        {
            var current = glob[i];
            if (current == '*')
            {
                var isDouble = i + 1 < glob.Length && glob[i + 1] == '*';
                builder.Append(isDouble ? ".*" : "[^/]*");
                if (isDouble)
                {
                    i++;
                }

                continue;
            }

            if (current == '?')
            {
                builder.Append("[^/]");
            }
            else
            {
                if (current is '\\' or '.' or '$' or '^' or '{' or '[' or '(' or '|' or ')' or '+' or ']')
                {
                    builder.Append('\\');
                }

                builder.Append(current);
            }
        }

        builder.Append('$');
        return builder.ToString();
    }

    private static string[] BuildAccelerationFragments(PathFilterCacheKey filter)
    {
        var fragments = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(filter.PathRegex))
        {
            foreach (var fragment in LiteralFragmentSet.PruneContained(RegexFragmentAnalyzer.ExtractMandatoryFragments(filter.PathRegex)))
            {
                if (fragment.Length >= 3)
                {
                    fragments.Add(fragment);
                }
            }
        }

        if (filter.IncludeGlobs.Count > 0)
        {
            string? onlyIncludeGlob = null;
            for (var i = 0; i < filter.IncludeGlobs.Count; i++)
            {
                if (filter.IncludeGlobs[i].AsSpan().Trim().IsEmpty)
                {
                    continue;
                }

                if (onlyIncludeGlob is not null)
                {
                    // Include globs are alternatives, so neither literal is required.
                    onlyIncludeGlob = null;
                    break;
                }

                onlyIncludeGlob = filter.IncludeGlobs[i];
            }

            // Glob verification canonicalizes separators; acceleration must index the same form.
            var fragment = ExtractGlobLiteral(
                (onlyIncludeGlob ?? string.Empty).Replace('\\', '/'));
            if (fragment.Length >= 3)
            {
                fragments.Add(fragment);
            }
        }

        return [.. fragments.Distinct(StringComparer.Ordinal)];
    }

    private static string ExtractGlobLiteral(string glob)
    {
        if (glob.AsSpan().Trim().IsEmpty)
        {
            return string.Empty;
        }

        var bestStart = 0;
        var bestLength = 0;
        var currentStart = 0;
        var currentLength = 0;
        for (var i = 0; i < glob.Length; i++)
        {
            var current = glob[i];
            if (current is '*' or '?')
            {
                if (currentLength > bestLength)
                {
                    bestStart = currentStart;
                    bestLength = currentLength;
                }

                currentStart = i + 1;
                currentLength = 0;
                continue;
            }

            currentLength++;
        }

        if (currentLength > bestLength)
        {
            bestStart = currentStart;
            bestLength = currentLength;
        }

        return bestLength == 0 ? string.Empty : glob.Substring(bestStart, bestLength);
    }

    private static bool HasRegexGlob(GlobMatcher[] matchers)
    {
        for (var i = 0; i < matchers.Length; i++)
        {
            if (matchers[i].Kind == GlobMatcherKind.Regex)
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class GlobMatcher
{
    private readonly string _value;
    private readonly string _suffix;
    private readonly byte[] _valueUtf8;
    private readonly byte[] _suffixUtf8;
    private readonly Regex? _regex;
    private readonly GlobMatcherKind _kind;

    private GlobMatcher(string value, string suffix, Regex? regex, GlobMatcherKind kind)
    {
        _value = value;
        _suffix = suffix;
        _valueUtf8 = value.Length == 0 ? [] : System.Text.Encoding.UTF8.GetBytes(value);
        _suffixUtf8 = suffix.Length == 0 ? [] : System.Text.Encoding.UTF8.GetBytes(suffix);
        _regex = regex;
        _kind = kind;
    }

    public GlobMatcherKind Kind => _kind;

    public static GlobMatcher Create(string glob)
    {
        glob = glob.Replace('\\', '/');
        if (glob.IndexOf('?') >= 0)
        {
            return CreateRegex(glob);
        }

        if (glob.IndexOf('*') < 0)
        {
            return new GlobMatcher(glob, string.Empty, null, GlobMatcherKind.Exact);
        }

        if (glob.EndsWith("/**", StringComparison.Ordinal) && glob.IndexOf('*') == glob.Length - 2)
        {
            return new GlobMatcher(glob[..^2], string.Empty, null, GlobMatcherKind.PrefixRecursive);
        }

        var recursiveIndex = glob.IndexOf("**/", StringComparison.Ordinal);
        if (recursiveIndex >= 0 && glob.LastIndexOf('*') == recursiveIndex + 1)
        {
            return new GlobMatcher(glob[..recursiveIndex], glob[(recursiveIndex + 3)..], null, GlobMatcherKind.RecursiveSuffix);
        }

        var starIndex = glob.IndexOf('*');
        if (starIndex >= 0 && starIndex == glob.LastIndexOf('*'))
        {
            return new GlobMatcher(glob[..starIndex], glob[(starIndex + 1)..], null, GlobMatcherKind.SingleSegmentWildcard);
        }

        return CreateRegex(glob);
    }

    public bool IsMatch(string path)
    {
        return _kind switch
        {
            GlobMatcherKind.Exact => string.Equals(path, _value, StringComparison.Ordinal),
            GlobMatcherKind.PrefixRecursive => path.StartsWith(_value, StringComparison.Ordinal),
            GlobMatcherKind.SingleSegmentWildcard => MatchSingleSegmentWildcard(path),
            GlobMatcherKind.RecursiveSuffix => MatchRecursiveSuffix(path),
            GlobMatcherKind.Regex => (_regex
                ?? throw new InvalidOperationException("A regex glob must have a compiled expression.")).IsMatch(path),
            _ => throw new InvalidOperationException($"Unsupported glob matcher kind '{_kind}'.")
        };
    }

    public bool IsMatch(ReadOnlySpan<byte> pathUtf8)
    {
        return _kind switch
        {
            GlobMatcherKind.Exact => pathUtf8.SequenceEqual(_valueUtf8),
            GlobMatcherKind.PrefixRecursive => pathUtf8.StartsWith(_valueUtf8),
            GlobMatcherKind.SingleSegmentWildcard => MatchSingleSegmentWildcard(pathUtf8),
            GlobMatcherKind.RecursiveSuffix => MatchRecursiveSuffix(pathUtf8),
            _ => false
        };
    }

    private bool MatchSingleSegmentWildcard(string path)
    {
        if (!path.StartsWith(_value, StringComparison.Ordinal) || !path.EndsWith(_suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var middleLength = path.Length - _value.Length - _suffix.Length;
        return middleLength >= 0 && path.AsSpan(_value.Length, middleLength).IndexOf('/') < 0;
    }

    private bool MatchSingleSegmentWildcard(ReadOnlySpan<byte> pathUtf8)
    {
        if (!pathUtf8.StartsWith(_valueUtf8) || !pathUtf8.EndsWith(_suffixUtf8))
        {
            return false;
        }

        var middleLength = pathUtf8.Length - _valueUtf8.Length - _suffixUtf8.Length;
        return middleLength >= 0 && pathUtf8.Slice(_valueUtf8.Length, middleLength).IndexOf((byte)'/') < 0;
    }

    private bool MatchRecursiveSuffix(string path)
    {
        if (!path.StartsWith(_value, StringComparison.Ordinal) || !path.EndsWith(_suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var middleLength = path.Length - _value.Length - _suffix.Length;
        return middleLength > 0 && path.AsSpan(_value.Length, middleLength).IndexOf('/') >= 0;
    }

    private bool MatchRecursiveSuffix(ReadOnlySpan<byte> pathUtf8)
    {
        if (!pathUtf8.StartsWith(_valueUtf8) || !pathUtf8.EndsWith(_suffixUtf8))
        {
            return false;
        }

        var middleLength = pathUtf8.Length - _valueUtf8.Length - _suffixUtf8.Length;
        return middleLength > 0 && pathUtf8.Slice(_valueUtf8.Length, middleLength).IndexOf((byte)'/') >= 0;
    }

    private static GlobMatcher CreateRegex(string glob)
    {
        return new GlobMatcher(
            string.Empty,
            string.Empty,
            PathFilterMatcher.CreateRegex(PathFilterMatcher.GlobToRegex(glob)),
            GlobMatcherKind.Regex);
    }

}

internal enum GlobMatcherKind
{
    Exact,
    PrefixRecursive,
    SingleSegmentWildcard,
    RecursiveSuffix,
    Regex
}

internal sealed class PathFilterCacheKey : IEquatable<PathFilterCacheKey>
{
    private readonly string? _pathPrefix;
    private readonly string? _pathRegex;
    private readonly IReadOnlyList<string> _includeGlobs;
    private readonly IReadOnlyList<string> _excludeGlobs;
    private readonly int _hashCode;

    public PathFilterCacheKey(PathFilter filter)
    {
        _pathPrefix = filter.PathPrefix;
        _pathRegex = filter.PathRegex;
        _includeGlobs = filter.IncludeGlobs;
        _excludeGlobs = filter.ExcludeGlobs;

        var hash = new HashCode();
        hash.Add(_pathPrefix, StringComparer.Ordinal);
        hash.Add(_pathRegex, StringComparer.Ordinal);
        AddPatterns(ref hash, _includeGlobs);
        AddPatterns(ref hash, _excludeGlobs);
        _hashCode = hash.ToHashCode();
        CharacterCount = (_pathPrefix?.Length ?? 0) +
            (_pathRegex?.Length ?? 0) +
            GetCharacterCount(_includeGlobs) +
            GetCharacterCount(_excludeGlobs);
    }

    public int CharacterCount { get; }
    public string? PathPrefix => _pathPrefix;
    public string? PathRegex => _pathRegex;
    public IReadOnlyList<string> IncludeGlobs => _includeGlobs;
    public IReadOnlyList<string> ExcludeGlobs => _excludeGlobs;

    public bool Equals(PathFilterCacheKey? other)
    {
        return other is not null &&
            string.Equals(_pathPrefix, other._pathPrefix, StringComparison.Ordinal) &&
            string.Equals(_pathRegex, other._pathRegex, StringComparison.Ordinal) &&
            SequenceEqual(_includeGlobs, other._includeGlobs) &&
            SequenceEqual(_excludeGlobs, other._excludeGlobs);
    }

    public override bool Equals(object? obj)
    {
        return obj is PathFilterCacheKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        return _hashCode;
    }

    private static void AddPatterns(ref HashCode hash, IReadOnlyList<string> patterns)
    {
        hash.Add(patterns.Count);
        for (var i = 0; i < patterns.Count; i++)
        {
            hash.Add(patterns[i], StringComparer.Ordinal);
        }
    }

    private static int GetCharacterCount(IReadOnlyList<string> patterns)
    {
        var count = 0;
        for (var i = 0; i < patterns.Count; i++)
        {
            count = checked(count + patterns[i].Length);
        }

        return count;
    }

    private static bool SequenceEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
