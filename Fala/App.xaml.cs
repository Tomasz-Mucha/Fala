using Microsoft.UI.Xaml;
using Fala.Helpers;
using Fala.Localization;
using Fala.Services;

namespace Fala;

public partial class App : Application
{
    public static MainWindow MainAppWindow { get; private set; } = null!;
    public static IServiceHub Services { get; private set; } = null!;

    public App()
    {
        AppIdentity.SetProcessId();
        RegisterCrashLogging();
        Log.Info($"Start Fala {typeof(App).Assembly.GetName().Version} · {Environment.OSVersion} · " +
                 $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} · dane: {AppPaths.DataDirectory}");
        InitializeComponent();
        AppSettings.Load();
        // Motyw wybrany w ustawieniach ustawiamy dla całej aplikacji jeszcze przed utworzeniem okna.
        // Samo RequestedTheme na elemencie głównym (po utworzeniu okna) zostawiało część kolorów
        // i tło Mica w motywie systemu — np. białe teksty na jasnym tle przy jasnym motywie.
        if (AppSettings.Theme != ElementTheme.Default)
            RequestedTheme = AppSettings.Theme == ElementTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        Loc.Current.SetLanguage(AppSettings.Language);
        Services = ServiceHub.Create();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        await Services.Database.InitializeAsync();
        await Services.Queue.LoadAsync();
        // Kliknięcie powiadomienia o nowych odcinkach (przy działającej aplikacji) otwiera Bibliotekę.
        NewEpisodeNotifier.Initialize(() => MainAppWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            MainAppWindow.Activate();
            MainAppWindow.GoToLibrary();
        }));
        MainAppWindow = new MainWindow();
        AppSettings.Apply(MainAppWindow); // wywołuje też AccentService.Apply()
        MainAppWindow.Activate();
        await RestoreLastEpisodeAsync();
        Services.Refresh.AutoRefreshCompleted += (_, summary) => NewEpisodeNotifier.Show(summary);
        Services.Refresh.Reschedule();
        // Wpisy w rejestrze i skrót w menu Start nie są potrzebne do pokazania okna — poza ścieżką startu.
        _ = Task.Run(AppIdentity.RegisterShell);
        MainAppWindow.Closed += (_, _) => Log.Info("Zamknięcie okna");
        Log.Info("Uruchomiono");
    }

    // Nieobsłużone wyjątki trafiają do dziennika, zanim aplikacja się zamknie — bez tego awaria
    // (np. w async void) znikała bez śladu.
    private void RegisterCrashLogging()
    {
        UnhandledException += (_, e) => Log.Error("Nieobsłużony wyjątek (UI)", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error($"Nieobsłużony wyjątek (proces, kończy działanie: {e.IsTerminating})", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Warn("Nieobserwowany wyjątek zadania", e.Exception);
            e.SetObserved();
        };
    }

    // Po starcie pasek odtwarzacza pokazuje odcinek, na którym skończyła się poprzednia sesja
    // (w pauzie, od zapisanej pozycji).
    private static async Task RestoreLastEpisodeAsync()
    {
        try
        {
            if (await Services.Database.GetLastListenedAsync() is { } last)
                Services.Player.Restore(last.Episode, last.Podcast.Title, last.Podcast.ArtworkUrl);
        }
        catch (Exception ex)
        {
            Log.Warn("Nie udało się przywrócić ostatniego odcinka", ex);
        }
    }
}
