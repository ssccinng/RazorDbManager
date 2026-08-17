[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string] $PackageDirectory = (Join-Path $PSScriptRoot "..\..\artifacts\packages"),

    [Parameter(Mandatory = $false)]
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [Parameter(Mandatory = $false)]
    [int] $StartupTimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"
$projectDirectory = [System.IO.Path]::GetFullPath($PSScriptRoot)
$project = Join-Path $projectDirectory "RazorDbManager.PackageSmoke.csproj"
$nugetConfigTemplate = Join-Path $projectDirectory "NuGet.Config"
$resolvedPackageDirectory = [System.IO.Path]::GetFullPath($PackageDirectory)
$runRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("RazorDbManager.PackageSmoke-" + [Guid]::NewGuid().ToString("N"))
$nugetConfig = Join-Path $runRoot "NuGet.Config"
$packagesPath = Join-Path $runRoot "packages"
$intermediatePath = Join-Path $runRoot "obj"
$outputPath = Join-Path $runRoot "bin"
$storagePath = Join-Path $runRoot "state"
$process = $null
$stdoutTask = $null
$stderrTask = $null

$providerPackages = @(Get-ChildItem -LiteralPath $resolvedPackageDirectory -File -Filter "RazorDbManager.MySql.*.nupkg" |
    Where-Object { $_.Name -notlike "*.symbols.nupkg" })
if ($providerPackages.Count -ne 1) {
    throw "Expected exactly one versioned RazorDbManager.MySql package."
}
$providerPackageMatch = [regex]::Match(
    $providerPackages[0].Name,
    '^RazorDbManager\.MySql\.(?<version>.+)\.nupkg$')
if (-not $providerPackageMatch.Success) {
    throw "The RazorDbManager.MySql package file name does not contain a valid version."
}
$packageVersion = $providerPackageMatch.Groups["version"].Value

