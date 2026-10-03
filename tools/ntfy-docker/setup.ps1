# Sets up (or repairs) the own ntfy server in Docker for Claude Code Traffic Light. Safe to run again.
#   .\setup.ps1                      server on this computer's address in the local network, user "phone"
#   .\setup.ps1 -BaseUrl https://ntfy.example.org -Port 8080   behind your own HTTPS front
#   .\setup.ps1 -User anna -Token    another user name, and also an access token for the program
# What it does: creates the data volume (taking over the data of the old test container if there is one), writes
# .env, starts the container, waits until it is healthy, creates the user when it does not exist yet and sets the
# access rules the program needs:  ccl-* read-write for the user, ccr-* read-write for the user and write-only for
# everyone else.
param(
    [string]$User = 'phone',
    [string]$Port = '18081',
    [string]$BaseUrl = '',
    [switch]$Token
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$container = 'claude-ntfy'
$volume = 'claude-ntfy-data'
$oldVolume = 'tl-ntfy-data'
$oldContainer = 'tl-ntfy-test'

# docker writes progress (pulling an image) to stderr, which PowerShell 5.1 would treat as an error
function Invoke-Docker {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $output = & docker @args 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = $saved
    if ($code -ne 0) { throw "docker $($args -join ' ') failed: $($output -join ' ')" }
    $output
}

# --- the address the phone will use
if (-not $BaseUrl) {
    $ip = Get-NetIPAddress -AddressFamily IPv4 |
        Where-Object { $_.IPAddress -match '^(10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)' -and $_.InterfaceAlias -notmatch 'vEthernet|WSL|Docker|Loopback|VirtualBox|VMware' } |
        Select-Object -First 1
    if (-not $ip) { throw 'No address in the local network found: pass -BaseUrl http://<address>:<port>' }
    $BaseUrl = "http://$($ip.IPAddress):$Port"
}
Set-Content .env -Value @("NTFY_BASE_URL=$BaseUrl", "NTFY_PORT=$Port") -Encoding ascii

# --- the data volume; the old test server's data (user, messages) is copied once
$haveVolume = (& docker volume ls --format '{{.Name}}') -contains $volume
if (-not $haveVolume) {
    Invoke-Docker volume create $volume | Out-Null
    if ((& docker volume ls --format '{{.Name}}') -contains $oldVolume) {
        Write-Host "Copying the data of the old test server ($oldVolume) into $volume ..."
        Invoke-Docker run --rm --entrypoint sh -v "${oldVolume}:/from" -v "${volume}:/to" binwiederhier/ntfy -c 'cp -a /from/. /to/' | Out-Null
    }
}
# the old test container held the same port
if ((& docker ps -a --format '{{.Names}}') -contains $oldContainer) {
    Write-Host "Stopping the old test container $oldContainer (its data volume stays) ..."
    Invoke-Docker rm -f $oldContainer | Out-Null
}

# --- start and wait
Invoke-Docker compose up -d | Out-Null
Write-Host 'Waiting for the server ...'
$healthy = $false
for ($i = 0; $i -lt 60 -and -not $healthy; $i++) {
    Start-Sleep 1
    $state = (& docker inspect $container --format '{{.State.Health.Status}}' 2>$null)
    $healthy = ($state -eq 'healthy')
}
if (-not $healthy) { throw "The server did not become healthy: docker logs $container" }

# --- the user and the access rules
$users = (& docker exec $container ntfy user list 2>&1) -join "`n"
$password = $null
if ($users -notmatch "(?m)^user $([regex]::Escape($User)) ") {
    $chars = (48..57) + (65..90) + (97..122)
    $password = -join ($chars | Get-Random -Count 20 | ForEach-Object { [char]$_ })
    Invoke-Docker exec -e "NTFY_PASSWORD=$password" $container ntfy user add --role=user $User | Out-Null
}
Invoke-Docker exec $container ntfy access $User 'ccl-*' read-write | Out-Null
Invoke-Docker exec $container ntfy access $User 'ccr-*' read-write | Out-Null
Invoke-Docker exec $container ntfy access everyone 'ccr-*' write-only | Out-Null

$tokenText = $null
if ($Token) { $tokenText = ((& docker exec $container ntfy token add $User) -join ' ') -replace '.*(tk_[a-z0-9]+).*', '$1' }

Write-Host ''
Write-Host '--- ready ---'
Write-Host "Server:   $BaseUrl"
Write-Host "User:     $User"
if ($password) { Write-Host "Password: $password   (shown only now; change it with: docker exec -it $container ntfy user change-pass $User)" }
else { Write-Host 'Password: the user already existed, its password is unchanged' }
if ($tokenText) { Write-Host "Token:    $tokenText   (for Settings > Phone > password field, leave the user name empty)" }
Write-Host ''
Write-Host 'Program: Settings > Phone > Own ntfy server: address, user, password.'
Write-Host 'Phone:   ntfy app > Settings > Users > add the same address and user; then scan the QR code of the Phone page.'
