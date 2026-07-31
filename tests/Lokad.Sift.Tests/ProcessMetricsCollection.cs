namespace Lokad.Sift.Tests;

// Process-wide allocation and mapping counters cannot be asserted while another test mutates them.
[CollectionDefinition("ProcessMetrics", DisableParallelization = true)]
public sealed class ProcessMetricsCollection
{
}
