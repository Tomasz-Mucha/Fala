using Fala.Localization;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Fala.Services;

/// <summary>Powiadomienia Windows o nowych odcinkach po automatycznym odświeżeniu.</summary>
public static class NewEpisodeNotifier
{
    private const int MaxListed = 3;
    private static bool _registered;

    /// <summary>
    /// Rejestruje aplikację do powiadomień. <paramref name="onClicked"/> jest wywoływane (z wątku w tle),
    /// gdy użytkownik kliknie powiadomienie przy uruchomionej aplikacji.
    /// </summary>
    public static void Initialize(Action onClicked)
    {
        try
        {
            if (!AppNotificationManager.IsSupported())
                return;
            var manager = AppNotificationManager.Default;
            // Handler musi być podpięty przed Register().
            manager.NotificationInvoked += (_, _) => onClicked();
            var icon = Path.Combine(AppContext.BaseDirectory, "icons", "Fala-icon-C-256.png");
            if (File.Exists(icon))
                manager.Register(Helpers.AppIdentity.DisplayName, new Uri(icon));
            else
                manager.Register();
            _registered = true;
            Helpers.Log.Info("Powiadomienia zarejestrowane");
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn("Rejestracja powiadomień nie powiodła się — powiadomienia wyłączone", ex);
            // System bez obsługi powiadomień — aplikacja działa dalej bez nich.
        }
    }

    public static void Show(RefreshSummary summary)
    {
        if (!_registered || !AppSettings.NewEpisodeNotifications || summary.TotalNew == 0)
            return;
        try
        {
            var L = Loc.Current;
            var lines = summary.WithNew
                .Take(MaxListed)
                .Select(x => L.Format("NotifyShowLine", x.Podcast.Title, x.NewEpisodes))
                .ToList();
            if (summary.WithNew.Count > MaxListed)
                lines.Add(L.Format("NotifyMore", summary.WithNew.Count - MaxListed));

            var notification = new AppNotificationBuilder()
                .AddArgument("action", "library")
                .AddText(L.Format("NotifyTitle", summary.TotalNew))
                .AddText(string.Join(Environment.NewLine, lines))
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
            Helpers.Log.Info($"Pokazano powiadomienie o {summary.TotalNew} nowych odcinkach");
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn("Wyświetlenie powiadomienia", ex);
        }
    }
}
