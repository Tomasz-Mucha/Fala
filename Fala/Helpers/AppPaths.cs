namespace Fala.Helpers;

/// <summary>Miejsca na dane aplikacji (baza, ustawienia, pobrane odcinki, logi).</summary>
public static class AppPaths
{
    /// <summary>
    /// Domyślnie %LOCALAPPDATA%\Fala. Zmienna środowiskowa FALA_DATA_DIR przekierowuje wszystkie dane
    /// w inne miejsce — do testów na kopii danych albo do trybu przenośnego.
    /// </summary>
    public static string DataDirectory { get; } = Resolve();

    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    private static string Resolve()
    {
        var overridden = Environment.GetEnvironmentVariable("FALA_DATA_DIR");
        var dir = !string.IsNullOrWhiteSpace(overridden)
            ? Path.GetFullPath(overridden)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fala");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
