using System.Reflection;

namespace Lokad.Sift.Tests;

public sealed class PublicContractTests
{
    [Fact]
    public void StorageAndCodecImplementationTypesAreNotExported()
    {
        var exportedTypes = typeof(SiftIndex).Assembly.GetExportedTypes();

        Assert.DoesNotContain(exportedTypes, static type =>
            type.Namespace is "Lokad.Sift.Storage" or "Lokad.Sift.LowLevel");
    }

    [Fact]
    public void InterfacesContainOnlyCanonicalOperationOverloads()
    {
        Assert.Equal(2, CountMethods<ISiftIndex>(nameof(ISiftIndex.Build)));
        Assert.Equal(4, CountMethods<ISiftIndex>(nameof(ISiftIndex.Update)));
        Assert.Single(typeof(ISiftIndex).GetMethods(), static method => method.Name == nameof(ISiftIndex.Validate));
        Assert.Single(typeof(ISiftIndex).GetMethods(), static method => method.Name == nameof(ISiftIndex.Compact));
        Assert.Single(typeof(IIndexSnapshot).GetMethods(), static method => method.Name == nameof(IIndexSnapshot.Search));
        Assert.Equal(2, CountMethods<IOverlaySnapshot>(nameof(IOverlaySnapshot.Update)));
        Assert.Single(typeof(IOverlaySnapshot).GetMethods(), static method => method.Name == nameof(IOverlaySnapshot.CommitToBase));
        Assert.Single(typeof(IOverlaySnapshot).GetMethods(), static method => method.Name == nameof(IOverlaySnapshot.ApplyDeltaTo));
    }

