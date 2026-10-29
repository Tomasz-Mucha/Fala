using System.Globalization;
using System.Text;

namespace Fala.Helpers;

/// <summary>
/// Prosty dziennik do pliku: %LOCALAPPDATA%\Fala\logs\fala-RRRRMMDD.log (trzymane 7 dni).
/// Nigdy nie rzuca wyjątków — logowanie nie może zepsuć aplikacji.
/// </summary>
public static class Log
{
    private const int KeepDays = 7;
    private static readonly object Lock = new();
    private static bool _cleaned;

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var now = DateTime.Now;
            var line = new StringBuilder()
                .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append(' ').Append(level.PadRight(5))
                .Append(" [").Append(Environment.CurrentManagedThreadId).Append("] ")
                .Append(message);
            if (ex is not null)
                line.AppendLine().Append("    ").Append(ex.ToString().Replace("\n", "\n    "));
            line.AppendLine();

            lock (Lock)
            {
                var dir = AppPaths.LogsDirectory;
                Directory.CreateDirectory(dir);
                if (!_cleaned)
                {
                    _cleaned = true;
                    Cleanup(dir);
                }
                File.AppendAllText(Path.Combine(dir, $"fala-{now:yyyyMMdd}.log"), line.ToString());
            }
#if DEBUG
            System.Diagnostics.Debug.Write(line.ToString());
#endif
        }
        catch
        {
            // Brak miejsca / uprawnień — trudno, aplikacja działa dalej.
        }
    }

    private static void Cleanup(string dir)
    {
        var cutoff = DateTime.Now.AddDays(-KeepDays);
        foreach (var file in Directory.EnumerateFiles(dir, "fala-*.log"))
        {
            if (File.GetLastWriteTime(file) < cutoff)
                File.Delete(file);
        }
    }
}
