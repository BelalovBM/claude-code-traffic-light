# Builds ClaudeCodeTrafficLight.exe (the same sources give the same file: /deterministic) with the Roslyn compiler from the installed .NET SDK,
# targeting the .NET Framework 4.8 that ships with Windows (no runtime needed to run the result).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $out | Out-Null

# The newest SDK installed, or the one named by TRAFFICLIGHT_SDK: a different compiler version can give a different
# file, so the release build names the version the author builds with.
$sdk = Get-ChildItem "$env:ProgramFiles\dotnet\sdk" -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' -and (-not $env:TRAFFICLIGHT_SDK -or $_.Name -eq $env:TRAFFICLIGHT_SDK) } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $sdk) { throw '.NET SDK not found (only needed to build from source).' }
$csc = Join-Path $sdk.FullName 'Roslyn\bincore\csc.dll'
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'

$refs = 'mscorlib', 'System', 'System.Core', 'System.Drawing', 'System.Windows.Forms', 'System.Web.Extensions', 'System.Security' |
    ForEach-Object { "/r:$fw\$_.dll" }
# Windows notifications with a button (Toasts.cs) are reached through the Windows Runtime: the metadata files
# ship with Windows 10 and later, and are only needed to compile.
$wm = Join-Path $env:WINDIR 'System32\WinMetadata'
$refs += 'Windows.UI', 'Windows.Data', 'Windows.Foundation' | ForEach-Object { "/r:$wm\$_.winmd" }
$refs += "/r:$fw\System.Runtime.WindowsRuntime.dll", "/r:$fw\System.Runtime.InteropServices.WindowsRuntime.dll", "/r:$fw\System.Runtime.dll"
$res = Get-ChildItem (Join-Path $root 'lang') -Filter *.txt |
    ForEach-Object { "/resource:$($_.FullName),Lang.$($_.BaseName).txt" }
$src = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }

$exe = Join-Path $out 'ClaudeCodeTrafficLight.exe'
& dotnet $csc /nologo /noconfig /nostdlib+ /target:winexe /platform:anycpu /optimize+ /langversion:latest /deterministic+ "/pathmap:$root=C:\src" `
    "/win32manifest:$root\src\app.manifest" "/win32icon:$root\src\app.ico" "/out:$exe" @refs @res @src
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
Write-Host "Built $exe"
