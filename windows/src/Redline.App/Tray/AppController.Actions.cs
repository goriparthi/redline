// Everything the dropdown, the settings window and the dashboard can ask the app to do, each
// through one method so its side effects live in one place (AppDelegate's @objc actions).
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;
using Redline.App.Dashboard;
using Redline.App.Services;
using Redline.App.Settings;
using Redline.Core;
using ShimInstaller = Redline.Cli.ShimInstaller;
using ShimInstallStatus = Redline.Cli.ShimInstallStatus;

namespace Redline.App.Tray;

public sealed partial class AppController
{
    // Toggles

    public void ToggleAlerts()
    {
        var next = !config.Alerts;
        if (!Config.Write(new() { ["alerts"] = next })) return;
        config.Alerts = next;
        RebuildMenu();
    }

    public void ToggleHistory()
    {
        var next = !config.RecordHistory;
        if (!Config.Write(new() { ["recordHistory"] = next })) return;
        config.RecordHistory = next;
        RebuildMenu();
    }

    public void ToggleCues()
    {
        var next = !config.MindfulCues;
        if (!Config.Write(new() { ["mindfulCues"] = next })) return;
        config.MindfulCues = next;
        RebuildMenu();
    }

    public void ToggleSidecar()
    {
        var next = !config.PublishSidecar;
        if (!Config.Write(new() { ["publishSidecar"] = next })) return;
        config.PublishSidecar = next;
        // Off means gone, not merely no longer updated: a file left behind would read as current
        PublishSidecar();
        RebuildMenu();
    }

    public void ToggleStatusChecks()
    {
        if (!Config.Write(new() { ["statusChecks"] = !config.StatusChecks })) return;
        config = Config.Load();
        serviceStatusAt = null;
        claudeService = null;
        codexService = null;
        // Both directions must reach every surface at once, or the toggle looks like it did nothing
        dashboardModel.Data.Services = SnapshotServices();
        dashboardModel.Data.ServicesCheckedAt = DateTimeOffset.UtcNow;
        PublishSnapshot();
        RefreshServiceStatus();
        RebuildMenu();
    }

    public void ToggleMenuIcon() => SetStyle(new() { ["showMenuIcon"] = !config.ShowMenuIcon });

    public void ToggleResetTimes() => SetStyle(new() { ["showResetTimes"] = !config.ShowResetTimes });

    void SetStyle(Dictionary<string, JsonNode?> values)
    {
        if (!Config.Write(values))
        {
            limitsStatus = "Could not write config";
            RebuildMenu();
            return;
        }
        config = Config.Load();
        UpdateTitle();
        RebuildMenu();
    }

    public void ToggleAutoUpdates()
    {
        if (!Config.Write(new() { ["autoCheckUpdates"] = !config.AutoCheckUpdates })) return;
        config = Config.Load();
        ScheduleUpdateTimer();
        RebuildMenu();
    }

    public void ToggleAgentFleet()
    {
        var next = !config.AgentFleet;
        if (!Config.Write(new() { ["agentFleet"] = next })) return;
        config.AgentFleet = next;
        if (next)
        {
            WatchFleetDirectory();
            RefreshFleet();
        }
        else
        {
            StopWatchingFleet();
            fleet = new FleetSnapshot();
            UpdateTitle();
        }
        RebuildMenu();
    }

    /// <summary>The Run value is the login item; the config remembers the choice so launch does not undo it.</summary>
    public void ToggleLaunchAtLogin()
    {
        var next = !login.IsEnabled;
        if (next) login.Enable(); else login.Disable();
        Config.Write(new() { ["launchAtLogin"] = next });
        config = Config.Load();
        RebuildMenu();
    }

    // Actions

    public void RefreshNow()
    {
        ReloadConfig();
        // On demand means now: the status throttle yields to an explicit refresh
        serviceStatusAt = null;
        Refresh();
    }

