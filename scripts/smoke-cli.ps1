# Proves a built command line binary actually works on Windows: it reads a Claude transcript,
# writes the SQLite warehouse, and reads it back. Mirrors scripts/smoke-cli.sh.
param([Parameter(Mandatory = $true)][string]$Bin)

$ErrorActionPreference = "Stop"
if (-not (Test-Path $Bin)) { throw "not found: $Bin" }

$HomeDir = Join-Path $env:TEMP ("redline-smoke-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path (Join-Path $HomeDir ".claude\projects\demo") -Force | Out-Null
$env:REDLINE_HOME = $HomeDir

# Runs the binary and checks the exit code, since the codes are part of the contract
function Invoke-Redline([int]$Want, [string[]]$RedlineArgs) {
    $out = & $Bin @RedlineArgs 2>&1 | Out-String
    if ($LASTEXITCODE -ne $Want) {
        throw "'$($RedlineArgs -join ' ')' exited $LASTEXITCODE, wanted $Want`n$out"
    }
    return $out
}

try {
    Write-Host "version"
    if ((Invoke-Redline 0 @("--version")) -notmatch "redline ") {
        throw "--version printed nothing recognisable"
    }

    Write-Host "help"
    if ((Invoke-Redline 0 @("help")) -notmatch [regex]::Escape("redline <command>")) {
        throw "help lost its usage line"
    }

    Write-Host "empty machine"
    # 30 is "no data", and saying so beats inventing a zero
    Invoke-Redline 30 @("status") | Out-Null

    Write-Host "ingest"
    $ts = (Get-Date).ToUniversalTime().AddMinutes(-30).ToString("yyyy-MM-ddTHH:mm:ss.000Z")
    $line = '{"timestamp":"' + $ts + '","requestId":"req_a","message":{"id":"a","model":"claude-sonnet-5","usage":{"input_tokens":1000,"output_tokens":100,"cache_read_input_tokens":0}}}'
    $session = Join-Path $HomeDir ".claude\projects\demo\session.jsonl"
    [System.IO.File]::WriteAllText($session, $line + "`n")
    if ((Invoke-Redline 0 @("ingest", "--json")) -notmatch '"added" : 1') {
        throw "ingest did not record the transcript"
    }

    Write-Host "the same transcript twice adds nothing"
    if ((Invoke-Redline 0 @("ingest", "--json")) -notmatch '"added" : 0') {
        throw "ingest was not incremental"
    }

    Write-Host "autostart reports without changing anything"
    # Status only: turning it on here would edit what the machine starts at login
    if ((Invoke-Redline 0 @("autostart")) -notmatch ": (on|off)") {
        throw "autostart did not report a state"
    }

    Write-Host "settings can be read and changed, and nonsense refused"
    if ((Invoke-Redline 0 @("config")) -notmatch "limitRedPct") { throw "config did not list the settings" }
    if ((Invoke-Redline 0 @("config", "limitRedPct", "90")) -notmatch "90") { throw "config did not change the value" }
    if ((Invoke-Redline 0 @("config", "limitRedPct")) -notmatch "90") { throw "the change did not stick" }
    # 2 is the refusal, and it has to refuse: the engine would not load this either
    Invoke-Redline 2 @("config", "limitRedPct", "500") | Out-Null

    Write-Host "the usage feed can be wired and unwired"
    if ((Invoke-Redline 20 @("setup")) -notmatch "off") { throw "setup did not report a state" }
    if ((Invoke-Redline 0 @("setup", "claude")) -notmatch "on") { throw "setup claude did not wire the feed" }
    if ((Invoke-Redline 0 @("setup")) -notmatch "on") { throw "setup did not stay wired" }
    if ((Invoke-Redline 0 @("setup", "off")) -notmatch "off") { throw "setup off did not unwire" }

    Write-Host "history reads it back"
    if ((Invoke-Redline 0 @("history")) -notmatch "1.1K") {
        throw "history lost the 1100 tokens"
    }

    Write-Host "OK: $Bin"
}
finally {
    Remove-Item -Recurse -Force $HomeDir -ErrorAction SilentlyContinue
}
