# RedLine ollama shim, PowerShell twin of ollama-shim.sh. A transparent stand-in for the
# ollama CLI that records token counts, which Ollama does not persist.
#
# Counted here, over the local API:
#   ollama run MODEL              (prompt piped on stdin)
#   ollama run MODEL "prompt"     (prompt as a single argument)
# Everything else passes through to the real binary untouched: every other subcommand, any
# `run` with flags or several prompt arguments, and interactive chat. If the API call fails
# the prompt is replayed through the real binary, so the worst case is an uncounted call,
# never a broken one.
#
# Privacy: only counts and timings reach the log, never the prompt. See SECURITY.md.
$ErrorActionPreference = "Stop"

$forwarded = $args

function Get-RedlineDataDir {
    if ($env:REDLINE_DATA_DIR) { return $env:REDLINE_DATA_DIR }
    if ($env:REDLINE_HOME -and (Test-Path -LiteralPath $env:REDLINE_HOME)) {
        return (Join-Path $env:REDLINE_HOME ".local\share\redline")
    }
    if ($env:LOCALAPPDATA) { return (Join-Path $env:LOCALAPPDATA "RedLine") }
    return (Join-Path $HOME ".local\share\redline")
}

# The real binary: explicit override first, then PATH minus this file and any copy of it
function Find-RealOllama {
    if ($env:REDLINE_OLLAMA_BIN) { return $env:REDLINE_OLLAMA_BIN }
    $self = $PSCommandPath
    foreach ($dir in ($env:PATH -split [System.IO.Path]::PathSeparator)) {
        if (-not $dir) { continue }
        foreach ($leaf in @("ollama.exe", "ollama.cmd", "ollama")) {
            $candidate = Join-Path $dir $leaf
            if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
            if ($self -and ((Resolve-Path $candidate).Path -eq (Resolve-Path $self).Path)) { continue }
            # Skip a copy of this shim, which would otherwise recurse into itself
            try {
                $head = Get-Content -LiteralPath $candidate -TotalCount 5 -ErrorAction Stop
                if ($head -match "RedLine ollama shim") { continue }
            } catch { }
            return $candidate
        }
    }
    return $null
}

$real = Find-RealOllama
if (-not $real) {
    [Console]::Error.WriteLine("redline ollama shim: no ollama binary found; install Ollama or set REDLINE_OLLAMA_BIN")
    exit 127
}

function Invoke-Real {
    param([string[]]$RealArgs, [string]$StdinText)
    if ($null -ne $StdinText) { $StdinText | & $real @RealArgs } else { & $real @RealArgs }
    exit $LASTEXITCODE
}

# Decide whether this exact invocation is one of the two counted shapes
$intercept = $null
if ($forwarded.Count -ge 2 -and $forwarded[0] -eq "run" -and
    $forwarded[1] -and -not $forwarded[1].StartsWith("-")) {
    if ($forwarded.Count -eq 2 -and [Console]::IsInputRedirected) {
        $intercept = "stdin"
    } elseif ($forwarded.Count -eq 3 -and -not $forwarded[2].StartsWith("-")) {
        $intercept = "arg"
    }
}
if (-not $intercept) { Invoke-Real -RealArgs $forwarded -StdinText $null }

$model = $forwarded[1]
if ($intercept -eq "arg") { $prompt = $forwarded[2] } else { $prompt = [Console]::In.ReadToEnd() }

# Nothing to send is not an error; hand the empty call to the real CLI to answer as it would
if (-not $prompt) { Invoke-Real -RealArgs $forwarded -StdinText "" }

$hostUrl = $env:OLLAMA_HOST
if (-not $hostUrl) { $hostUrl = "http://127.0.0.1:11434" }
if ($hostUrl -notmatch "://") { $hostUrl = "http://$hostUrl" }
$timeout = 600
if ($env:OLLAMA_TIMEOUT) { $timeout = [int]$env:OLLAMA_TIMEOUT }

# stream:false so the response carries final token counts in one object
$body = ConvertTo-Json -InputObject @{ model = $model; prompt = $prompt; stream = $false } -Depth 5 -Compress
try {
    $resp = Invoke-RestMethod -Method Post -Uri "$hostUrl/api/generate" `
        -ContentType "application/json" -Body $body -TimeoutSec $timeout -ErrorAction Stop
} catch {
    # The API refused; replay through the real binary so the call still succeeds
    Invoke-Real -RealArgs @("run", $model) -StdinText $prompt
}

$text = ""
if ($resp.PSObject.Properties.Name -contains "response") { $text = [string]$resp.response }
if ($text -and -not $text.EndsWith("`n")) { $text += "`n" }
[Console]::Out.Write($text)

function Field($obj, $name, $fallback) {
    if ($obj.PSObject.Properties.Name -contains $name -and $null -ne $obj.$name) { return $obj.$name }
    return $fallback
}

# Durations are nanoseconds. Missing counts mean a cached or empty eval, so default to 0.
$record = [ordered]@{}
$record["ts"]                = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
$record["model"]             = Field $resp "model" $model
$record["prompt_eval_count"] = Field $resp "prompt_eval_count" 0
$record["eval_count"]        = Field $resp "eval_count" 0
$record["total_duration_ms"] = [math]::Round(([double](Field $resp "total_duration" 0)) / 1e6)
$record["load_duration_ms"]  = [math]::Round(([double](Field $resp "load_duration" 0)) / 1e6)
$record["done_reason"]       = Field $resp "done_reason" $null

try {
    $logDir = Get-RedlineDataDir
    New-Item -ItemType Directory -Path $logDir -Force -ErrorAction Stop | Out-Null
    $line = ConvertTo-Json -InputObject $record -Depth 5 -Compress
    Add-Content -LiteralPath (Join-Path $logDir "ollama.jsonl") -Value $line -Encoding utf8
} catch {
    # A call that ran but went uncounted is better than a call that failed
}