    [Fact]
    public void PerDocumentContractsAreValidatedValueTypes()
    {
        Assert.False(typeof(SearchQuery).IsValueType);
        Assert.True(typeof(DocumentKey).IsValueType);
        Assert.True(typeof(SubmittedDocument).IsValueType);
        Assert.Throws<InvalidOperationException>(() => _ = default(DocumentKey).Path);
        Assert.Throws<InvalidOperationException>(() => _ = default(SubmittedDocument).Path);
        Assert.Throws<InvalidOperationException>(() => _ = default(RejectedDocument).Path);
        Assert.Empty(typeof(RejectedDocument).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(typeof(RejectedUpsertException).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void IdentityAndHitCoordinatesUseDomainTypes()
    {
        Assert.Equal(typeof(CorpusId), GetPropertyType<IndexRevision>(nameof(IndexRevision.CorpusId)));
        Assert.Equal(
            typeof(ContentRevision),
            GetPropertyType<IndexRevision>(nameof(IndexRevision.ContentRevision)));
        Assert.Equal(typeof(CorpusId), GetPropertyType<IndexIdentity>(nameof(IndexIdentity.CorpusId)));
        Assert.Equal(
            typeof(ContentRevision),
            GetPropertyType<IndexIdentity>(nameof(IndexIdentity.ContentRevision)));
        Assert.Equal(
            typeof(StorageGeneration),
            GetPropertyType<IndexIdentity>(nameof(IndexIdentity.StorageGeneration)));
        Assert.Equal(typeof(SegmentId), GetPropertyType<RawHit>(nameof(RawHit.SegmentId)));
        Assert.Equal(
            typeof(DocumentOrdinal),
            GetPropertyType<RawHit>(nameof(RawHit.DocumentOrdinal)));
        Assert.Empty(typeof(RawHit).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(typeof(WriteActivityStats).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void QueryPatternIsConstructorOwned()
    {
        var pattern = typeof(SearchQuery).GetProperty(nameof(SearchQuery.Pattern));

        Assert.NotNull(pattern);
        Assert.False(pattern.CanWrite);
        Assert.Equal(2, typeof(SearchQuery).GetConstructors().Length);
        Assert.Single(typeof(CompactOptions).GetConstructors());
    }

    [Fact]
    public void ProvisionalMutationStatsDoNotExposeCommittedState()
    {
        Assert.Null(typeof(BuildCommitStats).GetProperty(nameof(BuildStats.CommittedIdentity)));
        Assert.Null(typeof(UpdateCommitStats).GetProperty(nameof(UpdateStats.CommittedIdentity)));
        Assert.Null(typeof(OverlayUpdateStats).GetProperty(nameof(UpdateStats.CommittedIdentity)));
        Assert.Equal(
            typeof(Func<BuildCommitStats, ReadOnlyMemory<byte>>),
            GetPropertyType<BuildOptions>(nameof(BuildOptions.CommitMetadataProvider)));
        Assert.Equal(
            typeof(Func<UpdateCommitStats, ReadOnlyMemory<byte>>),
            GetPropertyType<UpdateOptions>(nameof(UpdateOptions.CommitMetadataProvider)));
    }

    [Fact]
    public void PathFilterOwnsImmutablePatternCollections()
    {
        var includes = new[] { "src/**" };
        var filter = new PathFilter { IncludeGlobs = includes };
        includes[0] = "test/**";

        Assert.False(typeof(PathFilter).IsValueType);
        Assert.Equal("src/**", filter.IncludeGlobs[0]);
        var mutableView = Assert.IsAssignableFrom<IList<string>>(filter.IncludeGlobs);
        Assert.Throws<NotSupportedException>(() => mutableView[0] = "other/**");
        Assert.Empty(new PathFilter().ExcludeGlobs);
    }

    [Fact]
    public void PublicEntryPointsRejectNullArguments()
    {
        Assert.Equal("path", Assert.Throws<ArgumentNullException>(() => new DocumentKey(null!)).ParamName);
        Assert.Equal("pattern", Assert.Throws<ArgumentNullException>(() => new SearchQuery(null!)).ParamName);
        Assert.Equal(
            "value",
            Assert.Throws<ArgumentNullException>(() => new PathFilter { IncludeGlobs = null! }).ParamName);
        Assert.Equal(
            "options",
            Assert.Throws<ArgumentNullException>(() => SiftIndex.CreateInMemory(null!)).ParamName);

        using var index = SiftIndex.CreateInMemory();
        Assert.Equal(
            "documents",
            Assert.Throws<ArgumentNullException>(() =>
            {
                _ = index.Build((IDocumentSource)null!, new BuildOptions(), CancellationToken.None);
            }).ParamName);
        Assert.Equal(
            "options",
            Assert.Throws<ArgumentNullException>(() =>
            {
                _ = index.Build(new InMemoryDocumentSource([]), null!, CancellationToken.None);
            }).ParamName);
        using var snapshot = index.OpenSnapshot();
        Assert.Equal(
            "query",
            Assert.Throws<ArgumentNullException>(() => snapshot.Explain(null!, new PathFilter())).ParamName);
        Assert.Equal(
            "pathFilter",
            Assert.Throws<ArgumentNullException>(() =>
                snapshot.Explain(new SearchQuery("text"), null!)).ParamName);
        Assert.Equal(
            "collector",
            Assert.Throws<ArgumentNullException>(() =>
                snapshot.Search(new SearchQuery("text"), new PathFilter(), null!)).ParamName);
    }

    [Fact]
    public void LargeOperationResultsAreReferenceTypes()
    {
        Assert.False(typeof(SearchPlan).IsValueType);
        Assert.False(typeof(SearchStats).IsValueType);
        Assert.False(typeof(BuildStats).IsValueType);
        Assert.False(typeof(BuildCommitStats).IsValueType);
        Assert.False(typeof(UpdateStats).IsValueType);
        Assert.False(typeof(UpdateCommitStats).IsValueType);
        Assert.False(typeof(OverlayUpdateStats).IsValueType);
        Assert.False(typeof(CompactStats).IsValueType);
        Assert.False(typeof(MaintenanceStats).IsValueType);
        Assert.False(typeof(StorageStats).IsValueType);
        Assert.False(typeof(IndexValidationStats).IsValueType);
        Assert.False(typeof(IndexState).IsValueType);
        Assert.False(typeof(MaterializedHit).IsValueType);
    }

    [Fact]
    public void OperationResultsAreOutputOnly()
    {
        Type[] resultTypes =
        [
            typeof(SearchPlan),
            typeof(SearchStats),
            typeof(BuildStats),
            typeof(BuildCommitStats),
            typeof(UpdateStats),
            typeof(UpdateCommitStats),
            typeof(OverlayUpdateStats),
            typeof(CompactStats),
            typeof(MaintenanceStats),
            typeof(StorageStats),
            typeof(IndexValidationStats),
            typeof(IndexState),
            typeof(MaterializedHit)
        ];

        foreach (var resultType in resultTypes)
        {
            Assert.Empty(resultType.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
            Assert.DoesNotContain(
                resultType.GetProperties(BindingFlags.Public | BindingFlags.Instance),
                static property => property.CanWrite);
        }
    }

    [Fact]
    public void PublicApiHasNoOptionalParameters()
    {
        var optionalParameters = typeof(SiftIndex).Assembly.GetExportedTypes()
            .SelectMany(static type => type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Cast<MethodBase>()
                .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)))
            .SelectMany(static method => method.GetParameters())
            .Where(static parameter => parameter.IsOptional || parameter.HasDefaultValue)
            .ToArray();

        Assert.Empty(optionalParameters);
    }

    [Fact]
    public void RejectedUpsertDetailsAreFrozen()
    {
        var source = new[]
        {
            new RejectedDocument("src/a.txt", DocumentRejectionReason.BinaryContent, 10)
        };
        var exception = new RejectedUpsertException(1, 10, source);
        source[0] = new RejectedDocument("src/b.txt", DocumentRejectionReason.InvalidUtf8, 20);

        Assert.Equal("src/a.txt", exception.RejectedDocuments[0].Path);
        var mutableView = Assert.IsAssignableFrom<IList<RejectedDocument>>(exception.RejectedDocuments);
        Assert.Throws<NotSupportedException>(() =>
            mutableView[0] = new RejectedDocument(
                "src/c.txt",
                DocumentRejectionReason.MaximumDocumentBytesExceeded,
                30));
    }

    private static int CountMethods<T>(string name)
    {
        return typeof(T).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Count(method => method.Name == name);
    }

    private static Type GetPropertyType<T>(string name)
    {
        var property = typeof(T).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        return property is null
            ? throw new InvalidOperationException($"Property '{name}' was not found on {typeof(T)}.")
            : property.PropertyType;
    }
}
