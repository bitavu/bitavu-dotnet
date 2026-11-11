# Publish Bitavu.Client to NuGet.org

CI builds and audits the package and runs the package consumer on .NET 10
and .NET 11. A successful push to `main` retains the verified package as
the `nuget-package` artifact for seven days.

Create the [NuGet.org Trusted Publishing policy](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
while signed in as `bitavu` and select `bitavucom` as the package owner.
The workflow signs in as the policy creator. Use the following settings.

| Setting | Value |
| --- | --- |
| Repository owner | `bitavu` |
| Repository | `bitavu-dotnet` |
| Workflow file | `publish.yml` |
| Environment | `nuget` |
| Package glob | `Bitavu.Client` |

Enable the new-package scope for the first publication. Enable the new-version
scope for later releases. NuGet.org may give the policy an initial seven-day
activation window for a private GitHub repository. Configure it when the first
reviewed release is ready.

1. Review the package downloaded from the current `main` commit's CI run.
   Inspect the exact archive, source, Git history, metadata, logs, and
   artifacts before giving release approval.
2. As the approved owner, run **Publish to NuGet.org** from `main` and enter
   that CI run ID and the exact package SHA-256. The workflow verifies the
   CI run and digest before requesting a short-lived publishing key. It can
   also use the immediately preceding CI run if the only changes on
   `main` are to the publish workflow or this release guide.
3. After validation and indexing, verify the package version and owner on
   NuGet.org.
