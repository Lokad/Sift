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
- The launcher requires Release, refuses concurrent launches in the same checkout,
  and manages only its own child process. It runs on Windows and Ubuntu.
- Benchmark smoke tests use Git and PowerShell for synthetic cache and launcher
  checks; neither is a runtime dependency of the library.

## Packaging

Treat NuGet artifacts as release outputs only when produced by an explicit
Release pack. Debug build/test outputs are not release artifacts.

Release packages and symbols go directly in `artifacts/nuget/`; their filenames
include the version. Use the library project's default output path:

```powershell
dotnet pack src/Lokad.Sift/Lokad.Sift.csproj -c Release --tl:off --nologo -v minimal
```

## Conditional compilation

`SIFT_INTERNAL_VERIFICATION` is the repository's only conditional-compilation
symbol. The test project defines it because it compiles the implementation
sources directly. It enables fault injection, operation counters, and helper
entry points used only to verify internal behavior.

Never define `SIFT_INTERNAL_VERIFICATION` for `Lokad.Sift` itself or for a
package build. The library project rejects the symbol before compilation and
packing so the verification hooks cannot enter the shipped assembly. Document
any future conditional-compilation symbol here and give it an explicit package
policy before introducing its first `#if`.

`tools/verify-build-guard.ps1` checks the actual guard with dummy compile/pack
targets, including whitespace and separators. It never instruments the library.
Package verification runs this check and cleans its owned temporary files; use
`-KeepTemporaryFiles` only when retaining diagnostics is useful.

## CI

`.github/workflows/ci.yml` runs locked restore, Release build/tests, and isolated
Release package consumers on Windows and Ubuntu. It uploads verification logs
and captures crash dumps on failures. `global.json` selects stable .NET 10 SDKs
so a newer preinstalled major version cannot change the build implicitly.

Use authenticated GitHub CLI calls and space CI status checks minutes apart.
Do not poll in a tight loop or rerun workflows without investigating failures.
