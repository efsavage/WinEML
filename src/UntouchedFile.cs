using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinEML;

/// <summary>
/// Read files without leaving any trace on them. A viewer must never alter what
/// it views: content and timestamps — including the last-access time, which NTFS
/// would otherwise bump on every read — stay exactly as they were, and generous
/// sharing means we never block another program from the file.
/// </summary>
internal static class UntouchedFile
{
    public static FileStream OpenRead(string path)
    {
        // SetFileTime's "-1" sentinel ("do not update last-access time for
        // operations on this handle") requires FILE_WRITE_ATTRIBUTES access,
        // which FileStream cannot request alongside read — so open via
        // CreateFile. Note: write *attributes*, not write data — the content
        // remains untouchable through this handle.
        var handle = CreateFile(
            path, GENERIC_READ | FILE_WRITE_ATTRIBUTES, FILE_SHARE_ALL,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            long preserveAccessTime = -1;
            SetFileTime(handle, IntPtr.Zero, ref preserveAccessTime, IntPtr.Zero);
            return new FileStream(handle, FileAccess.Read);
        }
        handle.Dispose();

        // Fallback (e.g. an ACL grants read but not write-attributes): plain
        // shared read — the file behaves as with any other reader.
        return new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
    }

    /// <summary>Read all text (BOM-aware, defaulting to UTF-8) without touching the file.</summary>
    public static string ReadAllText(string path)
    {
        using var stream = OpenRead(path);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_WRITE_ATTRIBUTES = 0x0100;
    private const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4; // read | write | delete
    private const uint OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileTime(
        SafeFileHandle hFile, IntPtr lpCreationTime, ref long lpLastAccessTime, IntPtr lpLastWriteTime);
}
