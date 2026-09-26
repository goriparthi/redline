// Login item as an HKCU Run value, the Windows counterpart of the macOS LaunchAgent plist.
// Per user, no elevation, and nothing is started now: this process already owns the tray icon.
using System.IO;
using Microsoft.Win32;
using Redline.Core;

namespace Redline.App.Services;

/// The one registry key this touches, behind an interface so tests never write the real one.
public interface IRunKey
{
    string? Get(string name);
    void Set(string name, string value);
    void Delete(string name);
}

public sealed class RegistryRunKey : IRunKey
{
    public const string SubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static readonly RegistryRunKey Shared = new();

    public string? Get(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(SubKey, writable: false);
        return key?.GetValue(name) as string;
    }

    public void Set(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SubKey, writable: true);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void Delete(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(SubKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

public sealed class MemoryRunKey : IRunKey
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Get(string name) => Values.TryGetValue(name, out var v) ? v : null;
    public void Set(string name, string value) => Values[name] = value;
    public void Delete(string name) => Values.Remove(name);
}

public sealed class LaunchAtLogin
{
    public const string ValueName = "RedLine";

    private readonly IRunKey _key;
    private readonly string _exePath;

    public LaunchAtLogin(IRunKey? key = null, string? exePath = null)
    {
        _key = key ?? RegistryRunKey.Shared;
        _exePath = exePath ?? Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "RedLine.exe");
    }

    public string ExePath => _exePath;

    /// The command Windows runs at login: the exe, quoted so a path with spaces survives.
    public string Command => "\"" + _exePath + "\"";

    /// True when a RedLine value exists at all, like the plist check on macOS.
    public bool IsEnabled
    {
        get
        {
            try { return !string.IsNullOrEmpty(_key.Get(ValueName)); }
            catch { return false; }
        }
    }

    /// True when the value launches this very exe, rather than an older copy somewhere else.
    public bool PointsHere
    {
        get
        {
            string? raw;
            try { raw = _key.Get(ValueName); } catch { return false; }
            if (string.IsNullOrEmpty(raw)) return false;
            var path = raw.Trim();
            if (path.StartsWith('"'))
            {
                var close = path.IndexOf('"', 1);
                path = close > 0 ? path[1..close] : path[1..];
            }
            try
            {
                return string.Equals(Path.GetFullPath(path), Path.GetFullPath(_exePath), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    /// Without this value the tray icon does not come back after a restart, so a failure is logged.
    public bool Enable()
    {
        try
        {
            _key.Set(ValueName, Command);
            return true;
        }
        catch (Exception e)
        {
            Diag.Log.Error("login.enable_failed", "could not write the Run value", new() { ["error"] = e.Message });
            return false;
        }
    }

    public bool Disable()
    {
        try
        {
            _key.Delete(ValueName);
            return true;
        }
        catch (Exception e)
        {
            Diag.Log.Error("login.disable_failed", "could not remove the Run value", new() { ["error"] = e.Message });
            return false;
        }
    }
}
