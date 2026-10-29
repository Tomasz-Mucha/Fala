namespace Fala.Helpers;

public static class AppInfo
{
    /// <summary>Wersja aplikacji (z &lt;Version&gt; w Fala.csproj albo z tagu wydania).</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static string UserAgent => $"Fala/{Version} (Windows Podcast App)";
}
