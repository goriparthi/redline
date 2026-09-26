// Where everything RedLine reads and writes hangs off. REDLINE_HOME moves it, but only when it
// names an absolute path that already exists, so a typo is ignored rather than starting a profile.
namespace Redline.Core;

public static class RedlineHome
{
    public const string Variable = "REDLINE_HOME";

    public static string AccountHome =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string Url
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(Variable);
            if (!string.IsNullOrEmpty(raw) && Path.IsPathFullyQualified(raw) && Directory.Exists(raw))
                return Path.GetFullPath(raw);
            return AccountHome;
        }
    }

    /// True when the paths in use are not the account's own, so a diagnostic can say so.
    public static bool IsOverridden =>
        !string.Equals(Path.GetFullPath(Url).TrimEnd('\\', '/'),
                       Path.GetFullPath(AccountHome).TrimEnd('\\', '/'),
                       StringComparison.OrdinalIgnoreCase);

    /// Joins forward-slash relative components onto the home, e.g. ".local/share/redline".
    public static string PathFor(string components) => Join(Url, components);

    public static string Join(string root, string components) =>
        Path.Combine(new[] { root }.Concat(components.Split('/', StringSplitOptions.RemoveEmptyEntries)).ToArray());

    /// RedLine's own data directory: snapshot, feed, shim log, history.
    public static string DataDir(string? home = null) => Join(home ?? Url, ".local/share/redline");
}
