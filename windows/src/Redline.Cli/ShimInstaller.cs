// Installs %USERPROFILE%\.local\bin\ollama.cmd, which forwards to `redlinectl ollama-shim`, and puts
// that directory at the front of the user PATH. Only ever overwrites or removes its own file.
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace Redline.Cli;

/// The user PATH as stored, unexpanded, plus whether it is REG_EXPAND_SZ. Injected in tests.
public interface IUserPathStore
{
    (string? Value, bool Expandable) ReadUserPath();
    void WriteUserPath(string value, bool expandable);
    /// The machine PATH, which Windows puts ahead of the user one when it builds a process PATH.
    string? ReadMachinePath();
    /// Tells Explorer and new terminals the environment changed (WM_SETTINGCHANGE).
    void Broadcast();
}

public enum ShimInstallStatus { Installed, Updated, Unchanged, ForeignFile, Removed, NotInstalled, Failed }

public sealed record ShimInstallResult(ShimInstallStatus Status, string ShimPath, bool PathChanged,
                                       string? Message = null, string? Warning = null)
{
    public bool Succeeded => Status is ShimInstallStatus.Installed or ShimInstallStatus.Updated
        or ShimInstallStatus.Unchanged or ShimInstallStatus.Removed or ShimInstallStatus.NotInstalled;
}

public static class ShimInstaller
{
    public const string Marker = OllamaShim.Marker;
    public const string FileName = "ollama.cmd";
    /// How the directory is written into a REG_EXPAND_SZ PATH, so it follows a renamed profile.
    public const string PortableEntry = @"%USERPROFILE%\.local\bin";

