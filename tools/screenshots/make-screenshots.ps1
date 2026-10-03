# Takes the documentation screenshots of the real application with demo data (sandbox settings, made-up
# sessions) into <repository>\UI\<language>-<theme>\. Windows are captured with PrintWindow, so whatever
# lies on top of them on the screen does not matter. The installed settings are never touched.
#   .\make-screenshots.ps1                    all 9 sets
#   .\make-screenshots.ps1 -Sets en-dark,ru-light
param(
    [string[]]$Sets = @('en-dark', 'en-light', 'ru-dark', 'ru-light', 'es-dark', 'de-dark', 'fr-dark', 'pt-dark', 'zh-dark'),
    [string]$OutDir
)

$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $OutDir) { $OutDir = Join-Path $root 'UI' }
$work = Join-Path $root 'work\screenshots'
New-Item -ItemType Directory -Force $work, $OutDir | Out-Null
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'

$wasRunning = [bool](Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
Start-Sleep 1
try {
    foreach ($set in $Sets) {
        $lang, $theme = $set -split '-'
        powershell -STA -NoProfile -File (Join-Path $PSScriptRoot 'shots.ps1') $work $OutDir $lang $theme
        powershell -STA -NoProfile -File (Join-Path $PSScriptRoot 'dialogs.ps1') $work $OutDir $lang $theme
    }
}
finally {
    if ($wasRunning) { Start-Process $exe }
}
