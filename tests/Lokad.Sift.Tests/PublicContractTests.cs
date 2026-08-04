using System.Reflection;

namespace Lokad.Sift.Tests;

public sealed class PublicContractTests
{
    [Fact]
    public void StorageAndCodecImplementationTypesAreNotExported()
    {
        var exportedTypes = typeof(Sift).Assembly.GetExportedTypes();

        Assert.DoesNotContain(exportedTypes, static type =>
            type.Namespace is "Lokad.Sift.Storage" or "Lokad.Sift.LowLevel");
    }

    [Fact]
    public void InterfacesContainOnlyCanonicalOperationOverloads()
    {
        Assert.Equal(2, CountMethods<ISift>(nameof(ISift.Build)));
        Assert.Equal(4, CountMethods<ISift>(nameof(ISift.Update)));
        Assert.Single(typeof(ISift).GetMethods(), static method => method.Name == nameof(ISift.Validate));
        Assert.Single(typeof(ISift).GetMethods(), static method => method.Name == nameof(ISift.Compact));
        Assert.Single(typeof(IIndexSnapshot).GetMethods(), static method => method.Name == nameof(IIndexSnapshot.Search));
        Assert.Equal(2, CountMethods<IOverlaySnapshot>(nameof(IOverlaySnapshot.Apply)));
        Assert.Single(typeof(IOverlaySnapshot).GetMethods(), static method => method.Name == nameof(IOverlaySnapshot.ApplyTo));
    }

    [Fact]
    public void RequiredInputContractsCannotBeDefaultValueTypes()
    {
        Assert.False(typeof(SearchQuery).IsValueType);
        Assert.False(typeof(DocumentKey).IsValueType);
        Assert.False(typeof(SubmittedDocument).IsValueType);
        Assert.Throws<ArgumentNullException>(() => new SearchQuery(null!));
        Assert.Throws<ArgumentNullException>(() => new DocumentKey(null!));
        Assert.Throws<ArgumentNullException>(() => new SubmittedDocument(null!, ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void LargeOperationResultsAreReferenceTypes()
    {
        Assert.False(typeof(SearchPlan).IsValueType);
        Assert.False(typeof(SearchStats).IsValueType);
        Assert.False(typeof(BuildStats).IsValueType);
        Assert.False(typeof(UpdateStats).IsValueType);
        Assert.False(typeof(CompactStats).IsValueType);
        Assert.False(typeof(MaintenanceStats).IsValueType);
        Assert.False(typeof(StorageStats).IsValueType);
    }

    private static int CountMethods<T>(string name)
    {
        return typeof(T).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Count(method => method.Name == name);
    }
}
