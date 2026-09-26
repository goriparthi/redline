// Asks Claude Code to renew its own token with `claude auth status` rather than spending its
// refresh token. The caller compares expiry before and after, so ineffectiveness is measured.
using System.IO;
using System.Diagnostics;
using Redline.Core;

namespace Redline.App.Services;

public abstract record DelegatedRefreshOutcome
{
    public sealed record Ran : DelegatedRefreshOutcome;
    public sealed record CliUnavailable : DelegatedRefreshOutcome;
    public sealed record SkippedByCooldown : DelegatedRefreshOutcome;
    public sealed record Failed(string Reason) : DelegatedRefreshOutcome;
}

/// Runs a resolved `claude` binary with arguments; returns the exit code, or null on timeout.
public delegate int? ClaudeProcessRunner(string binary, IReadOnlyList<string> arguments, TimeSpan timeout);

public sealed class DelegatedRefresh
{
    public static readonly DelegatedRefresh Shared = new();

    /// Long enough for a cold node start, short enough that a hung CLI cannot wedge the poll.
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(25);
    public const double Cooldown = 300;

    private readonly object _lock = new();
    private DateTimeOffset? _lastAttempt;
    private bool _running;
    private readonly Func<string?> _binary;
    private readonly ClaudeProcessRunner _runner;

    public DelegatedRefresh(Func<string?>? binary = null, ClaudeProcessRunner? runner = null)
    {
        _binary = binary ?? (() => Binary());
        _runner = runner ?? Run;
    }

    /// Null when no `claude` is installed. The native installer's location first, then PATH,
    /// because a login-started app inherits whatever PATH Explorer had.
    public static string? Binary(string? home = null, string? pathVariable = null)
    {
        var root = home ?? RedlineHome.Url;
        var candidates = new List<string>
        {
            RedlineHome.Join(root, ".local/bin/claude.exe"),
            RedlineHome.Join(root, ".claude/local/claude.exe"),
        };
        var npm = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (npm.Length > 0) candidates.Add(Path.Combine(npm, "npm", "claude.cmd"));
        foreach (var dir in (pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "claude.exe", "claude.cmd" })
            {
                try { candidates.Add(Path.Combine(dir.Trim().Trim('"'), name)); } catch { }
            }
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    /// Single-flight and cooldown-gated: a stale token makes every poll want to refresh, and
    /// without both guards that becomes one CLI process per poll, forever.
    public DelegatedRefreshOutcome Attempt(DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        string? bin;
        lock (_lock)
        {
            if (_running) return new DelegatedRefreshOutcome.SkippedByCooldown();
            if (_lastAttempt is { } last && (t - last).TotalSeconds < Cooldown)
                return new DelegatedRefreshOutcome.SkippedByCooldown();
            bin = _binary();
            if (bin is null) return new DelegatedRefreshOutcome.CliUnavailable();
            _running = true;
            _lastAttempt = t;
        }
        try
        {
            int? code;
            try { code = _runner(bin, new[] { "auth", "status", "--json" }, Timeout); }
            catch (Exception e) { return new DelegatedRefreshOutcome.Failed($"could not launch {bin}: {e.Message}"); }
            if (code is null) return new DelegatedRefreshOutcome.Failed("timed out");
            return code == 0 ? new DelegatedRefreshOutcome.Ran() : new DelegatedRefreshOutcome.Failed($"exit {code}");
        }
        finally
        {
            lock (_lock) _running = false;
        }
    }

    /// A manual Reconnect should never be answered with "not yet".
    public void ResetCooldown()
    {
        lock (_lock) _lastAttempt = null;
    }

    /// No window, no inherited stdin, and a .cmd shim goes through cmd.exe explicitly.
    public static int? Run(string binary, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var isScript = binary.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                       binary.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        var psi = isScript
            ? new ProcessStartInfo("cmd.exe") { Arguments = "/d /s /c \"\"" + binary + "\" " + string.Join(" ", arguments) + "\"" }
            : new ProcessStartInfo(binary);
        if (!isScript) foreach (var a in arguments) psi.ArgumentList.Add(a);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("process did not start");
        p.StandardInput.Close();
        // Drained so a chatty CLI cannot block on a full pipe
        _ = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return null;
        }
        return p.ExitCode;
    }
}
