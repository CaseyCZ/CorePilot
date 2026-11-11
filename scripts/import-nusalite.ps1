param()
$ErrorActionPreference = 'Stop'
$revision = '3a13d3447cd8b17be66807e9008c49e869ba22f2'
$upstream = Join-Path $env:RUNNER_TEMP 'nusalite-upstream'
$iconBackup = Join-Path $env:RUNNER_TEMP 'CorePilot.ico'

# Keep the existing CorePilot identity, not the NusaLite branding.
Copy-Item 'src/CorePilot/Assets/CorePilot.ico' $iconBackup -Force
& git clone --quiet https://github.com/Pidzzzz/nusa-lite.git $upstream
if ($LASTEXITCODE -ne 0) { throw 'NusaLite clone failed.' }
& git -C $upstream checkout --detach $revision
if ($LASTEXITCODE -ne 0) { throw 'Pinned upstream revision is not available.' }

$keep = @(
 '.github/workflows/import-nusalite.yml',
 '.github/workflows/build.yml',
 'scripts/import-nusalite.ps1',
 'scripts/smoke-startup.ps1',
 'installer/CorePilot.iss'
)
$tracked = @(& git ls-files)
foreach ($path in $tracked) {
    if ($path -notin $keep) {
        & git rm --quiet -f -- $path
        if ($LASTEXITCODE -ne 0) { throw "Could not remove obsolete file $path" }
    }
}

