param([string]$OutputPath, [switch]$Test)
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath $framework)) { $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$compiler = Join-Path $framework 'csc.exe'
$sdk = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -ErrorAction SilentlyContinue).KitsRoot10
if (-not $sdk) { $sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10' }
$metadata = Get-ChildItem -Path (Join-Path $sdk 'UnionMetadata\10.*\Windows.winmd') -ErrorAction SilentlyContinue |
    Sort-Object { [version]$_.Directory.Name } -Descending | Select-Object -First 1
if (-not $metadata) { throw 'Install the Windows 10/11 SDK (10.0.19041 or newer) to build from source.' }
if (-not $OutputPath) {
    $name = if ($Test) { 'BatteryTests.exe' } else { 'GpwBattery.exe' }
    $OutputPath = Join-Path (Split-Path $PSScriptRoot -Parent) ('dist\' + $name)
}
$target = [IO.Path]::GetFullPath($OutputPath)
$null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
$references = @('System.Drawing','System.Windows.Forms','System.Web.Extensions','System.Runtime',
                'System.Runtime.WindowsRuntime','System.Runtime.InteropServices.WindowsRuntime','System.Threading.Tasks',
                'System.Runtime.InteropServices','System.ObjectModel','System.Runtime.Extensions')
$options = @('/nologo','/optimize+','/codepage:65001','/warnaserror+',"/out:$target",('/reference:' + $metadata.FullName))
$options += $references | ForEach-Object { '/reference:' + (Join-Path $framework ($_ + '.dll')) }
if ($Test) { $options += '/target:exe','/main:GpwBattery.BatteryTests','..\tests\BatteryTests.cs' }
else { $options += '/target:winexe','/win32manifest:app.manifest','/win32icon:app.ico' }
$sources = Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File | Sort-Object Name | ForEach-Object FullName
Push-Location $PSScriptRoot
try {
    & $compiler @options @sources
    if ($LASTEXITCODE -ne 0) { throw "C# compiler exited with code $LASTEXITCODE" }
} finally { Pop-Location }
if ($Test) {
    & $target
    if ($LASTEXITCODE -ne 0) { throw 'Battery regression tests failed.' }
}
