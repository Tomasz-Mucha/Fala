using Fala.Models;
using Microsoft.Data.Sqlite;

namespace Fala.Services;

public sealed class DatabaseService
{
    public string DataDirectory { get; }
    private readonly string _path;

    public DatabaseService()
    {
        DataDirectory = Helpers.AppPaths.DataDirectory;
        _path = Path.Combine(DataDirectory, "fala.db");
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection($"Data Source={_path}");
        c.Open();
        using var pragma = c.CreateCommand();
        // journal_mode = WAL jest trwałe w pliku bazy — ustawiane raz w InitializeAsync.
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return c;
    }

    // Daty zapisujemy zawsze w UTC, żeby sortowanie tekstowe (ORDER BY) było poprawne.
    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("o");

    private static DateTimeOffset ParseIso(string value)
        => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    public async Task InitializeAsync()
    {
        await using var db = Open();
        var wal = db.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode = WAL;";
        await wal.ExecuteNonQueryAsync();

        var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Podcasts (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Title TEXT NOT NULL,
                Author TEXT NOT NULL DEFAULT '',
                FeedUrl TEXT NOT NULL UNIQUE,
                ArtworkUrl TEXT NOT NULL DEFAULT '',
                Description TEXT NOT NULL DEFAULT '',
                AddedUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Episodes (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PodcastId INTEGER NOT NULL,
                Guid TEXT NOT NULL,
                Title TEXT NOT NULL,
                AudioUrl TEXT NOT NULL,
                Description TEXT NOT NULL DEFAULT '',
                PublishedUtc TEXT,
                DurationTicks INTEGER,
                PositionTicks INTEGER NOT NULL DEFAULT 0,
                IsPlayed INTEGER NOT NULL DEFAULT 0,
                LocalPath TEXT,
                UNIQUE(PodcastId, Guid),
                FOREIGN KEY (PodcastId) REFERENCES Podcasts(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_Episodes_PodcastId ON Episodes(PodcastId);
            CREATE TABLE IF NOT EXISTS Groups (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                CreatedUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS GroupPodcasts (
                GroupId INTEGER NOT NULL,
                PodcastId INTEGER NOT NULL,
                PRIMARY KEY (GroupId, PodcastId),
                FOREIGN KEY (GroupId) REFERENCES Groups(Id) ON DELETE CASCADE,
                FOREIGN KEY (PodcastId) REFERENCES Podcasts(Id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS History (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                EpisodeId INTEGER,
                PodcastId INTEGER,
                ShowTitle TEXT NOT NULL,
                EpisodeTitle TEXT NOT NULL,
                ArtworkUrl TEXT NOT NULL DEFAULT '',
                HeardUtc TEXT NOT NULL,
                IsManual INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (EpisodeId) REFERENCES Episodes(Id) ON DELETE SET NULL,
                FOREIGN KEY (PodcastId) REFERENCES Podcasts(Id) ON DELETE SET NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_History_EpisodeId ON History(EpisodeId) WHERE EpisodeId IS NOT NULL;
            CREATE TABLE IF NOT EXISTS Bookmarks (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                EpisodeId INTEGER,
                PodcastId INTEGER,
                Title TEXT NOT NULL,
                Comment TEXT NOT NULL DEFAULT '',
                PositionTicks INTEGER NOT NULL DEFAULT 0,
                ShowTitle TEXT NOT NULL DEFAULT '',
                EpisodeTitle TEXT NOT NULL DEFAULT '',
                ArtworkUrl TEXT NOT NULL DEFAULT '',
                CreatedUtc TEXT NOT NULL,
                FOREIGN KEY (EpisodeId) REFERENCES Episodes(Id) ON DELETE SET NULL,
                FOREIGN KEY (PodcastId) REFERENCES Podcasts(Id) ON DELETE SET NULL
            );
            CREATE TABLE IF NOT EXISTS Queue (
                Position INTEGER NOT NULL,
                EpisodeId INTEGER NOT NULL,
                ShowTitle TEXT NOT NULL DEFAULT '',
                ArtworkUrl TEXT NOT NULL DEFAULT '',
                FOREIGN KEY (EpisodeId) REFERENCES Episodes(Id) ON DELETE CASCADE
            );
            """;
        await cmd.ExecuteNonQueryAsync();
        await EnsureColumnAsync(db, "Episodes", "LastHeardUtc", "TEXT");
        await EnsureColumnAsync(db, "Episodes", "IsNew", "INTEGER NOT NULL DEFAULT 0");
        var newIndex = db.CreateCommand();
        newIndex.CommandText = "CREATE INDEX IF NOT EXISTS IX_Episodes_New ON Episodes(PodcastId) WHERE IsNew = 1;";
        await newIndex.ExecuteNonQueryAsync();
        await MigrateAsync(db);
    }

    private static async Task MigrateAsync(SqliteConnection db)
    {
        var get = db.CreateCommand();
        get.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt64(await get.ExecuteScalarAsync());

        if (version < 1)
        {
            // Starsze wersje zapisywały daty z lokalnym offsetem (np. +02:00, -05:00),
            // przez co ORDER BY po tekście mieszał kolejność. Normalizujemy do UTC.
            await using var tx = (SqliteTransaction)await db.BeginTransactionAsync();
            var fix = db.CreateCommand();
            fix.Transaction = tx;
            fix.CommandText = """
                UPDATE Episodes
                SET PublishedUtc = strftime('%Y-%m-%dT%H:%M:%S', PublishedUtc) || '.0000000+00:00'
                WHERE PublishedUtc IS NOT NULL AND PublishedUtc NOT LIKE '%+00:00'
                  AND strftime('%s', PublishedUtc) IS NOT NULL;
                UPDATE History
                SET HeardUtc = strftime('%Y-%m-%dT%H:%M:%S', HeardUtc) || '.0000000+00:00'
                WHERE HeardUtc NOT LIKE '%+00:00' AND strftime('%s', HeardUtc) IS NOT NULL;
                PRAGMA user_version = 1;
                """;
            await fix.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }
    }

    private static async Task EnsureColumnAsync(SqliteConnection db, string table, string column, string type)
    {
        var info = db.CreateCommand();
        info.CommandText = $"PRAGMA table_info({table});";
        await using var r = await info.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }
        var alter = db.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};";
        await alter.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<Podcast>> GetPodcastsAsync()
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT p.Id, p.Title, p.Author, p.FeedUrl, p.ArtworkUrl, p.Description, p.AddedUtc,
                   (SELECT COUNT(*) FROM Episodes e WHERE e.PodcastId = p.Id AND e.IsNew = 1)
            FROM Podcasts p
            ORDER BY p.Title;
            """;
        var list = new List<Podcast>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(ReadPodcast(r));
        return list;
    }

    public async Task<Podcast?> GetPodcastByIdAsync(long id)
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Title, Author, FeedUrl, ArtworkUrl, Description, AddedUtc
            FROM Podcasts WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        if (await r.ReadAsync())
            return ReadPodcast(r);
        return null;
    }

    public async Task<Podcast?> FindPodcastByFeedUrlAsync(string feedUrl)
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Title, Author, FeedUrl, ArtworkUrl, Description, AddedUtc
            FROM Podcasts
            WHERE lower(rtrim(FeedUrl, '/')) = lower(rtrim($f, '/'));
            """;
        cmd.Parameters.AddWithValue("$f", feedUrl.Trim());
        await using var r = await cmd.ExecuteReaderAsync();
        if (await r.ReadAsync())
            return ReadPodcast(r);
        return null;
    }

    public async Task<Podcast> UpdatePodcastAsync(Podcast p)
    {
        await using var db = Open();
        try
        {
            var cmd = db.CreateCommand();
            cmd.CommandText = """
                UPDATE Podcasts
                SET Title = $t, Author = $a, FeedUrl = $f, ArtworkUrl = $art, Description = $d
                WHERE Id = $id
                RETURNING Id, Title, Author, FeedUrl, ArtworkUrl, Description, AddedUtc;
                """;
            cmd.Parameters.AddWithValue("$t", p.Title);
            cmd.Parameters.AddWithValue("$a", p.Author);
            cmd.Parameters.AddWithValue("$f", p.FeedUrl);
            cmd.Parameters.AddWithValue("$art", p.ArtworkUrl);
            cmd.Parameters.AddWithValue("$d", p.Description);
            cmd.Parameters.AddWithValue("$id", p.Id);
            await using var r = await cmd.ExecuteReaderAsync();
            await r.ReadAsync();
            return ReadPodcast(r);
        }
        catch (SqliteException)
        {
            var cmd = db.CreateCommand();
            cmd.CommandText = """
                UPDATE Podcasts
                SET Title = $t, Author = $a, ArtworkUrl = $art, Description = $d
                WHERE Id = $id
                RETURNING Id, Title, Author, FeedUrl, ArtworkUrl, Description, AddedUtc;
                """;
            cmd.Parameters.AddWithValue("$t", p.Title);
            cmd.Parameters.AddWithValue("$a", p.Author);
            cmd.Parameters.AddWithValue("$art", p.ArtworkUrl);
            cmd.Parameters.AddWithValue("$d", p.Description);
            cmd.Parameters.AddWithValue("$id", p.Id);
            await using var r = await cmd.ExecuteReaderAsync();
            await r.ReadAsync();
            return ReadPodcast(r);
        }
    }

    public async Task<Podcast> UpsertPodcastAsync(Podcast p)
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Podcasts (Title, Author, FeedUrl, ArtworkUrl, Description, AddedUtc)
            VALUES ($t, $a, $f, $art, $d, $added)
            ON CONFLICT(FeedUrl) DO UPDATE SET
                Title = excluded.Title,
                Author = excluded.Author,
                ArtworkUrl = excluded.ArtworkUrl,
                Description = excluded.Description
            RETURNING Id, Title, Author, FeedUrl, ArtworkUrl, Description, AddedUtc;
            """;
        cmd.Parameters.AddWithValue("$t", p.Title);
        cmd.Parameters.AddWithValue("$a", p.Author);
        cmd.Parameters.AddWithValue("$f", p.FeedUrl);
        cmd.Parameters.AddWithValue("$art", p.ArtworkUrl);
        cmd.Parameters.AddWithValue("$d", p.Description);
        cmd.Parameters.AddWithValue("$added", Iso(p.AddedUtc));
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return ReadPodcast(r);
    }

    public async Task DeletePodcastAsync(long id)
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM Podcasts WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Zapisuje odcinki kanału i zwraca liczbę odcinków dodanych (a nie zaktualizowanych).
    /// <paramref name="markNew"/>: dodane odcinki dostają flagę „nowy” (odświeżenie istniejącej subskrypcji).
    /// </summary>
    public async Task<int> UpsertEpisodesAsync(long podcastId, IEnumerable<Episode> episodes, bool markNew = false)
    {
        await using var db = Open();
        await using var tx = (SqliteTransaction)await db.BeginTransactionAsync();
        var before = await CountEpisodesAsync(db, tx, podcastId);
        // Jedno przygotowane polecenie dla wszystkich odcinków zamiast kompilowania SQL w każdej iteracji.
        await using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO Episodes (PodcastId, Guid, Title, AudioUrl, Description, PublishedUtc, DurationTicks, IsNew)
            VALUES ($p, $g, $t, $u, $d, $pub, $dur, $new)
            ON CONFLICT(PodcastId, Guid) DO UPDATE SET
                Title = excluded.Title,
                AudioUrl = excluded.AudioUrl,
                Description = excluded.Description,
                PublishedUtc = excluded.PublishedUtc,
                DurationTicks = excluded.DurationTicks;
            """;
        cmd.Parameters.AddWithValue("$p", podcastId);
        cmd.Parameters.AddWithValue("$new", markNew ? 1 : 0);
        var guid = cmd.Parameters.Add("$g", SqliteType.Text);
        var title = cmd.Parameters.Add("$t", SqliteType.Text);
        var audio = cmd.Parameters.Add("$u", SqliteType.Text);
        var desc = cmd.Parameters.Add("$d", SqliteType.Text);
        var pub = cmd.Parameters.Add("$pub", SqliteType.Text);
        var dur = cmd.Parameters.Add("$dur", SqliteType.Integer);
        cmd.Prepare();

        foreach (var e in episodes)
        {
            guid.Value = e.Guid;
            title.Value = e.Title;
            audio.Value = e.AudioUrl;
            desc.Value = e.Description;
            pub.Value = e.PublishedUtc is { } p ? Iso(p) : DBNull.Value;
            dur.Value = e.Duration?.Ticks ?? (object)DBNull.Value;
            await cmd.ExecuteNonQueryAsync();
        }
        var after = await CountEpisodesAsync(db, tx, podcastId);
        await tx.CommitAsync();
        return (int)Math.Max(0, after - before);
    }

    private static async Task<long> CountEpisodesAsync(SqliteConnection db, SqliteTransaction tx, long podcastId)
    {
        var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM Episodes WHERE PodcastId = $p;";
        cmd.Parameters.AddWithValue("$p", podcastId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task<int> CountNewEpisodesAsync()
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Episodes WHERE IsNew = 1;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task ClearNewAsync(long podcastId)
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE Episodes SET IsNew = 0 WHERE PodcastId = $p AND IsNew = 1;";
        cmd.Parameters.AddWithValue("$p", podcastId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SetAllPlayedAsync(long podcastId, bool isPlayed)
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE Episodes SET IsPlayed = $v, IsNew = 0 WHERE PodcastId = $p;";
        cmd.Parameters.AddWithValue("$v", isPlayed ? 1 : 0);
        cmd.Parameters.AddWithValue("$p", podcastId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<Episode>> GetEpisodesAsync(long podcastId)
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT Id, PodcastId, Guid, Title, AudioUrl, Description, PublishedUtc, DurationTicks, PositionTicks, IsPlayed, LocalPath,
                   IsNew
            FROM Episodes WHERE PodcastId = $p
            ORDER BY PublishedUtc DESC;
            """;
        cmd.Parameters.AddWithValue("$p", podcastId);
        var list = new List<Episode>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var episode = ReadEpisode(r);
            episode.IsNew = r.GetInt64(11) != 0;
            list.Add(episode);
        }
        return list;
    }

    public async Task<IReadOnlyList<(Episode Episode, Podcast Podcast)>> GetRecentEpisodesAsync(int take = 30)
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT e.Id, e.PodcastId, e.Guid, e.Title, e.AudioUrl, e.Description, e.PublishedUtc,
                   e.DurationTicks, e.PositionTicks, e.IsPlayed, e.LocalPath,
                   p.Id, p.Title, p.Author, p.FeedUrl, p.ArtworkUrl, p.Description, p.AddedUtc
            FROM Episodes e
            JOIN Podcasts p ON p.Id = e.PodcastId
            ORDER BY e.PublishedUtc DESC
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$n", take);
        return await ReadJoinedAsync(cmd);
    }

    public async Task<IReadOnlyList<(Episode Episode, Podcast Podcast)>> GetContinueListeningAsync(int take = 8)
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT e.Id, e.PodcastId, e.Guid, e.Title, e.AudioUrl, e.Description, e.PublishedUtc,
                   e.DurationTicks, e.PositionTicks, e.IsPlayed, e.LocalPath,
                   p.Id, p.Title, p.Author, p.FeedUrl, p.ArtworkUrl, p.Description, p.AddedUtc
            FROM Episodes e
            JOIN Podcasts p ON p.Id = e.PodcastId
            WHERE e.IsPlayed = 0 AND e.PositionTicks > 30000000
            ORDER BY (e.LastHeardUtc IS NULL), e.LastHeardUtc DESC
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$n", take);
        return await ReadJoinedAsync(cmd);
    }

    /// <summary>Ostatnio słuchany odcinek, o ile nie został dosłuchany do końca.</summary>
    public async Task<(Episode Episode, Podcast Podcast)?> GetLastListenedAsync()
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT e.Id, e.PodcastId, e.Guid, e.Title, e.AudioUrl, e.Description, e.PublishedUtc,
                   e.DurationTicks, e.PositionTicks, e.IsPlayed, e.LocalPath,
                   p.Id, p.Title, p.Author, p.FeedUrl, p.ArtworkUrl, p.Description, p.AddedUtc
            FROM Episodes e
            JOIN Podcasts p ON p.Id = e.PodcastId
            WHERE e.LastHeardUtc IS NOT NULL
            ORDER BY e.LastHeardUtc DESC
            LIMIT 1;
            """;
        var rows = await ReadJoinedAsync(cmd);
        if (rows.Count == 0 || rows[0].Item1.IsPlayed)
            return null;
        return rows[0];
    }

    public async Task<IReadOnlyList<QueueItem>> GetQueueAsync()
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT e.Id, e.PodcastId, e.Guid, e.Title, e.AudioUrl, e.Description, e.PublishedUtc,
                   e.DurationTicks, e.PositionTicks, e.IsPlayed, e.LocalPath,
                   q.ShowTitle, q.ArtworkUrl
            FROM Queue q
            JOIN Episodes e ON e.Id = q.EpisodeId
            ORDER BY q.Position;
            """;
        var list = new List<QueueItem>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new QueueItem
            {
                Episode = ReadEpisode(r),
                ShowTitle = r.GetString(11),
                ArtworkUrl = r.GetString(12)
            });
        }
        return list;
    }

    public async Task SaveQueueAsync(IReadOnlyList<(long EpisodeId, string ShowTitle, string ArtworkUrl)> items)
    {
        await using var db = Open();
        await using var tx = (SqliteTransaction)await db.BeginTransactionAsync();
        var clear = db.CreateCommand();
        clear.Transaction = tx;
        clear.CommandText = "DELETE FROM Queue;";
        await clear.ExecuteNonQueryAsync();

        await using var ins = db.CreateCommand();
        ins.Transaction = tx;
        // SELECT … WHERE Id = $e zamiast VALUES: pomija odcinki usunięte w międzyczasie z biblioteki
        // (np. razem z podcastem), zamiast wywracać cały zapis na kluczu obcym.
        ins.CommandText = """
            INSERT INTO Queue (Position, EpisodeId, ShowTitle, ArtworkUrl)
            SELECT $pos, Id, $show, $art FROM Episodes WHERE Id = $e;
            """;
        var pos = ins.Parameters.Add("$pos", SqliteType.Integer);
        var ep = ins.Parameters.Add("$e", SqliteType.Integer);
        var show = ins.Parameters.Add("$show", SqliteType.Text);
        var art = ins.Parameters.Add("$art", SqliteType.Text);
        for (var i = 0; i < items.Count; i++)
        {
            pos.Value = i;
            ep.Value = items[i].EpisodeId;
            show.Value = items[i].ShowTitle;
            art.Value = items[i].ArtworkUrl;
            await ins.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    public async Task SaveProgressAsync(long episodeId, TimeSpan position, bool isPlayed)
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE Episodes
            SET PositionTicks = $pos, IsPlayed = $played, LastHeardUtc = $t
            WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$pos", position.Ticks);
        cmd.Parameters.AddWithValue("$played", isPlayed ? 1 : 0);
        cmd.Parameters.AddWithValue("$t", Iso(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$id", episodeId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RecordHistoryAsync(
        Episode episode,
        string showTitle,
        string? artworkUrl = null,
        bool manual = false)
    {
        if (episode.Id == 0)
            return;
        await using var db = Open();
        var cmd = db.CreateCommand();
        // IX_History_EpisodeId jest indeksem częściowym, więc cel konfliktu musi powtórzyć jego WHERE —
        // bez tego SQLite zgłasza „ON CONFLICT clause does not match any PRIMARY KEY or UNIQUE constraint”.
        cmd.CommandText = """
            INSERT INTO History (EpisodeId, PodcastId, ShowTitle, EpisodeTitle, ArtworkUrl, HeardUtc, IsManual)
            VALUES ($e, $p, $show, $title, $art, $t, $m)
            ON CONFLICT(EpisodeId) WHERE EpisodeId IS NOT NULL DO UPDATE SET
                PodcastId = excluded.PodcastId,
                ShowTitle = excluded.ShowTitle,
                EpisodeTitle = excluded.EpisodeTitle,
                ArtworkUrl = excluded.ArtworkUrl,
                HeardUtc = excluded.HeardUtc,
                IsManual = MAX(History.IsManual, excluded.IsManual);
            """;
        cmd.Parameters.AddWithValue("$e", episode.Id);
        cmd.Parameters.AddWithValue("$p", episode.PodcastId);
        cmd.Parameters.AddWithValue("$show", showTitle);
        cmd.Parameters.AddWithValue("$title", episode.Title);
        cmd.Parameters.AddWithValue("$art", artworkUrl ?? "");
        cmd.Parameters.AddWithValue("$t", Iso(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$m", manual ? 1 : 0);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task AddCustomHistoryAsync(string showTitle, string episodeTitle, DateTimeOffset heardUtc, string? artworkUrl = null)
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO History (EpisodeId, PodcastId, ShowTitle, EpisodeTitle, ArtworkUrl, HeardUtc, IsManual)
            VALUES (NULL, NULL, $show, $title, $art, $t, 1);
            """;
        cmd.Parameters.AddWithValue("$show", showTitle.Trim());
        cmd.Parameters.AddWithValue("$title", episodeTitle.Trim());
        cmd.Parameters.AddWithValue("$art", artworkUrl ?? "");
        cmd.Parameters.AddWithValue("$t", Iso(heardUtc));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<HistoryItem>> GetHistoryAsync(int take = 300)
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT h.Id, h.EpisodeId, h.PodcastId, h.ShowTitle, h.EpisodeTitle, h.ArtworkUrl, h.HeardUtc, h.IsManual,
                   e.AudioUrl, e.LocalPath, p.ArtworkUrl
            FROM History h
            LEFT JOIN Episodes e ON e.Id = h.EpisodeId
            LEFT JOIN Podcasts p ON p.Id = COALESCE(h.PodcastId, e.PodcastId)
            ORDER BY h.HeardUtc DESC
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$n", take);
        var list = new List<HistoryItem>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var art = r.IsDBNull(5) ? "" : r.GetString(5);
            if (string.IsNullOrWhiteSpace(art) && !r.IsDBNull(10))
                art = r.GetString(10);
            list.Add(new HistoryItem
            {
                Id = r.GetInt64(0),
                EpisodeId = r.IsDBNull(1) ? null : r.GetInt64(1),
                PodcastId = r.IsDBNull(2) ? null : r.GetInt64(2),
                ShowTitle = r.GetString(3),
                EpisodeTitle = r.GetString(4),
                ArtworkUrl = art,
                HeardUtc = ParseIso(r.GetString(6)),
                IsManual = r.GetInt64(7) != 0,
                AudioUrl = r.IsDBNull(8) ? null : r.GetString(8),
                LocalPath = r.IsDBNull(9) ? null : r.GetString(9)
            });
        }
        return list;
    }

    public async Task DeleteHistoryAsync(long id)
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM History WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ClearHistoryAsync()
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM History;";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<Episode?> GetEpisodeByIdAsync(long id)
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT Id, PodcastId, Guid, Title, AudioUrl, Description, PublishedUtc, DurationTicks, PositionTicks, IsPlayed, LocalPath
            FROM Episodes WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        if (await r.ReadAsync())
            return ReadEpisode(r);
        return null;
    }

    public async Task<Bookmark> AddBookmarkAsync(Bookmark b)
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Bookmarks (EpisodeId, PodcastId, Title, Comment, PositionTicks, ShowTitle, EpisodeTitle, ArtworkUrl, CreatedUtc)
            VALUES ($e, $p, $title, $c, $pos, $show, $ep, $art, $t)
            RETURNING Id, EpisodeId, PodcastId, Title, Comment, PositionTicks, ShowTitle, EpisodeTitle, ArtworkUrl, CreatedUtc;
            """;
        cmd.Parameters.AddWithValue("$e", (object?)b.EpisodeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$p", (object?)b.PodcastId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$title", b.Title.Trim());
        cmd.Parameters.AddWithValue("$c", b.Comment ?? "");
        cmd.Parameters.AddWithValue("$pos", b.Position.Ticks);
        cmd.Parameters.AddWithValue("$show", b.ShowTitle);
        cmd.Parameters.AddWithValue("$ep", b.EpisodeTitle);
        cmd.Parameters.AddWithValue("$art", b.ArtworkUrl ?? "");
        cmd.Parameters.AddWithValue("$t", Iso(DateTimeOffset.UtcNow));
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return ReadBookmark(r);
    }

    public async Task UpdateBookmarkAsync(Bookmark b)
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE Bookmarks
            SET Title = $title, Comment = $c, PositionTicks = $pos
            WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$title", b.Title.Trim());
        cmd.Parameters.AddWithValue("$c", b.Comment ?? "");
        cmd.Parameters.AddWithValue("$pos", b.Position.Ticks);
        cmd.Parameters.AddWithValue("$id", b.Id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteBookmarkAsync(long id)
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM Bookmarks WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<Bookmark>> GetBookmarksAsync()
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT b.Id, b.EpisodeId, b.PodcastId, b.Title, b.Comment, b.PositionTicks,
                   b.ShowTitle, b.EpisodeTitle, b.ArtworkUrl, b.CreatedUtc,
                   COALESCE(NULLIF(b.ArtworkUrl, ''), p.ArtworkUrl) AS Art
            FROM Bookmarks b
            LEFT JOIN Podcasts p ON p.Id = b.PodcastId
            ORDER BY b.CreatedUtc DESC;
            """;
        var list = new List<Bookmark>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(ReadBookmark(r));
        return list;
    }

    private static Bookmark ReadBookmark(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        EpisodeId = r.IsDBNull(1) ? null : r.GetInt64(1),
        PodcastId = r.IsDBNull(2) ? null : r.GetInt64(2),
        Title = r.GetString(3),
        Comment = r.GetString(4),
        Position = TimeSpan.FromTicks(r.GetInt64(5)),
        ShowTitle = r.GetString(6),
        EpisodeTitle = r.GetString(7),
        ArtworkUrl = r.FieldCount > 10 && !r.IsDBNull(10) ? r.GetString(10) : r.GetString(8),
        CreatedUtc = ParseIso(r.GetString(9))
    };

    public async Task SetPlayedAsync(long episodeId, bool isPlayed)
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE Episodes SET IsPlayed = $v WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$v", isPlayed ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", episodeId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SetLocalPathAsync(long episodeId, string? path)
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE Episodes SET LocalPath = $p WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$p", (object?)path ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", episodeId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<PodcastGroup>> GetGroupsAsync()
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, SortOrder FROM Groups ORDER BY SortOrder, Name;";
        var list = new List<PodcastGroup>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new PodcastGroup
            {
                Id = r.GetInt64(0),
                Name = r.GetString(1),
                SortOrder = r.GetInt32(2)
            });
        }
        return list;
    }

    public async Task<PodcastGroup?> GetGroupAsync(long id)
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, SortOrder FROM Groups WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync())
            return null;
        return new PodcastGroup
        {
            Id = r.GetInt64(0),
            Name = r.GetString(1),
            SortOrder = r.GetInt32(2)
        };
    }

    public async Task<PodcastGroup> CreateGroupAsync(string name)
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Groups (Name, SortOrder, CreatedUtc)
            VALUES ($n, COALESCE((SELECT MAX(SortOrder) FROM Groups), 0) + 1, $t)
            RETURNING Id, Name, SortOrder;
            """;
        cmd.Parameters.AddWithValue("$n", name.Trim());
        cmd.Parameters.AddWithValue("$t", Iso(DateTimeOffset.UtcNow));
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return new PodcastGroup
        {
            Id = r.GetInt64(0),
            Name = r.GetString(1),
            SortOrder = r.GetInt32(2)
        };
    }

    public async Task RenameGroupAsync(long id, string name)
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE Groups SET Name = $n WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$n", name.Trim());
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteGroupAsync(long id)
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM Groups WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<Podcast>> GetGroupPodcastsAsync(long groupId)
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT p.Id, p.Title, p.Author, p.FeedUrl, p.ArtworkUrl, p.Description, p.AddedUtc,
                   (SELECT COUNT(*) FROM Episodes e WHERE e.PodcastId = p.Id AND e.IsNew = 1)
            FROM Podcasts p
            JOIN GroupPodcasts g ON g.PodcastId = p.Id
            WHERE g.GroupId = $g
            ORDER BY p.Title;
            """;
        cmd.Parameters.AddWithValue("$g", groupId);
        var list = new List<Podcast>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(ReadPodcast(r));
        return list;
    }

    public async Task AddPodcastToGroupAsync(long groupId, long podcastId)
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO GroupPodcasts (GroupId, PodcastId)
            VALUES ($g, $p);
            """;
        cmd.Parameters.AddWithValue("$g", groupId);
        cmd.Parameters.AddWithValue("$p", podcastId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RemovePodcastFromGroupAsync(long groupId, long podcastId)
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM GroupPodcasts WHERE GroupId = $g AND PodcastId = $p;";
        cmd.Parameters.AddWithValue("$g", groupId);
        cmd.Parameters.AddWithValue("$p", podcastId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<HashSet<long>> GetGroupIdsForPodcastAsync(long podcastId)
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT GroupId FROM GroupPodcasts WHERE PodcastId = $p;";
        cmd.Parameters.AddWithValue("$p", podcastId);
        var set = new HashSet<long>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            set.Add(r.GetInt64(0));
        return set;
    }

    public async Task<bool> GroupNameExistsAsync(string name, long? exceptId = null)
    {
        await using var db = Open();
        var cmd = db.CreateCommand();
        cmd.CommandText = exceptId is null
            ? "SELECT 1 FROM Groups WHERE lower(Name) = lower($n) LIMIT 1;"
            : "SELECT 1 FROM Groups WHERE lower(Name) = lower($n) AND Id <> $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$n", name.Trim());
        if (exceptId is long id)
            cmd.Parameters.AddWithValue("$id", id);
        var val = await cmd.ExecuteScalarAsync();
        return val is not null && val is not DBNull;
    }

    private static async Task<List<(Episode, Podcast)>> ReadJoinedAsync(SqliteCommand cmd)
    {
        var list = new List<(Episode, Podcast)>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var ep = ReadEpisode(r);
            var pod = new Podcast
            {
                Id = r.GetInt64(11),
                Title = r.GetString(12),
                Author = r.GetString(13),
                FeedUrl = r.GetString(14),
                ArtworkUrl = r.GetString(15),
                Description = r.GetString(16),
                AddedUtc = ParseIso(r.GetString(17))
            };
            list.Add((ep, pod));
        }
        return list;
    }

    private static Podcast ReadPodcast(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Title = r.GetString(1),
        Author = r.GetString(2),
        FeedUrl = r.GetString(3),
        ArtworkUrl = r.GetString(4),
        Description = r.GetString(5),
        AddedUtc = ParseIso(r.GetString(6)),
        // Kolumna 7 (liczba nowych odcinków) występuje tylko w zapytaniach listy podcastów.
        NewCount = r.FieldCount > 7 ? r.GetInt32(7) : 0
    };

    private static Episode ReadEpisode(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        PodcastId = r.GetInt64(1),
        Guid = r.GetString(2),
        Title = r.GetString(3),
        AudioUrl = r.GetString(4),
        Description = r.GetString(5),
        PublishedUtc = r.IsDBNull(6) ? null : ParseIso(r.GetString(6)),
        Duration = r.IsDBNull(7) ? null : TimeSpan.FromTicks(r.GetInt64(7)),
        Position = TimeSpan.FromTicks(r.GetInt64(8)),
        IsPlayed = r.GetInt64(9) != 0,
        LocalPath = r.IsDBNull(10) ? null : r.GetString(10)
    };
}
