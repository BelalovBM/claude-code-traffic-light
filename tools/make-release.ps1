# Builds the program (bin\) and assembles <repository>\Release: the exe, licence, READMEs and SHA-256 checksums.
# The zip with the corresponding source (required by the GPL when the binary is handed out) goes to <repository>\Source.
param([switch]$NoStart)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$rel = Join-Path $root 'Release'
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'

$wasRunning = [bool](Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
Start-Sleep -Milliseconds 800
& (Join-Path $root 'build.ps1')
$ver = (Get-Item $exe).VersionInfo.ProductVersion

# The Russian readme stays on the author's computer only (not in the repository); where it exists it must say what
# the published English one says.
if (Test-Path (Join-Path $root 'README.ru.md')) {
    & (Join-Path $PSScriptRoot 'check-readme-sync.ps1')
    if ($LASTEXITCODE -ne 0) { Write-Warning 'README.md and README.ru.md are out of step (see above)' }
}

if (Test-Path $rel) { Remove-Item $rel -Recurse -Force }
New-Item -ItemType Directory -Force $rel | Out-Null
Copy-Item $exe (Join-Path $rel 'ClaudeCodeTrafficLight.exe')
foreach ($f in 'LICENSE', 'README.md') { Copy-Item (Join-Path $root $f) (Join-Path $rel $f) }

$stage = Join-Path $root 'work\source-stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$top = Join-Path $stage "ClaudeCodeTrafficLight-$ver-source"
New-Item -ItemType Directory -Force $top | Out-Null
# The same as the repository holds (see .gitignore): the author's notes and a home server's address stay out.
foreach ($d in 'src', 'lang', 'tests', 'tools', 'docs', 'packaging', '.github') { Copy-Item (Join-Path $root $d) (Join-Path $top $d) -Recurse }
foreach ($f in 'build.ps1', 'LICENSE', 'README.md', 'CHANGELOG.md', 'DONATE.md', '.gitignore', '.gitattributes') { Copy-Item (Join-Path $root $f) (Join-Path $top $f) }
Remove-Item (Join-Path $top 'tools\ntfy-docker\.env') -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem
# The source (GPL) goes next to the release folder, not into it: the release holds only what a user runs.
$srcDir = Join-Path $root 'Source'
New-Item -ItemType Directory -Force $srcDir | Out-Null
Get-ChildItem $srcDir -Filter *.zip | Remove-Item -Force
[IO.Compression.ZipFile]::CreateFromDirectory($stage, (Join-Path $srcDir "ClaudeCodeTrafficLight-$ver-source.zip"))
Remove-Item $stage -Recurse -Force

$lines = Get-ChildItem $rel -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
    ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name }
[IO.File]::WriteAllLines((Join-Path $rel 'SHA256SUMS.txt'), $lines, (New-Object Text.UTF8Encoding $false))

# The kit for checking the look on another computer (the program and the self-check starter) follows every build.
$kit = Join-Path $root 'PC_Self_Check'
if (Test-Path $kit) {
    Copy-Item $exe (Join-Path $kit 'ClaudeCodeTrafficLight.exe') -Force
    Copy-Item (Join-Path $PSScriptRoot 'selfcheck.cmd') (Join-Path $kit 'selfcheck.cmd') -Force
}

if (-not $NoStart -and $wasRunning) { Start-Process $exe }
Get-ChildItem $rel -Recurse -File | Select-Object @{ n = 'file'; e = { $_.FullName.Replace("$rel\", '') } }, @{ n = 'KB'; e = { [math]::Round($_.Length / 1KB, 1) } } | Format-Table -AutoSize | Out-String | Write-Host
'exe identical to bin: ' + ((Get-FileHash $exe).Hash -eq (Get-FileHash (Join-Path $rel 'ClaudeCodeTrafficLight.exe')).Hash)
