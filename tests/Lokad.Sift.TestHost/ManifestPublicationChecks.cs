using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Lokad.Sift;

internal static class ManifestPublicationChecks
{
    private const int Publications = 48;

    public static async Task CheckWindowsProperties(string root)
    {
        if (!OperatingSystem.IsWindows()) return;
        // Exercise the actual native path conversion beyond MAX_PATH, with UTF-16 names.
        root = Path.Combine(root, new string('a', 90), new string('b', 90), "corpus-\u03bb-\U0001F642");
        using var index = new SiftIndex(root);
        await index.Build(new Documents(), new BuildOptions { CommitMetadataProvider = _ => Metadata(0) });
        var manifest = new FileInfo(Path.Combine(root, "manifest.bin"));
        var owner = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Current user has no SID.");
        var restricted = new FileSecurity();
        restricted.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        restricted.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
        manifest.SetAccessControl(restricted);
        var expectedAcl = manifest.GetAccessControl(AccessControlSections.Access);
        var oldBytes = File.ReadAllBytes(manifest.FullName);
        using var held = new FileStream(manifest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        await index.Update(new EmptyDocuments(), [], new UpdateOptions { CommitMetadataProvider = _ => Metadata(1) });
        var retainedBytes = new byte[oldBytes.Length];
        held.ReadExactly(retainedBytes);
        Require(retainedBytes.AsSpan().SequenceEqual(oldBytes), "A held manifest handle changed after publication.");
        Require(!File.ReadAllBytes(manifest.FullName).AsSpan().SequenceEqual(oldBytes), "Manifest replacement did not publish new bytes.");
        var actualAcl = manifest.GetAccessControl(AccessControlSections.Access);
        var expectedRules = expectedAcl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        var actualRules = actualAcl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        Require(actualAcl.AreAccessRulesProtected == expectedAcl.AreAccessRulesProtected &&
            actualRules.Length == expectedRules.Length, "Publication changed the manifest access rules or protection.");
        for (var i = 0; i < expectedRules.Length; i++)
        {
            var expected = expectedRules[i];
            var actual = actualRules[i];
            Require(actual.IdentityReference == expected.IdentityReference &&
                actual.AccessControlType == expected.AccessControlType && actual.FileSystemRights == expected.FileSystemRights &&
                actual.IsInherited == expected.IsInherited && actual.InheritanceFlags == expected.InheritanceFlags &&
                actual.PropagationFlags == expected.PropagationFlags, "Publication changed a manifest access rule.");
        }

        var beforeFailure = index.GetIndexIdentity();
        var attributes = File.GetAttributes(manifest.FullName);
        File.SetAttributes(manifest.FullName, attributes | FileAttributes.ReadOnly);
        try
        {
            try
            {
                await index.Update(new EmptyDocuments(), [], new UpdateOptions { CommitMetadataProvider = _ => Metadata(2) });
                throw new InvalidOperationException("Read-only manifest replacement unexpectedly succeeded.");
            }
            catch (IOException) { }
        }
        finally { File.SetAttributes(manifest.FullName, attributes); }
        Require(index.GetIndexIdentity() == beforeFailure, "Failed publication changed the visible identity.");
        Require(!index.RequiresRebuild, "A publication access failure poisoned the old index.");
        Require(!File.Exists(manifest.FullName + ".tmp"), "Failed publication left a recoverable temporary manifest.");
        await index.Update(new EmptyDocuments(), [], new UpdateOptions { CommitMetadataProvider = _ => Metadata(3) });
        using var recovered = index.OpenSnapshot();
        Check(recovered);

    }

    public static async Task Run(string root, bool separateProcess)
    {
        using var writer = new SiftIndex(root);
        await writer.Build(new Documents(), new BuildOptions
        {
            CommitMetadataProvider = _ => Metadata(0)
        });
        using var retained = writer.OpenSnapshot();
        var ready = Path.Combine(root, "observer.ready");
        var stop = Path.Combine(root, "observer.stop");
        Process? process = null;
        Task? observers = null;
        Task<string>? output = null;
        Task<string>? error = null;
        try
        {
            if (separateProcess)
            {
                var start = new ProcessStartInfo("dotnet")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var argument in new[]
                {
                    typeof(ManifestPublicationChecks).Assembly.Location,
                    "manifest-observer", root, ready, stop
                }) start.ArgumentList.Add(argument);
                process = Process.Start(start) ?? throw new InvalidOperationException("Observer did not start.");
                output = process.StandardOutput.ReadToEndAsync();
                error = process.StandardError.ReadToEndAsync();
            }
            else
            {
                observers = Observe(root, ready, stop);
            }

            var waiting = Stopwatch.StartNew();
            while (!File.Exists(ready))
            {
                if (observers?.IsCompleted == true) await observers;
                if (process?.HasExited == true)
                    throw new InvalidOperationException($"Observer exited before readiness: {(error is null ? "" : await error)}");
                if (waiting.Elapsed > TimeSpan.FromSeconds(10))
                    throw new TimeoutException("Observer readiness timed out.");
                await Task.Delay(5);
            }

            for (var revision = 1; revision <= Publications; revision++)
            {
                await writer.Update(new EmptyDocuments(), [], new UpdateOptions
                {
                    CommitMetadataProvider = _ => Metadata(revision)
                });
            }
        }
        finally
        {
            File.WriteAllText(stop, "stop");
            if (observers is not null) await observers.WaitAsync(TimeSpan.FromSeconds(10));
            if (process is not null)
            {
                using (process)
                {
                    try
                    {
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                        if (process.ExitCode != 0)
                            throw new InvalidOperationException($"{(output is null ? "" : await output)}\n{(error is null ? "" : await error)}");
                    }
                    finally
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                            await process.WaitForExitAsync();
                        }
                    }
                }
            }
        }

        Check(retained);
        Require(BinaryPrimitives.ReadInt32LittleEndian(retained.CommitMetadata.Span) == 0,
            "An earlier snapshot's metadata changed.");
        Require(!writer.RequiresRebuild, "Concurrent publication poisoned the writer.");
    }

    public static async Task Observe(string root, string ready, string stop)
    {
        var started = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var readers = started.Select(signal => Task.Factory.StartNew(() =>
        {
            try
            {
                using var index = new SiftIndex(root);
                using (var first = index.OpenSnapshot()) Check(first);
                signal.SetResult();
                var deadline = Stopwatch.StartNew();
                while (!File.Exists(stop))
                {
                    using var snapshot = index.OpenSnapshot();
                    Check(snapshot);
                    if (deadline.Elapsed > TimeSpan.FromSeconds(15))
                        throw new TimeoutException("Writer did not finish publication.");
                }
                using var last = index.OpenSnapshot();
                Check(last);
                Require(BinaryPrimitives.ReadInt32LittleEndian(last.CommitMetadata.Span) == Publications,
                    "The next snapshot did not observe the completed publications.");
                Require(!index.RequiresRebuild, "Concurrent publication poisoned a reader.");
            }
            catch (Exception exception)
            {
                signal.TrySetException(exception);
                throw;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        await Task.WhenAll(started.Select(signal => signal.Task));
        File.WriteAllText(ready, "ready");
        await Task.WhenAll(readers);
    }

    private static byte[] Metadata(int revision)
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, revision);
        return bytes;
    }

    private static void Check(IIndexSnapshot snapshot)
    {
        Require(snapshot.Identity.CorpusId != CorpusId.Empty, "Publication exposed an empty corpus.");
        Require(snapshot.CommitMetadata.Length == sizeof(int), "Publication exposed incomplete metadata.");
        var revision = BinaryPrimitives.ReadInt32LittleEndian(snapshot.CommitMetadata.Span);
        Require(revision >= 0 && revision <= Publications, "Publication exposed invalid metadata.");
        var collector = new Match();
        var stats = snapshot.Search(new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), collector);
        Require(stats.HitsReturned == 1 && stats.StopReason == SearchStopReason.None, "Publication changed document visibility.");
        Require(snapshot.GetDocumentKey(collector.Hit).Path == "doc.txt", "Publication changed the document key.");
        Require(snapshot.GetDocumentBytes(collector.Hit).Span.SequenceEqual("alpha\n"u8), "Publication changed borrowed content.");
        Require(snapshot.Materialize(collector.Hit, 0, 0).MatchText == "alpha", "Publication changed materialization.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Match : IHitCollector
    {
        public RawHit Hit { get; private set; }
        public bool OnHit(in RawHit hit) { Hit = hit; return true; }
    }

    private sealed class Documents : IDocumentSource
    {
        private bool _read;
        public bool TryReadNext(out SubmittedDocument document)
        {
            if (_read) { document = default; return false; }
            document = new SubmittedDocument("doc.txt", "alpha\n"u8.ToArray());
            _read = true;
            return true;
        }
    }

    private sealed class EmptyDocuments : IDocumentSource
    {
        public bool TryReadNext(out SubmittedDocument document) { document = default; return false; }
    }
}
