# Verify a package build

Install the .NET 11 SDK, the .NET 10 and 11 runtimes, PowerShell 7, and Python 3.
From the repository root, run the same check used by GitHub Actions.

```powershell
pwsh -File verification/check-package.ps1
```

The script builds a NuGet package in `artifacts/`, checks its metadata and
contents, and prints its SHA-256. It then installs that exact package into a
fresh cache and tests both target frameworks with simulated API responses.
