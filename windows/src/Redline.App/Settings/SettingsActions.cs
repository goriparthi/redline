// What settings can ask the app to do, and the machine state it shows but does not own.
// Port of SettingsActions and SettingsEnvironmentState in SettingsWindow.swift.
using System.Diagnostics;
using Redline.Core;

namespace Redline.App.Settings;

/// <summary>The things settings can ask the app to do, so a side effect beyond writing the file runs once, in the app.</summary>
/// <remarks>Alerts ask Windows for permission, the agent fleet starts watchers, the CLI token reads a credential.</remarks>
public sealed class SettingsActions
{
    public Action OpenSetup { get; set; } = () => { };
    public Action InstallClaudeFeed { get; set; } = () => { };
    public Action SignIn { get; set; } = () => { };
    public Action SignOut { get; set; } = () => { };
    public Action InstallOllamaShim { get; set; } = () => { };
    /// <summary>Notification Style. <see cref="SettingsShell.OpenNotificationSettings"/> is the Windows route.</summary>
    public Action OpenNotificationSettings { get; set; } = () => { };
    /// <summary>Kept for parity with macOS. Windows has no Full Disk Access, so nothing calls it.</summary>
    public Action GrantFullDiskAccess { get; set; } = () => { };
    public Action ToggleLaunchAtLogin { get; set; } = () => { };
    public Action CheckForUpdates { get; set; } = () => { };
    public Action EditConfig { get; set; } = () => { };
    public Action OpenDataFolder { get; set; } = () => { };
    public Action Uninstall { get; set; } = () => { };
    /// <summary>Reveals one of the bundled notice files, by file name. Nothing here reaches the network.</summary>
    public Action<string> OpenNotice { get; set; } = _ => { };

    // Preferences whose side effects belong to the app
    public Action<List<string>> SetProviders { get; set; } = _ => { };
    public Action<bool> SetCLIToken { get; set; } = _ => { };
    public Action<bool> SetAlerts { get; set; } = _ => { };
    public Action<bool> SetCues { get; set; } = _ => { };
    public Action<bool> SetHistory { get; set; } = _ => { };
    public Action<bool> SetSidecar { get; set; } = _ => { };
    public Action<bool> SetStatusChecks { get; set; } = _ => { };
    public Action<bool> SetAutoUpdates { get; set; } = _ => { };
    public Action<string> SetUpdateChannel { get; set; } = _ => { };
    public Action<bool> SetAgentFleet { get; set; } = _ => { };
    public Action<bool> SetMenuIcon { get; set; } = _ => { };
    public Action<bool> SetResetTimes { get; set; } = _ => { };
    public Action<string> SetLimitWindows { get; set; } = _ => { };
    public Action<string> SetMenuBarProvider { get; set; } = _ => { };
    public Action<string> SetTheme { get; set; } = _ => { };
}

/// <summary>State settings shows that is not config: what is installed, and whether the app is signed in.</summary>
/// <remarks>Refreshed by the app when the window opens, since each is a question about the machine.</remarks>
public sealed record SettingsEnvironmentState
{
    public bool SignedIn { get; init; }
    public bool OAuthConfigured { get; init; }
    public bool ClaudeFeedInstalled { get; init; }
    public bool OllamaShimInstalled { get; init; }
    public bool LaunchAtLogin { get; init; }
    /// <summary>Always false on Windows, which has no such grant; kept for parity.</summary>
    public bool FullDiskAccess { get; init; }
    public ProviderAvailability Availability { get; init; } = new([]);
    public string AppVersion { get; init; } = "";
}

/// <summary>Shell routes the app can plug into SettingsActions. Each opens something local.</summary>
public static class SettingsShell
{
    public const string RepoUrl = "https://github.com/goriparthi/redline";

    /// <summary>The bundled notices ship in Notices\ beside the executable.</summary>
    public static string NoticePath(string file) => Path.Combine(AppContext.BaseDirectory, "Notices", file);

    /// <summary>Windows Settings on the notifications page, the counterpart of System Settings.</summary>
    public static void OpenNotificationSettings() => Open("ms-settings:notifications");

    /// <summary>Opens a bundled notice in its default app, or reveals the folder if it is missing.</summary>
    public static void OpenNotice(string file)
    {
        var path = NoticePath(file);
        Open(File.Exists(path) ? path : Path.GetDirectoryName(path)!);
    }

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            Diag.Log.Error("settings.open_failed", ex.Message, new() { ["target"] = target });
        }
    }
}
