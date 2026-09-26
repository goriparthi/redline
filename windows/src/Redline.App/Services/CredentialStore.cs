// Windows Credential Manager behind an interface, so OAuth and the CLI-credential reader can be
// tested without touching the real store. Generic credentials only, current user only.
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

[assembly: InternalsVisibleTo("Redline.App.Tests")]

namespace Redline.App.Services;

/// Why a read produced nothing; mirrors the Keychain statuses the macOS reader distinguished.
public enum CredentialReadStatus { Found, NotFound, AccessDenied }

public sealed record StoredCredential(string Target, byte[] Blob, DateTimeOffset? LastWritten);

public sealed record CredentialRead(CredentialReadStatus Status, StoredCredential? Credential = null)
{
    public static readonly CredentialRead NotFound = new(CredentialReadStatus.NotFound);
    public static readonly CredentialRead Denied = new(CredentialReadStatus.AccessDenied);
}

public interface ICredentialStore
{
    CredentialRead Read(string target);
    /// Generic credentials whose target matches a `prefix*` filter, for items with a user suffix.
    IReadOnlyList<StoredCredential> Enumerate(string filter);
    bool Write(string target, string userName, byte[] blob);
    bool Delete(string target);
}

/// The real store. Blobs are capped by Windows at 2560 bytes per generic credential.
public sealed class WindowsCredentialStore : ICredentialStore
{
    public static readonly WindowsCredentialStore Shared = new();

    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredEnumerateW")]
    private static extern bool CredEnumerate(string? filter, int flags, out int count, out IntPtr credentials);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    public CredentialRead Read(string target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var ptr))
        {
            // Anything but a clean "not found" means it may exist and was not handed over
            return Marshal.GetLastWin32Error() == ERROR_NOT_FOUND ? CredentialRead.NotFound : CredentialRead.Denied;
        }
        try { return new CredentialRead(CredentialReadStatus.Found, Copy(ptr)); }
        finally { CredFree(ptr); }
    }

    public IReadOnlyList<StoredCredential> Enumerate(string filter)
    {
        if (!CredEnumerate(filter, 0, out var count, out var list)) return Array.Empty<StoredCredential>();
        try
        {
            var result = new List<StoredCredential>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.ReadIntPtr(list, i * IntPtr.Size);
                var c = Marshal.PtrToStructure<CREDENTIAL>(item);
                if (c.Type == CRED_TYPE_GENERIC) result.Add(Copy(item));
            }
            return result;
        }
        finally { CredFree(list); }
    }

    public bool Write(string target, string userName, byte[] blob)
    {
        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        var targetPtr = Marshal.StringToHGlobalUni(target);
        var userPtr = Marshal.StringToHGlobalUni(userName);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var c = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = targetPtr,
                UserName = userPtr,
                CredentialBlob = blobPtr,
                CredentialBlobSize = blob.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
            };
            // CredWrite replaces an existing item in place, so there is no duplicate case to handle
            if (CredWrite(ref c, 0)) return true;
            Redline.Core.Diag.Log.Error("credman.write_failed", "could not write the credential",
                new() { ["target"] = target, ["error"] = new Win32Exception(Marshal.GetLastWin32Error()).Message });
            return false;
        }
        finally
        {
            // The secret is zeroed before the buffer goes back to the heap
            for (var i = 0; i < blob.Length; i++) Marshal.WriteByte(blobPtr, i, 0);
            Marshal.FreeHGlobal(blobPtr);
            Marshal.FreeHGlobal(targetPtr);
            Marshal.FreeHGlobal(userPtr);
        }
    }

    public bool Delete(string target) =>
        CredDelete(target, CRED_TYPE_GENERIC, 0) || Marshal.GetLastWin32Error() == ERROR_NOT_FOUND;

    private static StoredCredential Copy(IntPtr ptr)
    {
        var c = Marshal.PtrToStructure<CREDENTIAL>(ptr);
        var blob = new byte[c.CredentialBlobSize];
        if (c.CredentialBlobSize > 0) Marshal.Copy(c.CredentialBlob, blob, 0, blob.Length);
        var ticks = ((long)(uint)c.LastWritten.dwHighDateTime << 32) | (uint)c.LastWritten.dwLowDateTime;
        DateTimeOffset? written = ticks > 0 ? DateTimeOffset.FromFileTime(ticks).ToUniversalTime() : null;
        return new StoredCredential(Marshal.PtrToStringUni(c.TargetName) ?? "", blob, written);
    }

    /// Credential blobs are UTF-8 from node's keytar and UTF-16 from most native tools.
    public static string DecodeBlob(byte[] blob)
    {
        if (blob.Length >= 2 && blob.Length % 2 == 0 && blob[1] == 0 && blob[0] != 0)
            return Encoding.Unicode.GetString(blob).TrimEnd('\0');
        return Encoding.UTF8.GetString(blob).TrimEnd('\0');
    }
}

/// An in-memory store for tests and for builds with no Credential Manager.
public sealed class MemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, StoredCredential> _items = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Denied { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Writes { get; private set; }

    public CredentialRead Read(string target)
    {
        lock (_items)
        {
            if (Denied.Contains(target)) return CredentialRead.Denied;
            return _items.TryGetValue(target, out var c)
                ? new CredentialRead(CredentialReadStatus.Found, c) : CredentialRead.NotFound;
        }
    }

    public IReadOnlyList<StoredCredential> Enumerate(string filter)
    {
        var prefix = filter.TrimEnd('*');
        lock (_items)
            return _items.Values.Where(c => c.Target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public bool Write(string target, string userName, byte[] blob)
    {
        lock (_items)
        {
            Writes++;
            _items[target] = new StoredCredential(target, blob.ToArray(), DateTimeOffset.UtcNow);
            return true;
        }
    }

    public bool Delete(string target)
    {
        lock (_items) { _items.Remove(target); return true; }
    }
}