# Import only source code and MIT license. The source's font binaries, logo,
# marketing images and old build workflows are intentionally not redistributed.
$files = Get-ChildItem -Path $upstream -Recurse -File
$copied = 0
foreach ($file in $files) {
    $relative = $file.FullName.Substring($upstream.Length + 1).Replace('\', '/')
    if ($relative -ne 'LICENSE' -and
        $relative -ne '.gitignore' -and
        $file.Extension -notin @('.cs', '.xaml', '.csproj', '.manifest')) { continue }
    $target = if ($relative -eq 'NusaLite.csproj') { 'CorePilot.csproj' } else { $relative }
    $destination = Join-Path (Get-Location).Path $target
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    $content = [System.IO.File]::ReadAllText($file.FullName)

    if ($relative -ne 'LICENSE') {
        $content = $content.Replace('NusaLite', 'CorePilot').Replace('Nusa Lite', 'CorePilot')
        $content = $content.Replace('nusa.ico', 'CorePilot.ico')
        if ($relative -eq 'App.xaml.cs') {
            $content = $content.Replace('CultureInfo.GetCultureInfo("id-ID")', 'CultureInfo.GetCultureInfo("cs-CZ")')
        }
        if ($relative -eq 'Themes/Styles.xaml') {
            $content = [regex]::Replace($content,
                '<FontFamily x:Key="DisplayFont">[^<]*</FontFamily>',
                '<FontFamily x:Key="DisplayFont">Segoe UI Variable Display, Segoe UI</FontFamily>')
            $content = [regex]::Replace($content,
                '<FontFamily x:Key="UiFont">[^<]*</FontFamily>',
                '<FontFamily x:Key="UiFont">Segoe UI Variable Text, Segoe UI</FontFamily>')
        }
        if ($relative -eq 'NusaLite.csproj') {
            $content = $content.Replace('net8.0-windows10.0.19041.0', 'net10.0-windows10.0.19041.0')
            $content = $content.Replace('10.0.17763.0', '10.0.19041.0')
            $content = $content.Replace('<Version>1.3.0</Version>', '<Version>2.0.0</Version>')
            $content = [regex]::Replace($content, '\s*<Resource Include="Fonts\\Inter-[^"]+\.ttf" />', '')
            $content = [regex]::Replace($content, '<ApplicationIcon>[^<]*</ApplicationIcon>',
                '<ApplicationIcon>Assets\CorePilot.ico</ApplicationIcon>')
            $content = [regex]::Replace($content, '<Authors>[^<]*</Authors>',
                '<Authors>CaseyCZ (based on Pidzzzz)</Authors>')
            $content = [regex]::Replace($content, '<Description>[^<]*</Description>',
                '<Description>A living island for Windows 10 and 11.</Description>')
        }
    }
    [System.IO.File]::WriteAllText($destination, $content,
        [System.Text.UTF8Encoding]::new($false))
    $copied++
}
New-Item -ItemType Directory -Path 'Assets' -Force | Out-Null
Copy-Item $iconBackup 'Assets/CorePilot.ico' -Force

# Adapt the existing CorePilot setup wizard to the new source layout.
$installer = [System.IO.File]::ReadAllText('installer/CorePilot.iss')
$installer = $installer.Replace('#define AppVersion "1.2.0"', '#define AppVersion "2.0.0"')
$installer = $installer.Replace('..\src\CorePilot\Assets\CorePilot.ico', '..\Assets\CorePilot.ico')
[System.IO.File]::WriteAllText('installer/CorePilot.iss', $installer,
    [System.Text.UTF8Encoding]::new($false))

$readme = @'
<div align="center">

# CorePilot

### Your desktop, one beautiful island.

An elegant, living desktop notch for **Windows 10 & 11**.

[**Download**](https://github.com/CaseyCZ/CorePilot/releases/latest) · [**Česky**](README.cs.md) · [**Feedback**](https://github.com/CaseyCZ/CorePilot/issues)

</div>

## A little island with a lot to offer

CorePilot sits discreetly at the top of the screen and expands when you need it. Music, volume, quick controls and useful tools are always within reach — without another big window.

- **Music:** album artwork, artist, play/pause, next/previous and seek controls, using Windows media sessions.
- **Interactive notch:** subtle animations, hover controls, status indicators and fullscreen auto-hide.
- **Sound and battery:** convenient access to volume and power information.
- **More when you need it:** optional lyrics, focus timer and temporary file shelf.
- **Personalize:** themes, shortcuts and behavior settings.

Compatible with **Windows 10 (2004 or later)** and **Windows 11**, x64.

## Get CorePilot

Download the latest release from the [official CorePilot releases page](https://github.com/CaseyCZ/CorePilot/releases/latest).

The new NusaLite-based generation is in preview. The current public release continues to offer the previous generation until the replacement has passed Windows tests.

## Open and transparent

CorePilot is free and open-source under the MIT license. Its living island engine is based on [NusaLite by Pidzzzz](https://github.com/Pidzzzz/nusa-lite); see the [license](LICENSE) and [attribution](NOTICE.md). This is not the closed-source Nusa application.

*Album art and optional lyrics can use external services. No personal account is required.*

<div align="center">

**CorePilot — Less clutter. More control.**

</div>
'@
$readmeCs = @'
<div align="center">

# CorePilot

### Celý svět Windows v jednom ostrůvku.

Elegantní Dynamic Island pro **Windows 10 a 11**.

[**Aktuální vydání**](https://github.com/CaseyCZ/CorePilot/releases/latest) · [**English**](README.md) · [**Nápady a chyby**](https://github.com/CaseyCZ/CorePilot/issues)

</div>

## Není potřeba otevírat další okna

CorePilot se nenápadně drží horního okraje obrazovky. Najeď na něj myší a rozbalí se. Ukáže právě přehrávanou hudbu, nabídne ovládání hlasitosti a další praktické funkce.

- **Hudba:** obal alba, interpret, pozastavení, přepínání a posun skladby.
- **Živý ostrůvek:** přirozené animace, rozbalování a skrytí při hrách.
- **Stav počítače:** zvuk a baterie přehledně na jednom místě.
- **Volitelné funkce:** texty písní, časovač soustředění a odkládání souborů.
- **Vzhled a ovládání:** vlastní nastavení, motivy a zkratky.

**Kompatibilita:** Windows 10 od verze 2004 a Windows 11, 64 bitů.

## Instalace

[Zobrazit poslední vydání CorePilotu](https://github.com/CaseyCZ/CorePilot/releases/latest).

Nová generace založená na NusaLite je zatím ve vývoji. Dosavadní veřejný instalátor zůstává dostupný, než náhrada projde testy na Windows.

CorePilot je otevřený projekt s licencí MIT, založený na [NusaLite od Pidzzzz](https://github.com/Pidzzzz/nusa-lite). Viz [licence](LICENSE) a [poděkování](NOTICE.md). Nejde o původní uzavřenou aplikaci Nusa.
'@
$notice = @"
# Open-source attribution

CorePilot's new desktop engine is a derivative of the MIT-licensed
[NusaLite](https://github.com/Pidzzzz/nusa-lite) by **Pidzzzz**.
Pinned upstream revision: $revision

The upstream author's copyright and MIT license are preserved in LICENSE.
The bundled upstream fonts and original branding are intentionally omitted.
CorePilot uses Segoe UI and its own icon.

This derivative is independent of the original closed-source Nusa by rama2555.
"@
[System.IO.File]::WriteAllText('README.md', $readme, [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllText('README.cs.md', $readmeCs, [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllText('NOTICE.md', $notice, [System.Text.UTF8Encoding]::new($false))

& git config user.name 'github-actions[bot]'
& git config user.email '41898282+github-actions[bot]@users.noreply.github.com'
& git add -A
& git commit -m 'Replace preview app with MIT NusaLite base, branded CorePilot'
if ($LASTEXITCODE -ne 0) { throw 'Migration commit failed.' }
& git push origin "HEAD:refs/heads/migration/nusalite-corepilot-20261111"
if ($LASTEXITCODE -ne 0) { throw 'Could not push migration branch.' }
Write-Host "Imported $copied NusaLite source and license files at pinned revision $revision."