    public static string BinDir(string? home = null) =>
        Path.Combine(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");

    public static string ShimPath(string? home = null) => Path.Combine(BinDir(home), FileName);

    /// The redlinectl.exe the shim should forward to: this process when it is redlinectl.
    public static string DefaultTarget() =>
        Environment.ProcessPath is { } p && Path.GetFileNameWithoutExtension(p)
            .Equals("redlinectl", StringComparison.OrdinalIgnoreCase)
            ? p : Path.Combine(AppContext.BaseDirectory, "redlinectl.exe");

    /// The file's text. The marker sits on line two, well inside the first 300 bytes.
    public static string Script(string redlinectl) =>
        "@echo off\r\n" +
        $"rem {Marker}: forwards to redlinectl, which counts `ollama run` tokens. Written by RedLine.\r\n" +
        $"\"{redlinectl}\" ollama-shim %*\r\n" +
        "exit /b %ERRORLEVEL%\r\n";

    /// True when the file at `path` is one RedLine wrote.
    public static bool IsOurs(string path) => File.Exists(path) && OllamaShim.CarriesMarker(path);

    public static bool IsInstalled(string? home = null) => IsOurs(ShimPath(home));

    /// Writes or refreshes the shim and fronts the PATH. A file RedLine did not write is left alone.
    public static ShimInstallResult Install(string? redlinectl = null, string? home = null,
                                            IUserPathStore? store = null)
    {
        var dest = ShimPath(home);
        var text = Script(redlinectl ?? DefaultTarget());
        ShimInstallStatus status;
        try
        {
            Directory.CreateDirectory(BinDir(home));
            if (File.Exists(dest))
            {
                if (!IsOurs(dest))
                    return new ShimInstallResult(ShimInstallStatus.ForeignFile, dest, false,
                        $"{dest} exists and is not RedLine's shim, so it was left alone. " +
                        "Remove it yourself if you want the shim there.");
                status = File.ReadAllText(dest) == text ? ShimInstallStatus.Unchanged : ShimInstallStatus.Updated;
            }
            else status = ShimInstallStatus.Installed;
            if (status != ShimInstallStatus.Unchanged)
                File.WriteAllText(dest, text, new UTF8Encoding(false));
        }
        catch (Exception e)
        {
            return new ShimInstallResult(ShimInstallStatus.Failed, dest, false, e.Message);
        }

        store ??= DefaultStore();
        bool changed;
        string? warning;
        try
        {
            changed = FrontPath(BinDir(home), home is null, store);
            warning = MachinePathWarning(store.ReadMachinePath());
        }
        catch (Exception e)
        {
            return new ShimInstallResult(ShimInstallStatus.Failed, dest, false,
                $"The shim was written but the PATH could not be updated: {e.Message}");
        }
        return new ShimInstallResult(status, dest, changed,
            changed ? "New terminals will find the shim first; open ones keep their old PATH." : null,
            warning);
    }

    /// Removes the shim if it is ours and takes its directory back off the user PATH.
    public static ShimInstallResult Uninstall(string? home = null, IUserPathStore? store = null,
                                              bool removeFromPath = true)
    {
        var dest = ShimPath(home);
        ShimInstallStatus status;
        try
        {
            if (!File.Exists(dest)) status = ShimInstallStatus.NotInstalled;
            else if (!IsOurs(dest))
                return new ShimInstallResult(ShimInstallStatus.ForeignFile, dest, false,
                    $"{dest} is not RedLine's shim, so it was left alone.");
            else
            {
                File.Delete(dest);
                status = ShimInstallStatus.Removed;
            }
        }
        catch (Exception e)
        {
            return new ShimInstallResult(ShimInstallStatus.Failed, dest, false, e.Message);
        }
        if (!removeFromPath) return new ShimInstallResult(status, dest, false);

        // Other tools install into ~/.local/bin too, so the entry stays while anything else lives there
        var bin = BinDir(home);
        try
        {
            if (Directory.Exists(bin) && Directory.EnumerateFileSystemEntries(bin).Any())
                return new ShimInstallResult(status, dest, false);
            store ??= DefaultStore();
            var (current, expandable) = store.ReadUserPath();
            var next = WithoutEntry(current, bin);
            if (next is null) return new ShimInstallResult(status, dest, false);
            store.WriteUserPath(next, expandable);
            store.Broadcast();
            return new ShimInstallResult(status, dest, true);
        }
        catch (Exception e)
        {
            return new ShimInstallResult(ShimInstallStatus.Failed, dest, false,
                $"The shim was removed but the PATH could not be updated: {e.Message}");
        }
    }

    /// Puts `bin` first in the user PATH, dropping any later copy. False when it already leads.
    internal static bool FrontPath(string bin, bool portable, IUserPathStore store)
    {
        var (current, expandable) = store.ReadUserPath();
        // No value yet means Windows would create it as REG_EXPAND_SZ, which is what we write
        if (current is null) expandable = true;
        var entry = portable && expandable ? PortableEntry : bin;
        var next = Fronted(current, bin, entry);
        if (next is null) return false;
        store.WriteUserPath(next, expandable);
        store.Broadcast();
        return true;
    }

    /// The PATH with `entry` first and no other spelling of `bin`, or null when nothing changes.
    internal static string? Fronted(string? current, string bin, string entry)
    {
        var parts = Split(current);
        if (parts.Count > 0 && SameDir(parts[0], bin)) return null;
        var kept = parts.Where(p => !SameDir(p, bin));
        return string.Join(";", new[] { entry }.Concat(kept));
    }

    /// The PATH without any spelling of `bin`, or null when it was not there.
    internal static string? WithoutEntry(string? current, string bin)
    {
        var parts = Split(current);
        var kept = parts.Where(p => !SameDir(p, bin)).ToList();
        return kept.Count == parts.Count ? null : string.Join(";", kept);
    }

    private static List<string> Split(string? path) =>
        (path ?? "").Split(';').Where(p => p.Trim().Length > 0).ToList();

    internal static bool SameDir(string entry, string dir)
    {
        static string Clean(string s)
        {
            var e = Environment.ExpandEnvironmentVariables(s.Trim().Trim('"'));
            try { e = Path.GetFullPath(e); } catch { }
            return e.TrimEnd('\\', '/');
        }
        return string.Equals(Clean(entry), Clean(dir), StringComparison.OrdinalIgnoreCase);
    }

    /// The machine PATH comes first in every process, so a real ollama there still wins.
    internal static string? MachinePathWarning(string? machinePath)
    {
        foreach (var dir in Split(machinePath))
        {
            string candidate;
            try { candidate = Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"')), "ollama.exe"); }
            catch { continue; }
            if (File.Exists(candidate))
                return $"{candidate} is on the system PATH, which Windows searches before the user " +
                       "PATH, so plain `ollama` still finds it first. Remove that entry or reinstall " +
                       "Ollama per user for the shim to count calls.";
        }
        return null;
    }

    private static IUserPathStore DefaultStore() =>
        OperatingSystem.IsWindows() ? new RegistryUserPathStore()
            : throw new PlatformNotSupportedException("the user PATH lives in the Windows registry");
}

/// HKCU\Environment\Path, read and written without expanding it, so %VARS% survive a round trip.
[SupportedOSPlatform("windows")]
public sealed class RegistryUserPathStore : IUserPathStore
{
    private const string Key = "Environment";
    private const string MachineKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    public (string? Value, bool Expandable) ReadUserPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        if (key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string value)
            return (null, true);
        return (value, key.GetValueKind("Path") == RegistryValueKind.ExpandString);
    }

    public void WriteUserPath(string value, bool expandable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key, writable: true);
        key.SetValue("Path", value, expandable ? RegistryValueKind.ExpandString : RegistryValueKind.String);
    }

    public string? ReadMachinePath()
    {
        using var key = Registry.LocalMachine.OpenSubKey(MachineKey);
        return key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public void Broadcast() =>
        SendMessageTimeout(HwndBroadcast, WmSettingChange, UIntPtr.Zero, "Environment",
                           SmtoAbortIfHung, 5000, out _);

    private static readonly IntPtr HwndBroadcast = new(0xffff);
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam,
                                                    uint flags, uint timeout, out UIntPtr result);
}
