<#
.SYNOPSIS
    Składa Fala/THIRD-PARTY-NOTICES.txt z plików licencji pakietów NuGet, które trafiają do paczki aplikacji.

.DESCRIPTION
    Tryb domyślny: generuje plik z licencjami (uruchom po zmianie zależności i zacommituj wynik).
    Tryb -Check: sprawdza, czy każda zależność Fala.csproj jest opisana poniżej jako dołączana albo
    jako niedołączana do paczki — nowa biblioteka bez wpisu zatrzymuje build w CI.

.EXAMPLE
    pwsh tools/Generate-ThirdPartyNotices.ps1
    pwsh tools/Generate-ThirdPartyNotices.ps1 -Check
#>
param([switch]$Check)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'Fala/Fala.csproj'
$output = Join-Path $root 'Fala/THIRD-PARTY-NOTICES.txt'
$licenses = Join-Path $PSScriptRoot 'licenses'

# --- Opis składników --------------------------------------------------------------------------
# Packages: pakiety NuGet należące do składnika (muszą pokrywać wszystkie dołączane zależności).
# Files: pliki z licencją/notami w pakiecie (ścieżki względne w folderze pakietu).
# Text: tekst licencji, gdy pakiet nie zawiera pliku (licencja podana jako wyrażenie SPDX).
$components = @(
    @{ Name = 'CommunityToolkit.Mvvm'; License = 'MIT'
       Packages = @('CommunityToolkit.Mvvm'); Files = @('License.md', 'ThirdPartyNotices.txt') }
    @{ Name = 'Microsoft.Data.Sqlite'; License = 'MIT'
       Packages = @('Microsoft.Data.Sqlite', 'Microsoft.Data.Sqlite.Core')
       Text = 'MIT:Copyright (c) .NET Foundation and Contributors' }
    @{ Name = 'SQLitePCLRaw'; License = 'Apache-2.0'; Copyright = 'Copyright 2014-2024 SourceGear, LLC'
       Packages = @('SQLitePCLRaw.bundle_e_sqlite3', 'SQLitePCLRaw.core', 'SQLitePCLRaw.lib.e_sqlite3', 'SQLitePCLRaw.provider.e_sqlite3')
       Text = 'FILE:Apache-2.0.txt' }
    @{ Name = 'SQLite (e_sqlite3.dll)'; License = 'Public Domain'; Packages = @()
       Text = 'RAW:The SQLite source code is in the public domain. See https://www.sqlite.org/copyright.html' }
    @{ Name = 'Microsoft Edge WebView2'; License = 'BSD-3-Clause (Microsoft)'
       Packages = @('Microsoft.Web.WebView2'); Files = @('LICENSE.txt', 'NOTICE.txt') }
    @{ Name = 'Windows App SDK'; License = 'Microsoft Software License Terms (Distributable Code)'
       Packages = @('Microsoft.WindowsAppSDK.WinUI', 'Microsoft.WindowsAppSDK.Runtime', 'Microsoft.WindowsAppSDK.Foundation',
                    'Microsoft.WindowsAppSDK.InteractiveExperiences', 'Microsoft.WindowsAppSDK.Base')
       Files = @('NOTICE.txt')
       Note = 'Distributed under the Microsoft Software License Terms for the Windows App SDK (license.txt in the Microsoft.WindowsAppSDK NuGet packages).' }
)

# Zależności, które NIE trafiają do paczki (narzędzia budowania / część środowiska .NET).
$notShipped = @{
    'Microsoft.Windows.SDK.BuildTools'      = 'narzędzia budowania (makepri, mt) — nie są dołączane'
    'Microsoft.Windows.SDK.BuildTools.MSIX' = 'narzędzia budowania MSIX — nie są dołączane'
    'System.Memory'                         = 'na .NET 8 dostarczane przez środowisko .NET (opisane w notach .NET Runtime)'
}

# --- Zależności projektu ------------------------------------------------------------------------
$json = dotnet list $project package --include-transitive --format json | Out-String | ConvertFrom-Json
$packages = @{}
foreach ($fw in $json.projects[0].frameworks) {
    foreach ($p in @($fw.topLevelPackages) + @($fw.transitivePackages)) {
        if ($p) { $packages[$p.id] = $p.resolvedVersion }
    }
}

