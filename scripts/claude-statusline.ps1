# RedLine Claude usage feed, PowerShell twin of claude-statusline.sh.
#
# Claude Code hands its statusline command a JSON payload on stdin, and that payload already
# carries the rate-limit windows RedLine shows. This writes just those to a sidecar file.
# No token, no credential store, no network, nothing undocumented.
#
# Composes rather than replaces: whatever statusline command was already configured is carried
# in REDLINE_STATUSLINE_CHAIN and still receives the same stdin and still draws the line.
#
# Privacy: only the rate-limit block is written. The cwd, session id, transcript path and cost
# figures in the payload are read and discarded. See SECURITY.md.

# The statusline must draw even when the sidecar cannot be written, so failures here are
# swallowed deliberately. A missing usage figure in a menu bar is not worth a broken prompt.
$ErrorActionPreference = "Continue"

$payload = [Console]::In.ReadToEnd()

# Resolves to the same place AppPaths.data does, or the two are writing past each other
function Get-RedlineDataDir {
    if ($env:REDLINE_HOME -and (Test-Path -LiteralPath $env:REDLINE_HOME)) {
        return (Join-Path $env:REDLINE_HOME ".local\share\redline")
    }
    if ($env:LOCALAPPDATA) { return (Join-Path $env:LOCALAPPDATA "RedLine") }
    return (Join-Path $HOME ".local\share\redline")
}

$out = $env:REDLINE_CLAUDE_USAGE
if (-not $out) { $out = Join-Path (Get-RedlineDataDir) "claude-usage.json" }

try {
    $p = $payload | ConvertFrom-Json -ErrorAction Stop

    $r = $null
    if ($p -and $p.PSObject.Properties.Name -contains "rate_limits") { $r = $p.rate_limits }

    $fiveHour = $null
    $sevenDay = $null
    $modelScoped = $null
    if ($r) {
        $names = $r.PSObject.Properties.Name
        if ($names -contains "five_hour")    { $fiveHour    = $r.five_hour }
        if ($names -contains "seven_day")    { $sevenDay    = $r.seven_day }
        if ($names -contains "model_scoped") { $modelScoped = $r.model_scoped }
    }
    if ($modelScoped -and @($modelScoped).Count -eq 0) { $modelScoped = $null }

    # Claude Code also sends rate_limits with every window null, on draws that made no API
    # call. Only a payload carrying at least one window may be written; nulls would erase a
    # real reading.
    if ($fiveHour -or $sevenDay -or $modelScoped) {
        $record = [ordered]@{}
        $record["updated_at"]   = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
        $record["five_hour"]    = $fiveHour
        $record["seven_day"]    = $sevenDay
        $record["model_scoped"] = $modelScoped

        $dir = Split-Path -Parent $out
        New-Item -ItemType Directory -Path $dir -Force -ErrorAction Stop | Out-Null
        # Written beside the target and moved, so a reader never sees half a file
        $tmp = "$out.$PID.tmp"
        $json = ConvertTo-Json -InputObject $record -Depth 20 -Compress
        [System.IO.File]::WriteAllText($tmp, $json)
        Move-Item -LiteralPath $tmp -Destination $out -Force -ErrorAction Stop
    }
} catch {
    # Deliberately silent, see above
}

# The chained command owns the visible line. It gets the untouched payload, exactly as Claude
# Code sent it, so it cannot tell this wrapper is in front of it.
if ($env:REDLINE_STATUSLINE_CHAIN) {
    $payload | & pwsh -NoProfile -Command $env:REDLINE_STATUSLINE_CHAIN
}
