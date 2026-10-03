# The choice between the public ntfy.sh and an own server, read from the compiled program:
# a) settings from before the choice with an own address are taken as "own server", its sign-in is used;
# b) switching to the public server keeps the own address and sign-in, but sends no sign-in to ntfy.sh;
# c) switching back restores the own address;
# d) old settings with the public address and a leftover password: public, and the password is not sent.
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$box = Join-Path $root 'work\tests\server-choice'
if (Test-Path $box) { Remove-Item $box -Recurse -Force }
New-Item -ItemType Directory -Force $box | Out-Null
$env:TRAFFICLIGHT_DATA_DIR = $box
$asm = [Reflection.Assembly]::LoadFrom($exe)
$cfgType = $asm.GetType('Semaphore.Config')
$auth = $asm.GetType('Semaphore.Notifier').GetMethod('Authorization', [Type[]]@($cfgType))
$prot = $asm.GetType('Semaphore.Secret').GetMethod('Protect')
$utf8 = New-Object Text.UTF8Encoding $false
function Load($json) { [IO.File]::WriteAllText("$box\config.json", $json, $utf8); $cfgType.GetMethod('Load').Invoke($null, @()) }
function P($c, $name) { $cfgType.GetProperty($name).GetValue($c) }

$secret = $prot.Invoke($null, @('s3cret'))
$c = Load ('{"NtfyServer":"http://192.168.0.10:8080","NtfyUser":"phone","NtfySecretProtected":"' + $secret + '"}')
"a) own=$(P $c 'NtfyOwn') server=$(P $c 'NtfyServer') remembered=$(P $c 'NtfyOwnServer') sign-in sent=$([bool]$auth.Invoke($null, @($c)))"

$cfgType.GetProperty('NtfyOwn').SetValue($c, $false); $cfgType.GetMethod('ApplyNtfyServer').Invoke($c, @())
"b) own=$(P $c 'NtfyOwn') server=$(P $c 'NtfyServer') remembered=$(P $c 'NtfyOwnServer') user kept=$(P $c 'NtfyUser') sign-in sent=$([bool]$auth.Invoke($null, @($c)))"

$cfgType.GetProperty('NtfyOwn').SetValue($c, $true); $cfgType.GetMethod('ApplyNtfyServer').Invoke($c, @())
"c) own=$(P $c 'NtfyOwn') server=$(P $c 'NtfyServer') sign-in sent=$([bool]$auth.Invoke($null, @($c)))"

$c = Load ('{"NtfyServer":"https://ntfy.sh","NtfyUser":"phone","NtfySecretProtected":"' + $secret + '"}')
"d) own=$(P $c 'NtfyOwn') server=$(P $c 'NtfyServer') sign-in sent=$([bool]$auth.Invoke($null, @($c)))"
$env:TRAFFICLIGHT_DATA_DIR = $null