function Invoke-DotNetChecked {
    param([Parameter(Mandatory = $true)][string[]] $Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Get-AvailableLoopbackPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([System.Net.IPEndPoint] $listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Invoke-SmokeRequest {
    param([Parameter(Mandatory = $true)][string] $Uri)

    return Invoke-WebRequest -Uri $Uri -TimeoutSec 5 -MaximumRedirection 0 -SkipHttpErrorCheck
}

try {
    & (Join-Path $projectDirectory "Verify-Packages.ps1") -PackageDirectory $resolvedPackageDirectory

    [xml] $projectXml = Get-Content -LiteralPath $project -Raw
    $packageReferences = @($projectXml.SelectNodes("/Project/ItemGroup/PackageReference"))
    if ($packageReferences.Count -ne 1 -or $packageReferences[0].GetAttribute("Include") -ne "RazorDbManager.MySql") {
        throw "The package consumer must reference only RazorDbManager.MySql."
    }
    if (@($projectXml.SelectNodes("/Project/ItemGroup/ProjectReference")).Count -ne 0) {
        throw "The package consumer must not contain source ProjectReference items."
    }

    New-Item -ItemType Directory -Path $runRoot | Out-Null
    Copy-Item -LiteralPath $nugetConfigTemplate -Destination $nugetConfig
    [xml] $nugetConfigXml = Get-Content -LiteralPath $nugetConfig -Raw
    $localPackageSource = @($nugetConfigXml.configuration.packageSources.add) |
        Where-Object { $_.key -eq "RazorDbManager local packages" } |
        Select-Object -First 1
    if ($null -eq $localPackageSource) {
        throw "The package consumer NuGet.Config has no local package source."
    }
    $localPackageSource.value = $resolvedPackageDirectory
    $nugetConfigXml.Save($nugetConfig)

    $commonProperties = @(
        "/p:RestorePackagesPath=$packagesPath",
        "/p:BaseIntermediateOutputPath=$intermediatePath$([System.IO.Path]::DirectorySeparatorChar)",
        "/p:BaseOutputPath=$outputPath$([System.IO.Path]::DirectorySeparatorChar)",
        "/p:RazorDbManagerPackageVersion=$packageVersion"
    )
    $restoreArguments = @(
        "restore", $project,
        "--configfile", $nugetConfig,
        "--force-evaluate"
    ) + $commonProperties
    Invoke-DotNetChecked $restoreArguments
    $buildArguments = @(
        "build", $project,
        "--configuration", $Configuration,
        "--no-restore"
    ) + $commonProperties
    Invoke-DotNetChecked $buildArguments

    $application = [System.IO.Path]::Combine(
        $outputPath,
        $Configuration,
        "net10.0",
        "RazorDbManager.PackageSmoke.dll")
    if (-not (Test-Path -LiteralPath $application -PathType Leaf)) {
        throw "The package consumer output was not found: $application"
    }

    $port = Get-AvailableLoopbackPort
    $baseUri = "http://127.0.0.1:$port"
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = "dotnet"
    $startInfo.WorkingDirectory = $projectDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add($application)
    $startInfo.ArgumentList.Add("--urls")
    $startInfo.ArgumentList.Add($baseUri)
    $startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development"
    $startInfo.Environment["PackageSmoke__StoragePath"] = $storagePath
    $process = [System.Diagnostics.Process]::Start($startInfo)
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $statusResponse = $null
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($process.HasExited) {
            throw "The package consumer exited before becoming ready (exit code $($process.ExitCode))."
        }
        try {
            $candidate = Invoke-SmokeRequest "$baseUri/_razor-db-manager/status"
            if ($candidate.StatusCode -eq 200) {
                $statusResponse = $candidate
                break
            }
        }
        catch {
            # Kestrel may not be listening during the first few attempts.
        }
        Start-Sleep -Milliseconds 200
    }
    if ($null -eq $statusResponse) {
        throw "The package consumer did not become ready within $StartupTimeoutSeconds seconds."
    }

    $status = $statusResponse.Content | ConvertFrom-Json
    if ($status.status -ne "degraded" -or
        @($status.databases).Count -ne 1 -or
        $status.databases[0].id -ne "Main" -or
        $status.databases[0].providerName -ne "mysql") {
        throw "The manager status payload did not expose the expected unreachable Main registration."
    }

    $scriptResponse = Invoke-SmokeRequest "$baseUri/_content/RazorDbManager/razordbmanager.js"
    if ($scriptResponse.StatusCode -ne 200 -or [string]::IsNullOrWhiteSpace($scriptResponse.Content)) {
        throw "The packaged RazorDbManager JavaScript asset was not served."
    }
    $accessibilityScript = Invoke-SmokeRequest "$baseUri/_content/RazorDbManager/razordbmanager-accessibility.js"
    if ($accessibilityScript.StatusCode -ne 200 -or
        $accessibilityScript.Content -notmatch "activateModal") {
        throw "The packaged RazorDbManager accessibility module was not served."
    }

    $routedPage = Invoke-SmokeRequest "$baseUri/db-manager"
    if ($routedPage.StatusCode -ne 200 -or $routedPage.Content -notmatch "_framework/blazor.web.js") {
        throw "The routed RazorDbManager RCL page was not rendered by the consumer host."
    }

    $embeddedPage = Invoke-SmokeRequest "$baseUri/"
    if ($embeddedPage.StatusCode -ne 200 -or $embeddedPage.Content -notmatch "rdm-shell") {
        throw "The embedded DatabaseManager component was not rendered by the consumer host."
    }

    Write-Host "Package consumer smoke passed: restore, build, start, degraded live status, routed page, embedded component, and RCL static JavaScript."
}
catch {
    if ($null -ne $process -and -not $process.HasExited) {
        $process.Kill($true)
        $process.WaitForExit()
    }
    if ($null -ne $stdoutTask) {
        Write-Host "--- package consumer stdout ---"
        Write-Host $stdoutTask.GetAwaiter().GetResult()
    }
    if ($null -ne $stderrTask) {
        Write-Host "--- package consumer stderr ---"
        Write-Host $stderrTask.GetAwaiter().GetResult()
    }
    throw
}
finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) {
            $process.Kill($true)
            $process.WaitForExit()
        }
        $process.Dispose()
    }
    if (Test-Path -LiteralPath $runRoot) {
        Remove-Item -LiteralPath $runRoot -Recurse -Force
    }
}
