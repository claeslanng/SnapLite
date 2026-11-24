# Builds both release versions of SnapLite into .\publish\
#   publish\framework-dependent\SnapLite.exe  (~2.7 MB, needs the .NET 10 Desktop Runtime x64)
#   publish\self-contained\SnapLite.exe       (~118 MB, runs on any 64-bit Windows 10/11; also zipped, ~46 MB)
# Usage:  .\publish.ps1            (both)
#         .\publish.ps1 -Only FrameworkDependent
param([ValidateSet('FrameworkDependent', 'SelfContained')][string[]]$Only = @('FrameworkDependent', 'SelfContained'))

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# A running SnapLite locks its files.
Get-Process SnapLite -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$PSScriptRoot\publish\*" } | Stop-Process -Force

dotnet test tests/SnapLite.Tests -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; nothing published.' }

foreach ($profile in $Only) {
    # Both profiles share intermediate files (win-x64); start each one clean so they can't mix.
    Remove-Item -Recurse -Force 'src\SnapLite\obj\Release\net10.0-windows\win-x64' -ErrorAction SilentlyContinue
    $dir = if ($profile -eq 'FrameworkDependent') { 'publish\framework-dependent' } else { 'publish\self-contained' }
    Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue

    dotnet publish src/SnapLite -p:PublishProfile=$profile --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publishing $profile failed." }
    $exe = Get-Item "$dir\SnapLite.exe"
    '{0,-20} {1}  ({2:N1} MB)' -f $profile, $exe.FullName, ($exe.Length / 1MB)

    if ($profile -eq 'FrameworkDependent') {
        # The browser extension, ready to upload to the Chrome Web Store / Edge Add-ons (no developer key).
        $ext = Join-Path $env:TEMP 'snaplite-store-extension'
        $p = Start-Process $exe.FullName -ArgumentList '--export-extension', $ext, '--store' -PassThru -Wait
        if ($p.ExitCode -ne 0) { throw 'Exporting the extension failed.' }
        $version = (Get-Content (Join-Path $ext 'manifest.json') -Raw | ConvertFrom-Json).version
        $storeZip = Join-Path $PSScriptRoot "publish\SnapLite-extension-$version-store.zip"
        Remove-Item $storeZip -ErrorAction SilentlyContinue
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        # manifest.json at the root, and forward slashes in paths (Windows PowerShell would write backslashes).
        $zipFile = [System.IO.Compression.ZipFile]::Open($storeZip, 'Create')
        try {
            foreach ($f in Get-ChildItem $ext -Recurse -File) {
                $entry = $f.FullName.Substring($ext.Length + 1).Replace('\', '/')
                [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zipFile, $f.FullName, $entry, 'Optimal')
            }
        }
        finally { $zipFile.Dispose() }
        Remove-Item -Recurse -Force $ext
        '{0,-20} {1}  ({2:N0} KB)' -f 'Extension (store)', $storeZip, ((Get-Item $storeZip).Length / 1KB)
    }

    if ($profile -eq 'SelfContained') {
        # The download version: the exe zips from ~118 MB to ~46 MB (and runs uncompressed, so memory stays low).
        $zip = Join-Path $PSScriptRoot 'publish\SnapLite-win-x64-standalone.zip'
        Remove-Item $zip -ErrorAction SilentlyContinue
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $z = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
        try { [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $exe.FullName, 'SnapLite.exe', 'Optimal') }
        finally { $z.Dispose() }
        '{0,-20} {1}  ({2:N1} MB)' -f 'Standalone zip', $zip, ((Get-Item $zip).Length / 1MB)
    }
}
