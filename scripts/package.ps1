$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
$exe = Join-Path $dist 'GpwBattery.exe'
& (Join-Path $root 'src\build.ps1') -OutputPath $exe
$ver = [version](Get-Item -LiteralPath $exe).VersionInfo.FileVersion
$name = 'logi-mouse-battery-' + $ver.ToString(3) + '-windows.zip'
$zip = Join-Path $dist $name
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$temp = Join-Path $dist ([guid]::NewGuid().ToString('N') + '.zip')
try {
    $archive = [IO.Compression.ZipFile]::Open($temp, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($item in @('dist/GpwBattery.exe', 'README.md', 'LICENSE', 'docs/styles.png')) {
            $entry = if ($item.StartsWith('dist/')) { $item.Substring(5) } else { $item }
            $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $root $item), $entry)
        }
    } finally { $archive.Dispose() }
    if ([IO.File]::Exists($zip)) { [IO.File]::Replace($temp, $zip, [NullString]::Value) } else { [IO.File]::Move($temp, $zip) }
} finally { if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) } }
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($zip + '.sha256'), ($hash + '  ' + $name + [Environment]::NewLine), [Text.Encoding]::ASCII)
Get-Item -LiteralPath $zip | Select-Object Name,Length
