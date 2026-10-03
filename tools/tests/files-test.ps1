# Damaged files must not cost the settings or the history:
# 1) a cut-off config.json next to a whole config.json.bak: the settings come from the backup, the log says so;
#    a later save writes a whole config.json and keeps the good backup;
# 2) a log grown past 512 KB keeps its newer half instead of starting empty.
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$box = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'work\tests\files'
Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
if (Test-Path $box) { Remove-Item $box -Recurse -Force }
New-Item -ItemType Directory -Force "$box\data", "$box\claude" | Out-Null
$utf8 = New-Object Text.UTF8Encoding $false
$good = '{"FirstRunDone":true,"Language":"de","StartupNotice":false,"NtfyServer":"http://from-backup.example"}'
[IO.File]::WriteAllText("$box\data\config.json.bak", $good, $utf8)
[IO.File]::WriteAllText("$box\data\config.json", '{"FirstRunDone":true,"Language":"de","Ntfy', $utf8)
$lines = New-Object Collections.Generic.List[string]
for ($i = 0; $i -lt 9000; $i++) { $lines.Add(("2000-01-01 00:00:00 old line {0:D5} " -f $i) + ('x' * 40)) }
[IO.File]::WriteAllLines("$box\data\trafficlight.log", $lines, $utf8)
"log before: {0} KB" -f [int]((Get-Item "$box\data\trafficlight.log").Length / 1KB)
$env:TRAFFICLIGHT_DATA_DIR = "$box\data"
$env:TRAFFICLIGHT_CLAUDE_DIR = "$box\claude"
$app = Start-Process $exe -PassThru
try {
    Start-Sleep 4
    # a session event makes the program write to the log
    '{"hook_event_name":"UserPromptSubmit","session_id":"files-1111","cwd":"C:/fake/Proj","prompt":"x"}' | & $exe --hook trafficlight | Out-Null
    Start-Sleep 1
    $log = Get-Content "$box\data\trafficlight.log" -Encoding UTF8
    "log after: {0} KB, first line: {1}" -f [int]((Get-Item "$box\data\trafficlight.log").Length / 1KB), $log[0].Substring(0, 36)
    "log keeps recent old lines: " + ($log[0] -like '*old line 04*')
    $log | Select-String 'config' | ForEach-Object { "log: " + $_.Line }
}
finally {
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
}
# A save after recovery: the settings window is not needed, the program saves through Config.Save.
$asm = [Reflection.Assembly]::LoadFrom($exe)
$cfgType = $asm.GetType("Semaphore.Config")
$cfg = $cfgType.GetMethod("Load").Invoke($null, @())
"loaded language from backup: " + $cfgType.GetProperty("Language").GetValue($cfg)
$cfgType.GetMethod("Save").Invoke($cfg, @())
$main = Get-Content "$box\data\config.json" -Raw
$bak = Get-Content "$box\data\config.json.bak" -Raw
"config.json whole after save: " + ($main -like '*from-backup.example*')
"backup still the good one: " + ($bak -eq $good)
"no temp file left: " + (-not (Test-Path "$box\data\config.json.tmp"))
# and an ordinary save over a whole file moves the old one to the backup
$cfgType.GetProperty("Language").SetValue($cfg, "fr")
$cfgType.GetMethod("Save").Invoke($cfg, @())
"second save: main has fr=" + ((Get-Content "$box\data\config.json" -Raw) -like '*"fr"*') + ", backup has de=" + ((Get-Content "$box\data\config.json.bak" -Raw) -like '*"de"*')
$env:TRAFFICLIGHT_DATA_DIR = $null
$env:TRAFFICLIGHT_CLAUDE_DIR = $null
