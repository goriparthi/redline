// One RedLine per sign-in session, whatever route started it. On Windows the guard is a named
// mutex keyed on the lock file's path, so each REDLINE_HOME profile gets its own instance.
using System.Security.Cryptography;
using System.Text;

namespace Redline.Core;

public sealed class SingleInstance : IDisposable
{
    private Mutex? _mutex;

    private SingleInstance(Mutex? mutex) { _mutex = mutex; }

    public static string LockUrl => RedlineHome.PathFor(".local/share/redline/instance.lock");

    /// Local\ scopes the name to this sign-in session, as a login-session lock does on macOS.
    public static string MutexName(string lockPath)
    {
        var key = Path.GetFullPath(lockPath).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
        return @"Local\redline-instance-" + hash;
    }

    /// Null when another holder exists. Hold the result for the life of the process; Windows
    /// closes the handle on exit or crash, so a killed app never locks out the next one.
    public static SingleInstance? Claim(string? at = null)
    {
        var path = at ?? LockUrl;
        // The file carries no lock; it is kept so the location matches macOS and is discoverable
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
        }
        catch { }
        Mutex mutex;
        bool createdNew;
        try { mutex = new Mutex(false, MutexName(path), out createdNew); }
        // Exists under another account's rights, so someone holds it
        catch (UnauthorizedAccessException) { return null; }
        // A guard that cannot even be created must not stop the app from starting
        catch { return new SingleInstance(null); }
        // Existence, not ownership, is the test: a mutex is re-entrant on the thread that owns it
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }
        return new SingleInstance(mutex);
    }

    public void Dispose()
    {
        _mutex?.Dispose();
        _mutex = null;
    }
}