$described = @($components | ForEach-Object { $_.Packages }) + @($notShipped.Keys)
$missing = @($packages.Keys | Where-Object { $_ -notin $described } | Sort-Object)
if ($missing.Count -gt 0) {
    Write-Error ("Zależności bez wpisu w tools/Generate-ThirdPartyNotices.ps1: " + ($missing -join ', ') +
        ". Dodaj je do `$components (z licencją) albo do `$notShipped (z uzasadnieniem) i wygeneruj noty ponownie.")
}
if ($Check) {
    Write-Output "OK: wszystkie zależności ($($packages.Count)) mają wpis licencyjny."
    exit 0
}

# --- Generowanie --------------------------------------------------------------------------------
$cache = ((dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', '').Trim()
$mit = @'
MIT License

{0}

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
'@

function Get-PackageDir([string]$id) {
    $version = $packages[$id]
    $dir = Join-Path $cache ($id.ToLowerInvariant() + '/' + $version.ToLowerInvariant())
    if (-not (Test-Path $dir)) { throw "Brak pakietu $id $version w $cache (uruchom dotnet restore)." }
    return $dir
}

$rule = '=' * 80
$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine('Fala — THIRD-PARTY SOFTWARE NOTICES AND INFORMATION')
[void]$sb.AppendLine('Do Not Translate or Localize')
[void]$sb.AppendLine()
[void]$sb.AppendLine('Fala includes the third-party software listed below. The licenses and notices')
[void]$sb.AppendLine('of these components are reproduced as required by their authors.')
[void]$sb.AppendLine('This file is generated by tools/Generate-ThirdPartyNotices.ps1.')
[void]$sb.AppendLine()

# .NET Runtime (wdrożenie samodzielne) — najnowsza łatka 8.0.x z pakietu środowiska.
$runtimeDir = Get-ChildItem (Join-Path $cache 'microsoft.netcore.app.runtime.win-x64') -Directory |
    Where-Object { $_.Name -like '8.0.*' } | Sort-Object { [version]$_.Name } | Select-Object -Last 1
$all = @(@{ Name = ".NET Runtime $($runtimeDir.Name)"; License = 'MIT'; RuntimeDir = $runtimeDir.FullName }) + $components

foreach ($c in $all) {
    [void]$sb.AppendLine($rule)
    $versions = @($c.Packages | Where-Object { $_ } | ForEach-Object { "$_ $($packages[$_])" })
    [void]$sb.AppendLine("$($c.Name) — $($c.License)")
    if ($versions.Count) { [void]$sb.AppendLine('Packages: ' + ($versions -join ', ')) }
    if ($c.Copyright) { [void]$sb.AppendLine($c.Copyright) }
    if ($c.Note) { [void]$sb.AppendLine($c.Note) }
    [void]$sb.AppendLine($rule)
    [void]$sb.AppendLine()

    $texts = @()
    if ($c.RuntimeDir) {
        $texts += Get-Content -Raw (Join-Path $c.RuntimeDir 'LICENSE.TXT')
        $texts += Get-Content -Raw (Join-Path $c.RuntimeDir 'THIRD-PARTY-NOTICES.TXT')
    }
    foreach ($file in @($c.Files)) {
        if (-not $file) { continue }
        # Pierwszy pakiet składnika, który ma ten plik (noty Windows App SDK są identyczne w kilku pakietach).
        $path = $c.Packages | ForEach-Object { Join-Path (Get-PackageDir $_) $file } | Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $path) { throw "Brak pliku $file w pakietach składnika $($c.Name)." }
        $texts += Get-Content -Raw $path
    }
    if ($c.Text -like 'MIT:*') { $texts += ($mit -f $c.Text.Substring(4)) }
    elseif ($c.Text -like 'FILE:*') { $texts += Get-Content -Raw (Join-Path $licenses $c.Text.Substring(5)) }
    elseif ($c.Text -like 'RAW:*') { $texts += $c.Text.Substring(4) }

    foreach ($t in $texts) {
        [void]$sb.AppendLine($t.Trim())
        [void]$sb.AppendLine()
    }
}

$content = ($sb.ToString() -replace "`r`n", "`n").TrimEnd() + "`n"
[System.IO.File]::WriteAllText($output, $content, [System.Text.UTF8Encoding]::new($false))
Write-Output "Zapisano $output ($((Get-Item $output).Length / 1KB -as [int]) KB, składników: $($all.Count))"
