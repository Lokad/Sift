# Dependency Policy

Sift uses a strict dependency allowlist. The goal is to keep the package surface conservative and auditable.

## Allowlisted Packages (Direct References Only)

These are the only NuGet packages allowed as direct `PackageReference` entries in Sift:

- Lokad.Utf8Regex
- BenchmarkDotNet
- Microsoft.NET.Test.Sdk
- xunit
- xunit.runner.visualstudio

Any additional package must be introduced by updating this file and `Directory.Packages.props`.

## Enforcement

`Directory.Build.targets` enforces the allowlist during restore/build for Sift projects.
