# README.md (English, published) and README.ru.md (Russian, kept locally) must say the same thing. A program cannot
# compare meaning, so this compares what must be identical in both: the number of headings and table rows, the code
# blocks, the links and the commands/paths in `backticks`. Differences are listed; the exit code is 1 when there are any.
$root = Split-Path $PSScriptRoot -Parent
$utf8 = New-Object System.Text.UTF8Encoding $false
$en = [IO.File]::ReadAllText((Join-Path $root 'README.md'), $utf8)
$ru = [IO.File]::ReadAllText((Join-Path $root 'README.ru.md'), $utf8)
$problems = @()

function Count($text, $pattern) { ([regex]::Matches($text, $pattern, 'Multiline')).Count }
function Blocks($text) { @([regex]::Matches($text, '(?s)```.*?```') | ForEach-Object { $_.Value -replace '\r', '' }) }
function Urls($text) { @([regex]::Matches($text, 'https?://[^\s)\]`>]+') | ForEach-Object { $_.Value.TrimEnd('.', ',', ';') } | Sort-Object -Unique) }
function Spans($text) {
    # `code` outside fenced blocks; the ones holding angle brackets or non-ASCII text are placeholders that differ by language
    $plain = [regex]::Replace($text, '(?s)```.*?```', '')
    @([regex]::Matches($plain, '`([^`\r\n]+)`') | ForEach-Object { $_.Groups[1].Value } |
        Where-Object { $_ -notmatch '[<>]' -and $_ -notmatch '[^\x00-\x7F]' } | Sort-Object -Unique)
}

foreach ($check in @(
    @{ name = 'headings (##)'; pattern = '^## ' },
    @{ name = 'table rows'; pattern = '^\|' },
    @{ name = 'list items'; pattern = '^\s*(-|\d+\.) ' })) {
    $a = Count $en $check.pattern; $b = Count $ru $check.pattern
    if ($a -ne $b) { $problems += "$($check.name): $a in README.md, $b in README.ru.md" }
}

$ba = Blocks $en; $bb = Blocks $ru
if ($ba.Count -ne $bb.Count) { $problems += "code blocks: $($ba.Count) in README.md, $($bb.Count) in README.ru.md" }
else { for ($i = 0; $i -lt $ba.Count; $i++) { if ($ba[$i] -ne $bb[$i]) { $problems += "code block $($i + 1) differs" } } }

$ua = Urls $en; $ub = Urls $ru
foreach ($x in $ua) { if ($ub -notcontains $x) { $problems += "link only in README.md: $x" } }
foreach ($x in $ub) { if ($ua -notcontains $x) { $problems += "link only in README.ru.md: $x" } }

$sa = Spans $en; $sb = Spans $ru
foreach ($x in $sa) { if ($sb -notcontains $x) { $problems += "`"$x`" only in README.md" } }
foreach ($x in $sb) { if ($sa -notcontains $x) { $problems += "`"$x`" only in README.ru.md" } }

if ($problems.Count -eq 0) { 'README.md and README.ru.md are in step (headings, rows, list items, code, links, `names`)'; exit 0 }
'README.md and README.ru.md differ:'
$problems | ForEach-Object { '  - ' + $_ }
exit 1
