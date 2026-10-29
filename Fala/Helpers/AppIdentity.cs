using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Fala.Helpers;

internal static class AppIdentity
{
    public const string AppUserModelId = "Fala.PodcastPlayer";
    public const string DisplayName = "Fala";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

    // Musi być wywołane przed utworzeniem pierwszego okna.
    public static void SetProcessId()
    {
        try
        {
            _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        }
        catch
        {
        }
    }

    public static void RegisterShell()
    {
        try
        {
            RegisterShellIdentity();
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn("Rejestracja skrótu i AUMID", ex);
        }
    }

    public static void ApplyAudioSessionName()
    {
        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "icons", "Fala.ico");
            if (!File.Exists(icon))
                icon = Path.Combine(AppContext.BaseDirectory, "Fala.exe");
            AudioSessionName.Set(DisplayName, icon);
        }
        catch
        {
        }
    }

    private static void RegisterShellIdentity()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Fala.exe");
        var icon = Path.Combine(AppContext.BaseDirectory, "icons", "Fala.ico");
        if (!File.Exists(icon))
            icon = exe;

        using (var appKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\Fala.exe"))
        {
            appKey?.SetValue("FriendlyAppName", DisplayName);
        }

        using (var aumid = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppUserModelId))
        {
            aumid?.SetValue("DisplayName", DisplayName);
            aumid?.SetValue("IconUri", icon);
        }

        var programs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs");
        Directory.CreateDirectory(programs);
        ShellShortcut.Create(
            Path.Combine(programs, "Fala.lnk"),
            exe,
            AppContext.BaseDirectory,
            icon,
            AppUserModelId,
            DisplayName);
    }
}
