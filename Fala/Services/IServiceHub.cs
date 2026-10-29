namespace Fala.Services;

public interface IServiceHub
{
    DatabaseService Database { get; }
    RssService Rss { get; }
    CatalogService Catalog { get; }
    PlayerService Player { get; }
    QueueService Queue { get; }
    DownloadService Downloads { get; }
    SubscriptionService Subscriptions { get; }
    FeedRefreshService Refresh { get; }
}

public sealed class ServiceHub : IServiceHub
{
    public DatabaseService Database { get; }
    public RssService Rss { get; }
    public CatalogService Catalog { get; }
    public PlayerService Player { get; }
    public QueueService Queue { get; }
    public DownloadService Downloads { get; }
    public SubscriptionService Subscriptions { get; }
    public FeedRefreshService Refresh { get; }

    private ServiceHub(
        DatabaseService database,
        RssService rss,
        CatalogService catalog,
        PlayerService player,
        QueueService queue,
        DownloadService downloads,
        SubscriptionService subscriptions,
        FeedRefreshService refresh)
    {
        Database = database;
        Rss = rss;
        Catalog = catalog;
        Player = player;
        Queue = queue;
        Downloads = downloads;
        Subscriptions = subscriptions;
        Refresh = refresh;
    }

    public static ServiceHub Create()
    {
        var db = new DatabaseService();
        var rss = new RssService();
        var queue = new QueueService(db);
        var subscriptions = new SubscriptionService(db, rss);
        return new ServiceHub(
            db,
            rss,
            new CatalogService(),
            new PlayerService(db, queue),
            queue,
            new DownloadService(),
            subscriptions,
            new FeedRefreshService(db, subscriptions));
    }
}
