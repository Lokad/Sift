using Lokad.Sift;

if (args.Length == 4 && args[0] == "manifest-observer")
{
    await ManifestPublicationChecks.Observe(args[1], args[2], args[3]);
    return 0;
}

if (args.Length == 3 && args[0] == "lifetime-checks")
{
    return await LifetimeChecks.Run(args[1], args[2]);
}

if (args.Length == 1 && args[0] == "parallel-queue-deadline")
{
    return await ParallelQueueDeadlineChecks.Run();
}

if (args.Length == 1 && args[0] == "parallel-progress")
{
    return await ParallelProgressChecks.Run();
}

if (args.Length == 1 && args[0] == "deadline-probes")
{
    return await SearchDeadlineChecks.Run();
}

if (args.Length == 4 && args[0] == "mutation-probes")
{
    await MutationProbeChecks.Run(args[1], args[2] == "disk", args[3] == "cancel");
    return 0;
}

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: Lokad.Sift.TestHost <corpus> <ready-file> <release-file>");
    return 1;
}

using var index = new SiftIndex(args[0]);
using var snapshot = index.OpenSnapshot();
File.WriteAllText(args[1], "ready");

var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(1);
while (!File.Exists(args[2]))
{
    if (DateTime.UtcNow >= deadline)
    {
        Console.Error.WriteLine("Timed out waiting for release.");
        return 2;
    }

    Thread.Sleep(20);
}

return 0;
