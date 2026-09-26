// Reads the credential Claude Code already stored, least intrusive first: the plain file Claude
// Code uses on Windows, then Credential Manager. Read only; every miss says why it missed.
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Redline.Core;

namespace Redline.App.Services;

public static class ClaudeCredentialSource
{
    /// The service name Claude Code uses for its Keychain item, and so for any keytar-style store.
    public const string CredentialTarget = "Claude Code-credentials";

    public static string FilePath(string? home = null) =>
        RedlineHome.Join(home ?? RedlineHome.Url, ".claude/.credentials.json");

    /// The file first, because on Windows that is where Claude Code keeps it. Credential Manager
    /// never prompts, so the macOS `security` rung has nothing to replace and is dropped.
    public static CredentialOutcome Load(string? home = null, ICredentialStore? store = null)
    {
        var fileOutcome = FromFile(home);
        if (fileOutcome is CredentialOutcome.Found) return fileOutcome;

        var storeOutcome = FromCredentialManager(store ?? WindowsCredentialStore.Shared);
        if (storeOutcome is CredentialOutcome.Found) return storeOutcome;

        // Prefer the more specific answer: a present-but-refused item is not an absent one
        if (storeOutcome is CredentialOutcome.NotFound && fileOutcome is CredentialOutcome.NotFound)
            return new CredentialOutcome.NotFound();
        return storeOutcome is CredentialOutcome.NotFound ? fileOutcome : storeOutcome;
    }

    public static CredentialOutcome FromFile(string? home = null)
    {
        var path = FilePath(home);
        if (!File.Exists(path)) return new CredentialOutcome.NotFound();
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        // Present but locked or unreadable is transient, never "signed out"
        catch (UnauthorizedAccessException) { return new CredentialOutcome.AccessDenied(); }
        catch (IOException) { return new CredentialOutcome.AccessDenied(); }
        return Parse(data);
    }

    /// The exact target first, then any `Claude Code-credentials*` item a keytar build suffixed.
    public static CredentialOutcome FromCredentialManager(ICredentialStore store)
    {
        CredentialRead read;
        try { read = store.Read(CredentialTarget); }
        catch { return new CredentialOutcome.AccessDenied(); }
        if (read.Status == CredentialReadStatus.Found && read.Credential is { } c)
            return Parse(Encoding.UTF8.GetBytes(WindowsCredentialStore.DecodeBlob(c.Blob)));
        if (read.Status == CredentialReadStatus.AccessDenied) return new CredentialOutcome.AccessDenied();

        IReadOnlyList<StoredCredential> matches;
        try { matches = store.Enumerate(CredentialTarget + "*"); }
        catch { return new CredentialOutcome.NotFound(); }
        CredentialOutcome best = new CredentialOutcome.NotFound();
        foreach (var m in matches.OrderBy(m => m.Target, StringComparer.Ordinal))
        {
            var outcome = Parse(Encoding.UTF8.GetBytes(WindowsCredentialStore.DecodeBlob(m.Blob)));
            if (outcome is CredentialOutcome.Found) return outcome;
            best = outcome;
        }
        return best;
    }

    /// When the credential last changed, read without parsing it: a moved stamp means Claude
    /// Code rotated the token and the cached copy is worthless.
    public static DateTimeOffset? ModifiedAt(string? home = null, ICredentialStore? store = null)
    {
        try
        {
            var path = FilePath(home);
            if (File.Exists(path)) return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            var read = (store ?? WindowsCredentialStore.Shared).Read(CredentialTarget);
            return read.Credential?.LastWritten;
        }
        catch { return null; }
    }

    internal static CredentialOutcome Parse(byte[] data)
    {
        JsonObject? json = null;
        try { json = JsonNode.Parse(data) as JsonObject; } catch { }
        if (json is null)
        {
            Diag.Log.Error("credential.unparsed", "stored credential was not the expected JSON",
                new() { ["bytes"] = data.Length.ToString() });
            return new CredentialOutcome.Unreadable();
        }
        return CredentialScan.Credential(json) is { } credential
            ? new CredentialOutcome.Found(credential)
            : new CredentialOutcome.Unreadable();
    }
}
