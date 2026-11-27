# Builds CodexScheduler.exe with the C# compiler that ships with .NET Framework.
# No SDK, NuGet or network access required.
#
#   powershell -ExecutionPolicy Bypass -File .\build.ps1
#
param(
    [string]$Out = "CodexScheduler.exe"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    Write-Host 'csc.exe not found - .NET Framework 4.x is required.' -ForegroundColor Red
    exit 1
}

$src = Join-Path $root 'src\*.cs'
$ico = Join-Path $root 'src\app.ico'
$man = Join-Path $root 'src\app.manifest'
$exe = Join-Path $root $Out

& $csc /nologo /target:winexe /optimize+ /warn:4 "/out:$exe" "/win32icon:$ico" "/win32manifest:$man" `
    /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll `
    $src

if ($LASTEXITCODE -ne 0) {
    Write-Host 'build failed' -ForegroundColor Red
    exit 1
}

Get-Item $exe | Select-Object Name, Length, LastWriteTime | Format-List
Write-Host "built: $exe"
