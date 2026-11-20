param(
    [string]$DotnetPath = (Join-Path $env:ProgramFiles "dotnet\dotnet.exe"),
    [switch]$SelfContained,
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"

$assistantRoot = $PSScriptRoot
$project = Join-Path $assistantRoot "src\NavicatTextProbe\NavicatTextProbe.csproj"
$output = Join-Path $assistantRoot "dist\Assistant"
$defaultExe = Join-Path $output "Assistant.exe"
if (Get-Process -Name Assistant -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $defaultExe }) {
    throw "Exit Assistant from its tray menu before publishing to dist\Assistant."
}
if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
    throw "Windows x64 dotnet was not found: $DotnetPath"
}

$publishArguments = @(
    "publish", $project,
    "--configuration", "Release",
    "--runtime", "win-x64",
    "--self-contained", $SelfContained.IsPresent.ToString().ToLowerInvariant(),
    "--property:UseAppHost=true",
    "--output", $output
)
if ($NoRestore) {
    $publishArguments += "--no-restore"
}

& $DotnetPath @publishArguments

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Write-Host "Published successfully:" -ForegroundColor Green
Write-Host (Join-Path $output "Assistant.exe")
