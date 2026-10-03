$exeForTest = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'bin\ClaudeCodeTrafficLight.exe'
Add-Type -AssemblyName System.Drawing,System.Windows.Forms
$root=$args[0]
Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$root\claude","$root\data" | Out-Null
$env:TRAFFICLIGHT_DATA_DIR="$root\data"; $env:TRAFFICLIGHT_CLAUDE_DIR="$root\claude"
'{"permissions":{"allow":["Bash(ls)"]},"hooks":{"Stop":[{"hooks":[{"type":"command","command":"echo foreign"}]}]}}' | Set-Content "$root\claude\settings.json" -Encoding utf8
"cfg" | Set-Content "$root\data\config.json"
$asm=[Reflection.Assembly]::LoadFrom("$exeForTest")
$hi=$asm.GetType("Semaphore.HookInstaller"); $un=$asm.GetType("Semaphore.Uninstaller"); $log=$asm.GetType("Semaphore.Log")
$hi.GetMethod("Install").Invoke($null,@())
"after install: status=" + $hi.GetMethod("GetStatus").Invoke($null,@()) + ", Stop groups=" + ((Get-Content "$root\claude\settings.json" -Raw | ConvertFrom-Json).hooks.Stop).Count
$err=$un.GetMethod("Run").Invoke($null,@())
"uninstall result: [$err]"
$j=Get-Content "$root\claude\settings.json" -Raw | ConvertFrom-Json
"after uninstall: has permissions=" + [bool]$j.permissions + ", foreign Stop hook kept=" + ((($j.hooks.Stop | ForEach-Object { $_.hooks } | ForEach-Object { $_.command }) -contains "echo foreign")) + ", our hooks left=" + ((Get-Content "$root\claude\settings.json" -Raw) -match 'trafficlight')
"own files left in the program folder: " + @(Get-ChildItem "$root\data" -File).Count
$log.GetMethod("Write").Invoke($null,@("should not be written"))
"log written again after removal: " + (Test-Path "$root\data\trafficlight.log")
"backup kept: " + (Test-Path "$root\claude\settings.json.trafficlight.bak")
