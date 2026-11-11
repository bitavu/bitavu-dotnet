# Release Bitavu.Client

Bitavu.Client releases are published to [NuGet.org](https://www.nuget.org/packages/Bitavu.Client)
under the `bitavucom` organization.

## Prepare a release

1. Set the release version in `Bitavu.Client.csproj`. Update the install
   command in `README.md` and the package reference in
   `verification/Verification.csproj` to match.
2. Run the [package checks](verification/README.md). They build the package,
   audit its contents, and test it on .NET 10 and .NET 11.
3. Open the successful **CI** run for the release commit on `main`. Download
   the `nuget-package` artifact and find the package SHA-256 in the
   verification step. CI keeps the artifact for seven days.

## Publish the package

Review the exact package alongside its source, Git history, repository
metadata, and CI outputs. Resolve any findings and obtain the repository
owner's approval for that package.

In GitHub Actions, open **Publish to NuGet.org**, select `main`, and enter the
CI run ID and package SHA-256. The workflow checks the digest and publishes
the same archive using NuGet.org Trusted Publishing.

After NuGet.org indexes the release, verify the version and package owner on
the listing.

## Configure Trusted Publishing

The [NuGet.org policy](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
selects `bitavucom` as the package owner and grants permission to publish
versions of `Bitavu.Client`. The workflow signs in as the account that created
the policy.

| Policy setting | Value |
| --- | --- |
| GitHub repository owner | `bitavu` |
| Repository | `bitavu-dotnet` |
| Workflow file | `publish.yml` |
| Environment | `nuget` |
| Package glob | `Bitavu.Client` |
