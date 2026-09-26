// Entry point logic from main.swift: the login-item flags, --version, the bundled CLI, the
// instance guard, and the normal launch that puts the tray icon up.
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Redline.App.Services;
using Redline.Core;

namespace Redline.App.Tray;

public static class TrayHost
{
    static SingleInstance? instance;
    static InstanceSignal? signal;
    static AppController? controller;

    /// <summary>Handles the command line and, for a normal launch, starts the tray. Shuts down itself when done.</summary>
    public static void Run(Application app, string[] args)
    {
        var version = Updates.CurrentVersion;
        switch (args.FirstOrDefault())
        {
            // The login item is the Run key; the flag names match the macOS LaunchAgent ones
            case "--install-launch-agent":
                var on = new LaunchAtLogin().Enable();
                // Remembered only where a config exists, so a fresh install still gets its setup window
                if (!Config.IsFirstRun()) Config.Write(new() { ["launchAtLogin"] = true });
                Exit(app, on ? $"Installed login item {LaunchAtLogin.ValueName}" : "Could not write the login item", on ? 0 : 1);
                return;
            case "--uninstall-launch-agent":
                var off = new LaunchAtLogin().Disable();
                if (!Config.IsFirstRun()) Config.Write(new() { ["launchAtLogin"] = false });
                Exit(app, off ? $"Removed login item {LaunchAtLogin.ValueName}" : "Could not remove the login item", off ? 0 : 1);
                return;
            // Run by the MSI before it removes the files: undo what RedLine set up outside them
            case "--msi-uninstall":
                MsiInstall.StopOtherInstances();
                var report = new Uninstaller(scheduleDelete: (_, _) => { }, productCode: () => null).Run(purge: false);
                Diag.Log.Info("uninstall.msi", "cleanup before Windows Installer removal",
                    new() { ["removed"] = report.Removed.Count.ToString(), ["problems"] = string.Join("; ", report.Problems) });
                Exit(app, string.Join(Environment.NewLine, report.Removed.Concat(report.Problems)), 0);
                return;
            case "--version":
                Exit(app, $"redline {version}", 0);
                return;
            // The bundled CLI, answered from the files the app publishes, so no second copy starts
            case { } arg when RedlineCLI.Commands.Contains(arg):
                var result = RedlineCLI.Run(args, version);
                Exit(app, result.Text, result.Code);
                return;
            case { } arg when arg != "--dashboard":
                Exit(app, $"unknown argument: {arg}", 2, error: true);
                return;
        }

        // A copy already owns the tray. Show its dashboard rather than exiting silently
        instance = SingleInstance.Claim();
        if (instance is null)
        {
            InstanceSignal.Post();
            // Zero on purpose: a duplicate that exits cleanly is not a failure to restart
            Exit(app, "redline is already running", 0, error: true);
            return;
        }

        // Before anything else can fail, so a startup failure has somewhere to go
        Diag.Configure(version);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += (_, e) =>
        {
            // A tray app that dies takes the readout with it, so the fault is logged and survived
            Diag.Log.Error("app.unhandled", "unhandled exception on the UI thread",
                new() { ["error"] = e.Exception.GetType().Name + ": " + e.Exception.Message });
            e.Handled = true;
        };
        app.Exit += (_, _) =>
        {
            controller?.Dispose();
            signal?.Dispose();
            instance?.Dispose();
        };
        controller = new AppController();
        var ui = app.Dispatcher;
        signal = InstanceSignal.Listen(() => ui.BeginInvoke(() => controller?.OpenDashboard()));
        controller.Start(openDashboard: args.FirstOrDefault() == "--dashboard");
    }

    static void Exit(Application app, string text, int code, bool error = false)
    {
        Print(text, error);
        app.Shutdown(code);
    }

    /// <summary>A GUI executable has no console of its own; it borrows the one it was started from.</summary>
    static void Print(string text, bool error)
    {
        try
        {
            AttachConsole(-1);
            var stream = error ? Console.OpenStandardError() : Console.OpenStandardOutput();
            using var w = new StreamWriter(stream, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            w.WriteLine(text);
        }
        catch { }
    }

    [DllImport("kernel32.dll")] static extern bool AttachConsole(int processId);
}
