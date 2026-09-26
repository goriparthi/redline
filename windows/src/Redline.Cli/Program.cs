// redlinectl.exe entry point: the RedlineCLI commands, Claude Code's statusline feeder, and the
// ollama shim that ollama.cmd forwards to. Nothing here launches the tray app.
using System.Reflection;
using System.Text;
using Redline.Core;

namespace Redline.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        var first = args.Length > 0 ? args[0] : null;
        switch (first)
        {
            case "statusline":
                return Statusline();
            case "ollama-shim":
                return Shim(args.Skip(1).ToArray());
            default:
                // Nothing a script reads should depend on the console's code page
                using (var stdout = Utf8Out())
                    return RedlineCLI.Execute(args, stdout, Version());
        }
    }

    /// Claude Code's statusLine command on Windows: `"...\redlinectl.exe" statusline`.
    private static int Statusline()
    {
        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var stdout = Utf8Out();
        return new StatuslineFeeder().Run(stdin, stdout);
    }

    private static int Shim(string[] args)
    {
        // Ctrl+C belongs to the real ollama; the shim waits for it rather than dying first
        Console.CancelKeyPress += (_, e) => e.Cancel = true;
        using var stdin = Console.OpenStandardInput();
        // A terminal gets Console.Out so non-ASCII renders; a pipe gets plain UTF-8 bytes
        var stdout = Console.IsOutputRedirected ? Utf8Out() : Console.Out;
        try
        {
            return new OllamaShim().Run(args, stdin, !Console.IsInputRedirected, stdout, Console.Error);
        }
        finally
        {
            stdout.Flush();
            if (!ReferenceEquals(stdout, Console.Out)) stdout.Dispose();
        }
    }

    private static TextWriter Utf8Out() =>
        new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false };

    private static string Version() =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev";
}
