# Local package verification

Run from the client project directory with a stable .NET 11 SDK, the .NET 10
and .NET 11 runtimes, and Python 3.
The same command runs in GitHub Actions. It uses a fresh restore cache so the
consumer loads the exact package just built.

```powershell
pwsh -File verification/check-package.ps1
```

The consumer restores from the local package directory and uses synthetic HTTP
responses. The audit prints the SHA-256 of the inspected archive.
