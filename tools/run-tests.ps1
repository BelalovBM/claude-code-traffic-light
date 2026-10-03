# Builds the unit tests (tests\*.cs) against the program in bin\ and runs them; the exit code is the number of
# failed checks. Run after build.ps1 (or make-release.ps1); the GitHub build runs it on every tag.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$program = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
if (-not (Test-Path $program)) { throw 'Build the program first (build.ps1).' }
$out = Join-Path $root 'work\unit-tests'
New-Item -ItemType Directory -Force $out | Out-Null
# The tests load the program as a library, so it sits next to them.
Copy-Item $program (Join-Path $out 'ClaudeCodeTrafficLight.exe') -Force

$sdk = Get-ChildItem "$env:ProgramFiles\dotnet\sdk" -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' -and (-not $env:TRAFFICLIGHT_SDK -or $_.Name -eq $env:TRAFFICLIGHT_SDK) } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $sdk) { throw '.NET SDK not found.' }
$csc = Join-Path $sdk.FullName 'Roslyn\bincore\csc.dll'
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs = 'mscorlib', 'System', 'System.Core', 'System.Drawing', 'System.Windows.Forms', 'System.Web.Extensions', 'System.Security' |
    ForEach-Object { "/r:$fw\$_.dll" }
$refs += "/r:$out\ClaudeCodeTrafficLight.exe"
$tests = Join-Path $out 'ClaudeCodeTrafficLight.Tests.exe'
$src = @(Get-ChildItem (Join-Path $root 'tests') -Filter *.cs | ForEach-Object { $_.FullName })
& dotnet $csc /nologo /noconfig /nostdlib+ /target:exe /platform:anycpu /langversion:latest "/out:$tests" @refs @src
if ($LASTEXITCODE -ne 0) { throw "Tests did not build ($LASTEXITCODE)" }

& $tests $root
$failed = $LASTEXITCODE
if ($failed -ne 0) { Write-Host "$failed check(s) failed" -ForegroundColor Red }
exit $failed
