[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

Push-Location (Join-Path $PSScriptRoot '..')
try {
    $project = [xml](Get-Content -LiteralPath 'Bitavu.Client.csproj' -Raw)
    $version = [string]$project.Project.PropertyGroup.Version
    if ($version -notmatch '\A[0-9]+\.[0-9]+\.[0-9]+(?:-[a-z0-9.]+)?\z') {
        throw 'The SDK project must declare a package version.'
    }
    $package = "artifacts/Bitavu.Client.$version.nupkg"

    dotnet pack Bitavu.Client.csproj --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'SDK packaging failed.' }

    python verification/audit_package.py $package
    if ($LASTEXITCODE -ne 0) { throw 'Package audit failed.' }

    $cache = Join-Path (Resolve-Path artifacts).Path ([Guid]::NewGuid().ToString('N'))
    dotnet build verification/Verification.csproj --configuration Release "-p:RestorePackagesPath=$cache"
    if ($LASTEXITCODE -ne 0) { throw 'Verification build failed.' }

    foreach ($target in @('net10.0', 'net11.0')) {
        dotnet "verification/bin/Release/$target/Bitavu.Client.Verification.dll" $package
        if ($LASTEXITCODE -ne 0) { throw "Package verification failed on $target." }
    }

    if ($env:GITHUB_OUTPUT) {
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "package=$package"
    }
}
finally {
    Pop-Location
}
