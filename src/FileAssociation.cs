using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinEML;

/// <summary>
/// Per-user (HKCU) association for the .eml extension. No admin rights needed,
/// fully reversible. Windows 10/11 protect the *default* choice behind a hashed
/// UserChoice, so the most we can do programmatically is register WinEML as an
/// available handler — the user then confirms it as the default via the standard
/// "Open with" dialog (which <see cref="ShowOpenWith"/> pops for them).
/// </summary>
internal static class FileAssociation
{
    private const string ProgId = "WinEML.eml";
    private const string Ext = ".eml";

    private static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;
    private static string ExeName => Path.GetFileName(ExePath);

    // ---- registry registration (no UI) ----

    public static void RegisterCore()
    {
        string exe = ExePath;

        // 1) A ProgId describing how to open the file.
        using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progId.SetValue("", "EML E-mail Message");
            progId.SetValue("FriendlyTypeName", "EML E-mail Message");
            using (var icon = progId.CreateSubKey("DefaultIcon"))
                icon.SetValue("", $"\"{exe}\",0");
            using (var command = progId.CreateSubKey(@"shell\open\command"))
                command.SetValue("", $"\"{exe}\" \"%1\"");
        }

        // 2) Register the application itself so it appears in "Open with",
        //    scoped to .eml so it shows up specifically for these files.
        using (var app = Registry.CurrentUser.CreateSubKey($@"Software\Classes\Applications\{ExeName}"))
        {
            app.SetValue("FriendlyAppName", "WinEML");
            using (var command = app.CreateSubKey(@"shell\open\command"))
                command.SetValue("", $"\"{exe}\" \"%1\"");
            using (var types = app.CreateSubKey("SupportedTypes"))
                types.SetValue(Ext, "");
        }

        // 3) Offer WinEML as a handler for .eml (and set the classic default,
        //    which takes effect on systems without a protected UserChoice).
        using (var ext = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Ext}"))
        {
            if (ext.GetValue("") is null or "")
                ext.SetValue("", ProgId);
            using var openWith = ext.CreateSubKey("OpenWithProgids");
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        NotifyShell();
    }

    public static void UnregisterCore()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\Applications\{ExeName}", throwOnMissingSubKey: false);

        using (var ext = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{Ext}", writable: true))
        {
            if (ext is not null)
            {
                if (string.Equals(ext.GetValue("") as string, ProgId, StringComparison.OrdinalIgnoreCase))
                    ext.SetValue("", "");
                using var openWith = ext.OpenSubKey("OpenWithProgids", writable: true);
                openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
            }
        }

        NotifyShell();
    }

    // ---- CLI entry points (show a simple confirmation) ----

    public static void Register()
    {
        RegisterCore();
        MessageBox.Show(
            "WinEML is now registered as a handler for .eml files.\n\n" +
            "To make it the default, right-click any .eml file → Open with → " +
            "Choose another app → WinEML, and tick \"Always use this app\".",
            "WinEML", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    public static void Unregister()
    {
        UnregisterCore();
        MessageBox.Show("WinEML has been unregistered from .eml files.",
            "WinEML", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ---- "Open with" chooser (lets the user set the default the sanctioned way) ----

    /// <summary>
    /// Show Windows' own "How do you want to open this?" dialog for a file. Picking
    /// WinEML there (with the box ticked) is the only way to set the default that
    /// Windows' UserChoice protection permits.
    /// </summary>
    public static void ShowOpenWith(nint hwnd, string filePath)
    {
        var info = new OpenAsInfo
        {
            FileName = filePath,
            FileClass = null,
            InFlags = OAIF_ALLOW_REGISTRATION | OAIF_REGISTER_EXT,
        };
        try { SHOpenWithDialog(hwnd, ref info); } catch { /* user cancelled */ }
    }

    private const int OAIF_ALLOW_REGISTRATION = 0x01;
    private const int OAIF_REGISTER_EXT = 0x02;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string FileName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? FileClass;
        public int InFlags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHOpenWithDialog(nint hwndParent, ref OpenAsInfo info);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);

    private static void NotifyShell()
    {
        const int SHCNE_ASSOCCHANGED = 0x08000000;
        const uint SHCNF_IDLIST = 0x0000;
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, 0, 0);
    }
}
