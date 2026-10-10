# Sends made-up pushes to a throwaway topic on ntfy.sh, in the format the program uses, for screenshots of the phone
# for the project page: a finished task, a question and a permission prompt (newest on top in the ntfy app), each
# ending with the line of the usage limits. Nothing real is in them: the project is "demo-app".
# Subscribe to the topic on the phone first, then run this.
param([Parameter(Mandatory = $true)][string]$Topic)
$ErrorActionPreference = 'Stop'
$server = 'https://ntfy.sh'
# The buttons post here; nobody listens, a tap does nothing.
$reply = "$server/$Topic-reply"
# The limits line as the program writes it (English): the 5-hour window resets in about two hours.
$reset = (Get-Date).AddHours(2).AddMinutes(-(Get-Date).Minute % 10).ToString('HH:mm')
function Footer($five, $week) { "`n`n📊 Limits: 5 h ≈$five%, resets at $reset · week ≈$week%" }

function Push($title, $message, $urgent, $actions) {
    $p = [ordered]@{ topic = $Topic; title = $title; message = $message; priority = $(if ($urgent) { 4 } else { 3 });
        tags = @($(if ($urgent) { 'red_circle' } else { 'green_circle' })) }
    if ($actions) { $p.actions = $actions }
    $json = $p | ConvertTo-Json -Depth 5
    Invoke-RestMethod $server -Method Post -Body ([Text.Encoding]::UTF8.GetBytes($json)) -ContentType 'application/json; charset=utf-8' | Out-Null
    "sent: $title"
    Start-Sleep 2
}
function Button($label, $body) { [ordered]@{ action = 'http'; label = $label; url = $reply; method = 'POST'; body = $body; clear = $true } }

Push 'Claude Code finished' ('demo-app: finished in 12 min' + (Footer 31 46)) $false $null
Push 'Claude asks a question' ("demo-app`nWhich test runner should I set up?`n`n1) Vitest — fast, works with Vite`n2) Jest — the most widely used`n`n" +
    'A different answer cannot be given here: use the Claude window on the computer.' + (Footer 34 47)) $true @(
    (Button '1) Vitest' 'pick:demo:0'), (Button '2) Jest' 'pick:demo:1'))
Push 'Claude Code asks permission' ("demo-app`nBash: npm test`n`n" + '"Always" adds this Claude Code rule:' + "`nBash(npm test:*) — in this project (only you)" + (Footer 38 48)) $true @(
    (Button 'Allow' 'allow:demo'), (Button 'Always' 'always:demo'), (Button 'Deny' 'deny:demo'))
