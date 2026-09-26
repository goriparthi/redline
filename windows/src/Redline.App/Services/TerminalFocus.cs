// Raises the window a Claude Code session is running in. Windows has no tty and no terminal
// publishes its tabs, so the honest result is the app coming forward, never a specific tab.
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Redline.App.Services;

/// The process whose window hosts a session, with a name fit for a menu item.
public sealed record TerminalOwner(int Pid, string ProcessName, string DisplayName, IntPtr Window);

public static class TerminalFocus
{
    /// How precisely a focus request landed. Tab is never reported on Windows.
    public enum Result { Tab, App, Failed }

    /// Reads a process's parent PID and start time; injectable so the chain walk is testable.
    public interface IProcessTable
    {
        int? Parent(int pid);
        DateTime? StartTime(int pid);
        string? Name(int pid);
        IntPtr MainWindow(int pid);
    }

    // Hosts that are not a terminal and must never be raised as one
    private static readonly HashSet<string> Stops = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "svchost", "services", "wininit", "winlogon", "csrss", "smss", "System", "Idle", "RedLine",
    };

    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WindowsTerminal"] = "Windows Terminal",
        ["OpenConsole"] = "Console",
        ["conhost"] = "Console",
        ["Code"] = "Visual Studio Code",
        ["Code - Insiders"] = "VS Code Insiders",
        ["Cursor"] = "Cursor",
        ["Windsurf"] = "Windsurf",
        ["pwsh"] = "PowerShell",
        ["powershell"] = "Windows PowerShell",
        ["cmd"] = "Command Prompt",
        ["wezterm-gui"] = "WezTerm",
        ["alacritty"] = "Alacritty",
        ["mintty"] = "Git Bash",
        ["idea64"] = "IntelliJ IDEA",
        ["rider64"] = "Rider",
        ["devenv"] = "Visual Studio",
    };

    /// The first process up the parent chain that owns a visible top-level window, or null.
    /// A session started by a service or a scheduler genuinely has none to raise.
    public static TerminalOwner? Owner(int pid, IProcessTable? table = null)
    {
        var t = table ?? SystemProcessTable.Shared;
        var self = Environment.ProcessId;
        var current = pid;
        DateTime? childStart = t.StartTime(pid);
        // The chain to a terminal is short; the bound stops a malformed table spinning forever
        for (var i = 0; i < 12; i++)
        {
            var name = t.Name(current);
            if (name is null || Stops.Contains(name) || current == self) return null;
            var window = t.MainWindow(current);
            if (window != IntPtr.Zero) return new TerminalOwner(current, name, DisplayName(name), window);
            if (t.Parent(current) is not { } parent || parent <= 4 || parent == current) return null;
            // A parent that started after its child is a recycled PID, not the real parent
            var parentStart = t.StartTime(parent);
            if (childStart is { } cs && parentStart is { } ps && ps > cs) return null;
            childStart = parentStart ?? childStart;
            current = parent;
        }
        return null;
    }

    public static string DisplayName(string processName) =>
        Names.TryGetValue(processName, out var n) ? n : processName;

    /// The menu item wording: it names the app, and never promises a tab.
    public static string MenuTitle(TerminalOwner owner) => $"Focus {owner.DisplayName}";

    public static string ToolTip(TerminalOwner owner) =>
        $"Brings {owner.DisplayName} forward. It publishes no way to say which tab a session is in, so the tab is yours to find.";

    /// A tab is only reachable where a terminal publishes which tab holds which session.
    public static bool CanFocusTab(int pid) => false;

    public static Result Focus(int pid, IProcessTable? table = null)
    {
        if (Owner(pid, table) is not { } owner) return Result.Failed;
        return Raise(owner.Window) ? Result.App : Result.Failed;
    }

    /// Windows refuses SetForegroundWindow to a process that is not in the foreground, so the
    /// call runs attached to the foreground thread's input, which lends its rights.
    public static bool Raise(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return false;
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
        if (Native.SetForegroundWindow(hwnd)) return true;

        var foreground = Native.GetForegroundWindow();
        var foreThread = Native.GetWindowThreadProcessId(foreground, out _);
        var ownThread = Native.GetCurrentThreadId();
        var attached = foreThread != 0 && foreThread != ownThread && Native.AttachThreadInput(ownThread, foreThread, true);
        try
        {
            Native.BringWindowToTop(hwnd);
            Native.ShowWindow(hwnd, Native.IsIconic(hwnd) ? Native.SW_RESTORE : Native.SW_SHOW);
            return Native.SetForegroundWindow(hwnd) || Native.GetForegroundWindow() == hwnd;
        }
        finally
        {
            if (attached) Native.AttachThreadInput(ownThread, foreThread, false);
        }
    }

    public sealed class SystemProcessTable : IProcessTable
    {
        public static readonly SystemProcessTable Shared = new();

        public int? Parent(int pid)
        {
            var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var info = new Native.PROCESS_BASIC_INFORMATION();
                var status = Native.NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf(info), out _);
                return status == 0 ? (int)info.InheritedFromUniqueProcessId : null;
            }
            finally { Native.CloseHandle(handle); }
        }

        public DateTime? StartTime(int pid)
        {
            try { using var p = Process.GetProcessById(pid); return p.StartTime.ToUniversalTime(); }
            catch { return null; }
        }

        public string? Name(int pid)
        {
            try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
            catch { return null; }
        }

        public IntPtr MainWindow(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                var h = p.MainWindowHandle;
                // A pseudoconsole's hidden window is not something to raise
                return h != IntPtr.Zero && Native.IsWindowVisible(h) ? h : IntPtr.Zero;
            }
            catch { return IntPtr.Zero; }
        }
    }

    private static class Native
    {
        public const int SW_SHOW = 5;
        public const int SW_RESTORE = 9;
        public const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr ExitStatus;
            public IntPtr PebBaseAddress;
            public IntPtr AffinityMask;
            public IntPtr BasePriority;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        [DllImport("ntdll.dll")]
        public static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref PROCESS_BASIC_INFORMATION info, int size, out int returned);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint attach, uint to, bool doAttach);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hwnd, int cmd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hwnd);
    }
}
