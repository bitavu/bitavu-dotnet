# Bitavu .NET SDK

Keep this repository private until the owner explicitly approves public
visibility. Keep credentials, customer data, private Bitavu source, personal
contact details, and identifying local paths out of source, Git history,
metadata, logs, and artifacts. Use the owner's approved GitHub noreply
identity for commits. Use approved business metadata in the package.

## Development

Maintain the SDK in `bitavu/bitavu-dotnet`. The Studio application lives in a
separate repository. Use the Studio `/v1` API contract when changing the
client, and keep the C# examples in `README.md` aligned with the public API.
Run `pwsh -File verification/check-package.ps1` from the SDK root to build,
audit, and test the exact package on .NET 10 and .NET 11.

Keep live Studio test credentials in the example application's .NET User
Secrets. Use fictional data for examples and verification. Package only
the files allowlisted in `Bitavu.Client.csproj`.

## Releases

The owner must explicitly approve the exact reviewed package before it is
published. Inspect the package, source, Git history, GitHub metadata, CI
logs, and artifacts before a public release. Resolve findings before
publishing. Use the manual workflow described in `RELEASING.md`.

The NuGet.org Trusted Publishing workflow signs in as the policy creator
`bitavu`. The policy assigns the package to `bitavucom`. Preserve the default
gold Bitavu icon in the package and verify it on the public listing.
