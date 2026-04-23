# Lokad.Sift

`Lokad.Sift` is a `net10.0` library for local indexed grep/code search over UTF-8 text.

## Turn off the .NET terminal logger

Use `--tl:off` to avoid dynamic output and progress rendering.

```powershell
dotnet restore --tl:off -v minimal
dotnet build   --tl:off --nologo -v minimal
dotnet test    --tl:off --nologo -v minimal --no-build
```

## Prefer the test script

Use `.\test.ps1` from the repository root for the standard build+test loop. It already targets `Lokad.Sift.slnx`, disables the terminal logger, and supports `-Configuration`, `-SkipBuild`, and `-Filter`.

## Prefer the benchmark script

Use `.\bench.ps1` from the repository root for the standard benchmark loop.

Important benchmark rule:

- Run benchmark work in `Release`, not `Debug`, otherwise the numbers are misleading.
- Prefer `.\bench.ps1` over ad hoc `dotnet run` so stale benchmark hosts and output paths are handled consistently.
- Benchmark commands should be run sequentially, not in parallel.
