param(
    [Parameter(Mandatory = $false)]
    [string] $PackageDirectory = (Join-Path $PSScriptRoot "..\..\artifacts\packages")
)

$ErrorActionPreference = "Stop"
$resolvedPackageDirectory = [System.IO.Path]::GetFullPath($PackageDirectory)
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))

function Get-SinglePackage {
    param([Parameter(Mandatory = $true)][string] $Pattern)

    $packages = @(Get-ChildItem -LiteralPath $resolvedPackageDirectory -File -Filter $Pattern |
        Where-Object { $_.Name -notlike "*.symbols.nupkg" })
    if ($packages.Count -ne 1) {
        throw "Expected exactly one package matching '$Pattern', found $($packages.Count)."
    }
    return $packages[0].FullName
}

function Assert-PackageEntryMatchesFile {
    param(
        [Parameter(Mandatory = $true)][string] $PackagePath,
        [Parameter(Mandatory = $true)][string] $EntryPath,
        [Parameter(Mandatory = $true)][string] $SourcePath
    )

    if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
        throw "Current build output was not found: $SourcePath"
    }
    $entryArchive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entry = $entryArchive.Entries |
            Where-Object { $_.FullName -eq $EntryPath } |
            Select-Object -First 1
        if ($null -eq $entry) {
            throw "Package '$([IO.Path]::GetFileName($PackagePath))' is missing '$EntryPath'."
        }
        $entryStream = $entry.Open()
        try {
            $packageHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($entryStream))
        }
        finally {
            $entryStream.Dispose()
        }
        $sourceHash = (Get-FileHash -LiteralPath $SourcePath -Algorithm SHA256).Hash
        if ($packageHash -ne $sourceHash) {
            throw "Package entry '$EntryPath' does not match the current Release build. Repack the artifacts."
        }
    }
    finally {
        $entryArchive.Dispose()
    }
}

$corePackage = Get-SinglePackage "RazorDbManager.Core.*.nupkg"
$rclPackage = @(Get-ChildItem -LiteralPath $resolvedPackageDirectory -File -Filter "RazorDbManager.*.nupkg" |
    Where-Object {
        $_.Name -notlike "RazorDbManager.Core.*" -and
        $_.Name -notlike "RazorDbManager.MySql.*" -and
        $_.Name -notlike "*.symbols.nupkg"
    })
if ($rclPackage.Count -ne 1) {
    throw "Expected exactly one RazorDbManager UI package, found $($rclPackage.Count)."
}
$rclPackage = $rclPackage[0].FullName
$mySqlPackage = Get-SinglePackage "RazorDbManager.MySql.*.nupkg"

if (-not (Test-Path -LiteralPath $rclPackage -PathType Leaf)) {
    throw "Package was not found: $rclPackage"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($rclPackage)
try {
    $entries = @($archive.Entries | ForEach-Object { $_.FullName })
    $requiredPatterns = @(
        "staticwebassets/razordbmanager.js",
        "staticwebassets/razordbmanager-accessibility.js",
        "staticwebassets/*.bundle.scp.css",
        "README.md",
        "README.zh-CN.md",
        "SECURITY.md",
        "THIRD-PARTY-NOTICES.md"
    )

    foreach ($pattern in $requiredPatterns) {
        if (-not ($entries | Where-Object { $_ -like $pattern })) {
            throw "RazorDbManager package is missing required entry '$pattern'."
        }
    }

    if ($entries | Where-Object { $_ -like "*background.png" }) {
        throw "RazorDbManager package still contains the retired background.png asset."
    }

    $scriptEntry = $archive.Entries |
        Where-Object { $_.FullName -eq "staticwebassets/razordbmanager.js" } |
        Select-Object -First 1
    $sourceScript = Join-Path $repositoryRoot "src\RazorDbManager\wwwroot\razordbmanager.js"
    if (-not (Test-Path -LiteralPath $sourceScript -PathType Leaf)) {
        throw "Bundled source JavaScript was not found: $sourceScript"
    }
    $sourceHash = (Get-FileHash -LiteralPath $sourceScript -Algorithm SHA256).Hash
    $entryStream = $scriptEntry.Open()
    try {
        $packageHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($entryStream))
    }
    finally {
        $entryStream.Dispose()
    }
    if ($packageHash -ne $sourceHash) {
        throw "RazorDbManager package JavaScript does not match the current source bundle. Rebuild and repack."
    }
}
finally {
    $archive.Dispose()
}

Assert-PackageEntryMatchesFile $rclPackage "staticwebassets/razordbmanager-accessibility.js" `
    (Join-Path $repositoryRoot "src\RazorDbManager\wwwroot\razordbmanager-accessibility.js")

$securityDocument = Join-Path $repositoryRoot "SECURITY.md"
$chineseReadme = Join-Path $repositoryRoot "README.zh-CN.md"
Assert-PackageEntryMatchesFile $corePackage "README.zh-CN.md" $chineseReadme
Assert-PackageEntryMatchesFile $rclPackage "README.zh-CN.md" $chineseReadme
Assert-PackageEntryMatchesFile $mySqlPackage "README.zh-CN.md" $chineseReadme
Assert-PackageEntryMatchesFile $corePackage "SECURITY.md" $securityDocument
Assert-PackageEntryMatchesFile $rclPackage "SECURITY.md" $securityDocument
Assert-PackageEntryMatchesFile $mySqlPackage "SECURITY.md" $securityDocument

Assert-PackageEntryMatchesFile $corePackage "lib/net10.0/RazorDbManager.Core.dll" `
    (Join-Path $repositoryRoot "src\RazorDbManager.Core\bin\Release\net10.0\RazorDbManager.Core.dll")
Assert-PackageEntryMatchesFile $rclPackage "lib/net10.0/RazorDbManager.dll" `
    (Join-Path $repositoryRoot "src\RazorDbManager\bin\Release\net10.0\RazorDbManager.dll")
Assert-PackageEntryMatchesFile $mySqlPackage "lib/net10.0/RazorDbManager.MySql.dll" `
    (Join-Path $repositoryRoot "src\RazorDbManager.MySql\bin\Release\net10.0\RazorDbManager.MySql.dll")

Write-Host "Verified packages '$([IO.Path]::GetFileName($corePackage))', '$([IO.Path]::GetFileName($rclPackage))', and '$([IO.Path]::GetFileName($mySqlPackage))', including the current static JavaScript bundle."
