$ErrorActionPreference = 'Stop'
$loc = Get-Content 'Services/LanguageManager.cs' -Raw -Encoding UTF8
$settings = Get-Content 'Services/SettingsStore.cs' -Raw -Encoding UTF8
$ui = Get-Content 'Views/SettingsWindow.xaml' -Raw -Encoding UTF8
$host = Get-Content 'AppHost.cs' -Raw -Encoding UTF8
$expected = @('cs', 'en')
foreach ($code in $expected) {
    if (!$loc.Contains('"' + $code + '"')) { throw "Missing language: $code" }
}
if (!$settings.Contains('public string Language { get; set; } = "cs"')) {
    throw 'Czech must be the default UI language.'
}
if (!$host.Contains('LanguageManager.Current.SetLanguage(_settings.Current.Language)')) {
    throw 'Saved UI language must be restored on startup.'
}
if (!$ui.Contains('x:Name="LanguageCombo"') -or
    !$ui.Contains('Tag="cs"') -or !$ui.Contains('Tag="en"')) {
    throw 'Expected both language choices in Settings.'
}
$keys = [regex]::Matches($loc, '\["(?<key>[A-Za-z0-9]+)"\]\s*=\s*new')
$keySet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($k in $keys) { [void]$keySet.Add($k.Groups['key'].Value) }
$uses = 0
foreach ($file in Get-ChildItem 'Views' -Filter '*.xaml') {
    $text = Get-Content $file.FullName -Raw -Encoding UTF8
    foreach ($m in [regex]::Matches($text, '\{loc:Tr Key=(?<key>[A-Za-z0-9]+)\}')) {
        $uses++
        if (!$keySet.Contains($m.Groups['key'].Value)) {
            throw "Missing translated key $($m.Groups['key'].Value) in $($file.Name)"
        }
    }
    foreach ($bad in @('Pengaturan', 'Fokus &amp; Air', 'Jeda hover',
                       'Lirik lebih cepat', 'Sebelumnya', 'Putar / jeda',
                       'Belum ada lagu', 'Lepaskan, biar kami rapikan')) {
        if ($text.Contains($bad)) { throw "Untranslated UI string '$bad' in $($file.Name)" }
    }
}
if ($uses -lt 60) { throw "Only $uses localized bindings. Expected at least 60." }
Write-Host "PASS: two supported languages, persisted selection, $uses dynamic translated XAML bindings."
