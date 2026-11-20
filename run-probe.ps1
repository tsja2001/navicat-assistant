$ErrorActionPreference = "Stop"

$output = Join-Path $PSScriptRoot "dist\Assistant"
$executable = Join-Path $output "Assistant.exe"
if (Test-Path -LiteralPath $executable -PathType Leaf) {
    Start-Process -FilePath $executable -WindowStyle Normal
    return
}

$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
$probe = Join-Path $output "Assistant.dll"

if (-not (Test-Path $dotnet)) {
    throw "Windows x64 dotnet was not found: $dotnet"
}

if (-not (Test-Path $probe)) {
    throw "Published app was not found: $probe. Run build-and-publish.ps1 first."
}

Start-Process -FilePath $dotnet -ArgumentList @("`"$probe`"")
