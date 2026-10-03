$exeForTest = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'bin\ClaudeCodeTrafficLight.exe'
Add-Type -AssemblyName System.Drawing,System.Windows.Forms
$root=$args[0]
Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$root\claude","$root\data" | Out-Null
$env:TRAFFICLIGHT_DATA_DIR="$root\data"; $env:TRAFFICLIGHT_CLAUDE_DIR="$root\claude"
'{"permissions":{"allow":["Bash(ls)"]},"hooks":{"PermissionRequest":[{"hooks":[{"type":"command","command":"echo foreign-permission-hook"}]}]}}' | Set-Content "$root\claude\settings.json" -Encoding utf8
$asm=[Reflection.Assembly]::LoadFrom("$exeForTest")
$hi=$asm.GetType("Semaphore.HookInstaller")
function Show($label){
  $j=Get-Content "$root\claude\settings.json" -Raw | ConvertFrom-Json
  $pr=@($j.hooks.PermissionRequest | ForEach-Object { $_.hooks } | ForEach-Object { "{0} (timeout {1})" -f ($_.command -replace '.*/',''), $_.timeout })
  $evs=($j.hooks | Get-Member -MemberType NoteProperty).Name -join ","
  "{0}: status={1}; events=[{2}]; PermissionRequest hooks=[{3}]" -f $label,$hi.GetMethod("GetStatus").Invoke($null,@()),$evs,($pr -join " ; ")
}
$hi.GetMethod("Configure").Invoke($null,@($false,90)); $hi.GetMethod("Install").Invoke($null,@()); Show "feature OFF, installed"
$hi.GetMethod("Configure").Invoke($null,@($true,330)); "after switching ON (before reinstall): status=" + $hi.GetMethod("GetStatus").Invoke($null,@())
$hi.GetMethod("Install").Invoke($null,@()); Show "feature ON, installed"
$hi.GetMethod("Configure").Invoke($null,@($false,330)); "after switching OFF (before reinstall): status=" + $hi.GetMethod("GetStatus").Invoke($null,@())
$hi.GetMethod("Install").Invoke($null,@()); Show "feature OFF again"
$hi.GetMethod("Configure").Invoke($null,@($true,330)); $hi.GetMethod("Install").Invoke($null,@()); $hi.GetMethod("Remove").Invoke($null,@())
$j=Get-Content "$root\claude\settings.json" -Raw | ConvertFrom-Json
"after Remove: our hooks left=" + ((Get-Content "$root\claude\settings.json" -Raw) -match 'trafficlight') + "; foreign PermissionRequest hook kept=" + ((@($j.hooks.PermissionRequest | ForEach-Object { $_.hooks } | ForEach-Object { $_.command }) -contains 'echo foreign-permission-hook'))
