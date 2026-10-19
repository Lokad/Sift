# Dependency Policy

Sift uses a strict dependency allowlist. The goal is to keep the package surface conservative and auditable.

## Allowlisted Packages (Direct References Only)

These are the only NuGet packages allowed as direct `PackageReference` entries in Sift:

- Lokad.Utf8Regex (direct test/benchmark and optional test-host integrations only)
- BenchmarkDotNet
- Microsoft.SourceLink.GitHub
- Microsoft.NET.Test.Sdk
- xunit
- xunit.runner.visualstudio

Any additional package must be introduced by updating this file and `Directory.Packages.props`.

## Enforcement

`Directory.Build.targets` enforces the allowlist during restore/build for Sift projects.

The shipped `Lokad.Sift` assembly/package has no runtime package dependencies.
`Microsoft.SourceLink.GitHub` is private build tooling. The core project and CLI
may not reference `Lokad.Utf8Regex`; the allowlist scopes that package to the test,
benchmark, and optional test-host projects. Tests keep their direct pinned
reference and exercise the real producer through application-side adapter source
and differential checks. Producer-free core verification uses isolated restores
and package consumers rather than removing the test dependency.

Hosts implement the Sift-owned regex contract and reference their chosen producer
directly. No shared abstraction or adapter package is introduced. Core release
independence and qualification of real UTF-8 acceleration are separate: inability
to establish native capability selects managed verification without hiding the
execution route or requiring a producer upgrade.