    /// <summary>Notepad explicitly; the default .json handler is often an IDE.</summary>
    public void EditConfig()
    {
        if (!File.Exists(Config.ConfigPath)) Config.WriteDefault(Config.ConfigPath);
        try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Config.ConfigPath}\"") { UseShellExecute = false }); }
        catch { OpenUrl(Config.ConfigPath); }
    }

    static void OpenUrl(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception e) { Diag.Log.Warn("shell.open_failed", "could not open", new() { ["target"] = target, ["error"] = e.Message }); }
    }

    static void RevealInExplorer(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false }); }
        catch { }
    }

    static void CopyText(string text)
    {
        try { Clipboard.SetText(text); } catch { }
    }

    void OpenRepo() => OpenUrl(Updates.RepoUrl);

    void OpenUpdate()
    {
        if (updateURL is { } u) OpenUrl(u);
    }

    void FocusFleetSession(int pid)
    {
        _ = Task.Run(() =>
        {
            var result = TerminalFocus.Focus(pid);
            // A session that exited between the menu opening and the click has nothing to raise
            if (result == TerminalFocus.Result.Failed) ui.BeginInvoke(RefreshFleet);
        });
    }

    /// <summary>Whether RedLine's own shim is the ollama on the PATH, by the installer's marker.</summary>
    static bool OllamaShimInstalled => ShimInstaller.IsInstalled(RedlineHome.IsOverridden ? RedlineHome.Url : null);

    /// <summary>Writing onto the PATH is on request, not at launch, since it needs a decision.</summary>
    public void InstallOllamaShim()
    {
        var result = ShimInstaller.Install(home: RedlineHome.IsOverridden ? RedlineHome.Url : null);
        switch (result.Status)
        {
            case ShimInstallStatus.ForeignFile:
                AlertDialog.Show("A different ollama already lives there", result.Message ?? result.ShimPath);
                return;
            case ShimInstallStatus.Failed:
                AlertDialog.Show("Could not install the shim", $"{result.ShimPath}\n\n{result.Message}");
                return;
        }
        var text = $"Installed at {result.ShimPath}\n\n" +
            "It passes everything through to the real ollama unchanged and records token counts for plain " +
            "`ollama run` calls. For it to be found first, %USERPROFILE%\\.local\\bin must come before the real " +
            "ollama in your PATH" + (result.PathChanged ? "; RedLine put it at the front of your user PATH." : ", and it already does.");
        if (result.Message is { } m) text += $"\n\n{m}";
        if (result.Warning is { } w) text += $"\n\n{w}";
        if (AlertDialog.Show("Ollama tracking is set up", text, "OK", "Show in Explorer") == 1) RevealInExplorer(result.ShimPath);
        RefreshSettingsState();
    }

    /// <summary>The percentages without a credential: Claude Code hands its statusline command its own limits.</summary>
    public void InstallStatuslineFeed()
    {
        switch (StatuslineInstaller.Install())
        {
            case StatuslineInstaller.Result.Failed f:
                AlertDialog.Show("Could not set up the usage feed", f.Message);
                break;
            case StatuslineInstaller.Result.AlreadyInstalled a:
                AlertDialog.Show("The usage feed is already set up",
                    $"{a.Script}\n\nStart or continue a Claude Code session and the percentages appear at the next refresh.");
                break;
            case StatuslineInstaller.Result.Installed i:
                var text = $"Installed at {i.Script} and pointed to by statusLine in %USERPROFILE%\\.claude\\settings.json.\n\n" +
                    "Claude Code passes its rate-limit windows to that command, so RedLine reads them from disk. " +
                    "No token, no Credential Manager, and no request to Anthropic.\n\n" +
                    "The figures update while Claude Code is running and carry their own timestamp between sessions.";
                if (i.Chained is { } chained) text += $"\n\nYour existing statusline is kept and still draws the line:\n{chained}";
                if (AlertDialog.Show("Claude usage feed is set up", text, "OK", "Show in Explorer") == 1) RevealInExplorer(i.Script);
                Refresh();
                break;
        }
        RefreshSettingsState();
    }

    public void SignIn()
    {
        limitsStatus = "Waiting for browser sign-in…";
        RebuildMenu();
        _ = Task.Run(async () =>
        {
            string? err;
            try { err = await oauth.SignInAsync(); } catch (Exception e) { err = e.Message; }
            await ui.InvokeAsync(() =>
            {
                limitsStatus = err;
                Refresh();
                RefreshSettingsState();
            });
        });
    }

    public void SignOut()
    {
        oauth.SignOut();
        claudeLimits = new();
        claudeLimitsAt = null;
        claudeLimitsSource = null;
        limitsStatus = null;
        UpdateTitle();
        // The feed, if installed, repopulates the percentages on the next poll
        Refresh();
        RefreshSettingsState();
    }

    /// <summary>The browser route: straight to sign-in with a client id, otherwise the setup window.</summary>
    public void BrowserSignIn()
    {
        if (oauth.CanSignIn) SignIn(); else ShowSetup();
    }

    public void FixCredentialAccess()
    {
        oauth.ResetCLIProbe();
        VerifyCLITokenReadable();
    }

    /// <summary>On enables the borrow and verifies the read; off disables it.</summary>
    public void ToggleCLIToken()
    {
        if (config.UseCLIToken)
        {
            if (!Config.Write(new() { ["useCLIToken"] = false }))
            {
                limitsStatus = "Could not write config";
                RebuildMenu();
                return;
            }
            config = Config.Load();
            oauth.Update(config.OAuth, false);
            Refresh();
        }
        else EnableCLIToken();
    }

    public void EnableCLIToken()
    {
        if (!Config.Write(new() { ["useCLIToken"] = true }))
        {
            limitsStatus = "Could not write config";
            RebuildMenu();
            return;
        }
        config = Config.Load();
        oauth.Update(config.OAuth, true);
        oauth.ResetCLIProbe();
        VerifyCLITokenReadable();
    }

    /// <summary>Enabling the borrowed token either works now or needs the user, and both are said out loud.</summary>
    void VerifyCLITokenReadable()
    {
        _ = Task.Run(async () =>
        {
            bool readable;
            try { readable = await oauth.ProbeCLITokenAsync(); } catch { readable = false; }
            await ui.InvokeAsync(() =>
            {
                if (readable) { Refresh(); return; }
                AlertDialog.Show("Could not read the Claude CLI's token",
                    "The percentages stay hidden until RedLine can read Claude Code's credential.\n\n" +
                    $"It looks in {ClaudeCredentialSource.FilePath()} and in Credential Manager under \"{ClaudeCredentialSource.CredentialTarget}\".\n\n" +
                    "If Claude Code is not signed in, run `claude` once and sign in first.\n\n" +
                    $"{oauth.TokenUnavailableReason()}");
                Refresh();
            });
        });
    }

    // Updates

    /// <summary>Once a day, on by default: a security fix nobody hears about is not a fix.</summary>
    void ScheduleUpdateTimer()
    {
        updates.StopDailyChecks();
        if (!config.AutoCheckUpdates) return;
        updates.StartDailyChecks(() => config, result => ui.BeginInvoke(() => HandleUpdateResult(result, interactive: false)));
    }

    public void CheckForUpdates()
    {
        updateStatus = "Checking…";
        RebuildMenu();
        RunUpdateCheck(interactive: true);
    }

    void RunUpdateCheck(bool interactive)
    {
        var channel = config.UpdateChannel;
        _ = Task.Run(async () =>
        {
            var result = await updates.CheckAsync(Updates.CurrentVersion, channel);
            await ui.InvokeAsync(() => HandleUpdateResult(result, interactive));
        });
    }

    /// <summary>Interactive: every outcome is a dialog. Background: only an available update earns one.</summary>
    void HandleUpdateResult(Updates.CheckResult result, bool interactive)
    {
        var current = Updates.CurrentVersion;
        if (result is not Updates.CheckResult.Available && !interactive)
        {
            if (result is Updates.CheckResult.UpToDate) updateStatus = $"Up to date ({current})";
            RebuildMenu();
            return;
        }
        switch (result)
        {
            case Updates.CheckResult.UpToDate:
                updateStatus = $"Up to date ({current})";
                AlertDialog.Show("You're up to date", $"RedLine {current} is the latest release.");
                break;
            case Updates.CheckResult.Available a:
                updateStatus = $"Update available: {a.Version}";
                updateURL = a.PageUrl;
                updatePackage = a.CanInstall ? a : null;
                updateVersion = a.Version;
                RebuildMenu();
                if (a.CanInstall)
                {
                    switch (AlertDialog.Show($"RedLine {a.Version} is available", $"You have {current}.",
                                             "Install and Relaunch", "Open Release Page", "Later"))
                    {
                        case 0: BeginUpdateInstall(); break;
                        case 1: OpenUrl(a.PageUrl); break;
                    }
                }
                else if (AlertDialog.Show($"RedLine {a.Version} is available", $"You have {current}.",
                                          "Open Release Page", "Later") == 0)
                {
                    OpenUrl(a.PageUrl);
                }
                break;
            case Updates.CheckResult.Failed f:
                updateStatus = f.Message;
                AlertDialog.Show("Update check failed", f.Message);
                break;
        }
        RebuildMenu();
    }

    /// <summary>The only way to replace a running tray app is for it to do it itself: quit, swap, relaunch.</summary>
    public void InstallUpdate()
    {
        if (updateInFlight || updatePackage is null || updateVersion is not { } version) return;
        if (Updates.IsDevelopmentBuild())
        {
            updateStatus = "Development build; update with git pull instead";
            RebuildMenu();
            return;
        }
        if (AlertDialog.Show($"Install RedLine {version}?",
                "Downloads the release, verifies its checksum and signature against this build, then quits, " +
                "replaces the RedLine folder in place, and relaunches.",
                "Install and Relaunch", "Cancel") != 0) return;
        BeginUpdateInstall();
    }

    // The flow after consent; the update-available dialog enters here directly
    void BeginUpdateInstall()
    {
        if (updateInFlight || updatePackage is not { } package || updateVersion is not { } version) return;
        if (Updates.IsDevelopmentBuild())
        {
            updateStatus = "Development build; update with git pull instead";
            RebuildMenu();
            return;
        }
        updateInFlight = true;
        var progress = new Progress<string>(s =>
        {
            updateStatus = $"{s} ({version})";
            RebuildMenu();
        });
        _ = Task.Run(async () =>
        {
            var result = await updates.StageAsync(package, status: progress);
            await ui.InvokeAsync(() =>
            {
                updateInFlight = false;
                switch (result)
                {
                    case Updates.StageResult.Failed f:
                        updateStatus = f.Message;
                        RebuildMenu();
                        break;
                    case Updates.StageResult.Ready r:
                        updateStatus = "Relaunching…";
                        r.Swap();
                        Quit();
                        break;
                }
            });
        });
    }

    /// <summary>The removals scripts/uninstall.sh performs, then the install folder once this process exits.</summary>
    public void Uninstall()
    {
        var (choice, purge) = AlertDialog.ShowWithCheckbox("Uninstall RedLine?",
            "Removes RedLine's folder, its login item, its Credential Manager token and the Claude usage feed. " +
            "Your Claude, Codex and Ollama files are never touched.",
            "Also remove settings, logs and history", "Uninstall", "Cancel");
        if (choice != 0) return;
        oauth.SignOut();
        var report = new Uninstaller().Run(purge);
        // A development build runs straight from bin, where there is no install to remove
        if (!report.InstallDirScheduled && !Updates.IsDevelopmentBuild())
        {
            var why = string.Join("\n", report.Problems);
            if (AlertDialog.Show("Everything except the app itself was removed",
                    $"RedLine could not schedule its own folder for removal:\n{why}\n\nDelete {Updates.InstallDir} to finish.",
                    "Show in Explorer", "Quit") == 0)
                RevealInExplorer(Path.Combine(Updates.InstallDir, Updates.ExeName));
        }
        Quit();
    }

    public void Quit()
    {
        Dispose();
        Application.Current.Shutdown();
    }

    // Windows

    public void OpenDashboard()
    {
        // The findings panel is the one thing not derived from the poll, so opening is a good moment
        if (findingsService.RefreshIfDue(config)) dashboardModel.Data.FindingsScanning = true;
        dashboardModel.Data.Paces = paces;
        if (dashboardWindow is { } existing)
        {
            Raise(existing);
            dashboardModel.Load(dashboardModel.Data.Range, AllLimits.ToList());
            dashboardModel.Data.ClaudeLimitsAsOf = claudeLimitsAt;
            RefreshOllamaPanel();
            return;
        }
        var window = new DashboardWindow(dashboardModel,
            onReload: range =>
            {
                dashboardModel.Load(range, AllLimits.ToList());
                dashboardModel.Data.ClaudeLimitsAsOf = claudeLimitsAt;
                // Ollama is live state, not derived from transcripts, so it refreshes here too
                RefreshOllamaPanel();
            },
            onFocus: provider =>
            {
                dashboardModel.SetFocus(provider);
                if (provider == OllamaStore.Provider) RefreshOllamaPanel();
            },
            onOpenSettings: () => OpenSettings());
        dashboardModel.OnThemeChange = _ =>
        {
            if (dashboardWindow is DashboardWindow d)
            {
                d.Background = RL.Surface.Ground.Brush(d.Dashboard.EffectiveTheme);
                ThemeManager.ApplyTitleBar(d, d.Dashboard.EffectiveTheme);
            }
        };
        window.Closed += (_, _) => dashboardWindow = null;
        dashboardWindow = window;
        window.Show();
        Raise(window);
        dashboardModel.Load(14, AllLimits.ToList());
        dashboardModel.Data.ClaudeLimitsAsOf = claudeLimitsAt;
        RefreshOllamaPanel();
    }

    static void Raise(Window w)
    {
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Show();
        w.Activate();
        // A tray app is not the foreground process, so Windows may only flash the taskbar button
        w.Topmost = true;
        w.Topmost = false;
        w.Focus();
    }

    public void ShowSetup()
    {
        if (setupWindow is { } existing)
        {
            Raise(existing);
            return;
        }
        var detected = ProviderAvailability.Detect(ollamaReachable: ollamaSection?.Reachable ?? false,
                                                   claudeAccount: oauth.IsSignedIn || config.OAuth.IsConfigured);
        var window = new FirstRunWindow(detected, (providers, choice, clientId) =>
        {
            if (providers.Count > 0) Config.SetProviders(providers);
            if (!Config.Write(new() { ["useCLIToken"] = choice == ClaudeLimitsChoice.CliToken }))
                limitsStatus = "Could not write config";
            if (choice == ClaudeLimitsChoice.Browser) Config.SetOAuthClientId(clientId);
            // Off means off: keeping a signed-in token would leave the percentages showing
            if (choice == ClaudeLimitsChoice.Off && oauth.IsSignedIn && !oauth.UsingCLIToken)
            {
                oauth.SignOut();
                claudeLimits = new();
            }
            config = Config.Load();
            oauth.Update(config.OAuth, config.UseCLIToken);
            // The window closes itself once this returns
            setupWindow = null;
            // The feed installs quietly here: Start is the consent, and failures still speak up
            if (choice == ClaudeLimitsChoice.Feed && !StatuslineInstaller.IsInstalled() &&
                StatuslineInstaller.Install() is StatuslineInstaller.Result.Failed f)
                limitsStatus = $"Usage feed: {f.Message}";
            // The browser flow is the one thing Start cannot finish on its own
            if (choice == ClaudeLimitsChoice.Browser && !oauth.IsSignedIn) SignIn();
            if (choice == ClaudeLimitsChoice.CliToken)
            {
                // The same choice re-asserted still means "try again", not "keep the old failure"
                oauth.ResetCLIProbe();
                VerifyCLITokenReadable();
            }
            Refresh();
        }, config.Providers, config.UseCLIToken, config.OAuth.ClientId, StatuslineInstaller.IsInstalled(),
           // This app's own grant, not the broader IsSignedIn that a borrowed token also satisfies
           oauth.HasOwnGrant);
        window.Closed += (_, _) => { if (setupWindow == window) setupWindow = null; };
        setupWindow = window;
        window.Show();
        Raise(window);
    }

    /// <summary>Everything that changes how RedLine behaves, in named sections, driving these same methods.</summary>
    public void OpenSettings()
    {
        RefreshSettingsState();
        if (settingsWindow is { } existing)
        {
            Raise(existing);
            return;
        }
        settingsModel.OnConfigChanged = ApplyPlainSettingsChange;
        settingsModel.Actions = SettingsActions();
        var window = new SettingsWindow(settingsModel);
        window.Closed += (_, _) => settingsWindow = null;
        settingsWindow = window;
        window.Show();
        Raise(window);
    }

    /// <summary>The questions settings asks about the machine. Read when it opens and after any change.</summary>
    void RefreshSettingsState()
    {
        settingsModel.Reload();
        settingsModel.State = new SettingsEnvironmentState
        {
            SignedIn = oauth.HasOwnGrant,
            OAuthConfigured = config.OAuth.IsConfigured,
            ClaudeFeedInstalled = StatuslineInstaller.IsInstalled(),
            OllamaShimInstalled = OllamaShimInstalled,
            LaunchAtLogin = login.IsEnabled,
            FullDiskAccess = false,
            Availability = ProviderAvailability.Detect(ollamaReachable: ollamaSection?.Reachable ?? false,
                                                       claudeAccount: oauth.IsSignedIn || config.OAuth.IsConfigured),
            AppVersion = Updates.CurrentVersion,
        };
    }

    /// <summary>A preference whose effect is the stored value still reaches the timer, the icon and the dashboard.</summary>
    void ApplyPlainSettingsChange()
    {
        config = Config.Load();
        oauth.Update(config.OAuth, config.UseCLIToken);
        ScheduleTimer();
        UpdateTitle();
        RebuildMenu();
        ReloadDashboardIfOpen();
    }

    /// <summary>Thresholds and ranges are read when the dashboard loads, so a change reloads it.</summary>
    void ReloadDashboardIfOpen()
    {
        if (dashboardWindow is null) return;
        dashboardModel.Load(dashboardModel.Data.Range, AllLimits.ToList());
    }

    SettingsActions SettingsActions() => new()
    {
        OpenSetup = ShowSetup,
        InstallClaudeFeed = InstallStatuslineFeed,
        SignIn = BrowserSignIn,
        SignOut = SignOut,
        InstallOllamaShim = InstallOllamaShim,
        OpenNotificationSettings = SettingsShell.OpenNotificationSettings,
        // Windows has no Full Disk Access grant; the row exists only for parity
        GrantFullDiskAccess = RefreshSettingsState,
        ToggleLaunchAtLogin = () => { ToggleLaunchAtLogin(); RefreshSettingsState(); },
        CheckForUpdates = CheckForUpdates,
        EditConfig = EditConfig,
        OpenDataFolder = () =>
        {
            var dir = RedlineHome.DataDir();
            try { Directory.CreateDirectory(dir); } catch { }
            OpenUrl(dir);
        },
        Uninstall = Uninstall,
        OpenNotice = SettingsShell.OpenNotice,
        // Each is the same method the dropdown uses, so the side effects stay in one place
        SetProviders = ApplyProviderSelection,
        SetCLIToken = _ => { ToggleCLIToken(); RefreshSettingsState(); },
        SetAlerts = _ => ToggleAlerts(),
        SetCues = _ => ToggleCues(),
        SetHistory = _ => ToggleHistory(),
        SetSidecar = _ => ToggleSidecar(),
        SetStatusChecks = _ => ToggleStatusChecks(),
        SetAutoUpdates = _ => ToggleAutoUpdates(),
        SetUpdateChannel = ApplyUpdateChannel,
        SetAgentFleet = _ => ToggleAgentFleet(),
        SetMenuIcon = _ => ToggleMenuIcon(),
        SetResetTimes = _ => ToggleResetTimes(),
        SetLimitWindows = choice => SetStyle(new() { ["limitWindows"] = choice }),
        SetMenuBarProvider = ApplyMenuBarProvider,
        SetTheme = theme =>
        {
            dashboardModel.SetTheme(theme);
            settingsModel.Reload();
        },
    };

    /// <summary>Changing what is read changes what is detected, so both follow the write at once.</summary>
    void ApplyProviderSelection(List<string> providers)
    {
        if (providers.Count == 0 || !Config.SetProviders(providers)) return;
        config = Config.Load();
        RefreshAvailability();
        Refresh();
        RebuildMenu();
        ReloadDashboardIfOpen();
    }

    /// <summary>Switching channels rechecks straight away, so the choice answers with the build it found.</summary>
    void ApplyUpdateChannel(string channel)
    {
        if (channel == config.UpdateChannel) return;
        if (!Config.Write(new() { ["updateChannel"] = channel }))
        {
            updateStatus = "Could not write config";
            RebuildMenu();
            return;
        }
        config = Config.Load();
        CheckForUpdates();
    }

    void ApplyMenuBarProvider(string provider)
    {
        if (!Config.SetMenuBarProvider(provider))
        {
            limitsStatus = "Could not write config";
            RebuildMenu();
            return;
        }
        config = Config.Load();
        UpdateTitle();
        RebuildMenu();
    }
}
