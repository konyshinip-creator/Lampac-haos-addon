using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LampacHub;

/// <summary>
/// Закладки профиля в базе модуля Sync (database/Sync.sql, таблица bookmarks): строка на карточку,
/// в categories — членство и порядок {"look": метка, ...}, updated_at — курсор, по которому
/// bookmark.js забирает изменения. Пишем так же, как сам Sync: под его семафорами, с меткой выше
/// всех прежних, а удалённое оставляем пустым «надгробием» {} — иначе клиенты вернут карточку.
/// </summary>
public static class SyncBookmarks
{
    const string FilePath = "database/Sync.sql";

    /// <summary>Категории Lampa, которые хранит Sync</summary>
    public static readonly string[] Categories = { "history", "like", "watch", "wath", "book", "look", "viewed", "scheduled", "continued", "thrown" };

    /// <summary>Статусы Lampa: у карточки не больше одного (так же делает Lampa.Favorite.toggle)</summary>
    public static readonly string[] Statuses = { "look", "viewed", "scheduled", "continued", "thrown" };

    static readonly string _connection = new SqliteConnectionStringBuilder
    {
        DataSource = FilePath,
        Cache = SqliteCacheMode.Shared,
        DefaultTimeout = 10,
        Pooling = true
    }.ToString();

    public static bool Available => File.Exists(FilePath);

    public class Row
    {
        public string card_id { get; set; }
        public JObject card { get; set; }
        public JObject categories { get; set; }
        public long updated_at { get; set; }

        internal bool exists;
        internal bool changed;

        public bool Has(string category) => categories != null && categories.ContainsKey(category);

        public IEnumerable<string> Names => categories?.Properties().Select(p => p.Name) ?? Enumerable.Empty<string>();

        public void Add(string category, long stamp)
        {
            categories ??= new JObject();
            if (!categories.ContainsKey(category))
            {
                categories[category] = stamp;
                changed = true;
            }
        }

        public void Remove(string category)
        {
            if (categories != null && categories.Remove(category))
                changed = true;
        }

        public void SetCard(JObject value)
        {
            if (value == null)
                return;

            if (card == null || !JToken.DeepEquals(card, value))
            {
                card = value;
                changed = true;
            }
        }
    }

    public static List<Row> Load(string area)
    {
        var list = new List<Row>();
        if (!Available || string.IsNullOrEmpty(area))
            return list;

        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT card_id, card, categories, updated_at FROM bookmarks WHERE user = $u";
        cmd.Parameters.AddWithValue("$u", area);

        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadRow(r));

        return list;
    }

    /// <summary>
    /// Правка закладок области. edit получает строки по card_id и метку, которую ставить новым
    /// членствам; новые строки добавляет через rows[id] = new Row { card_id, card }.
    /// </summary>
    public static async Task<int> Modify(string area, Action<Dictionary<string, Row>, long> edit)
    {
        if (!Available || string.IsNullOrEmpty(area))
            return 0;

        // Те же ключи, что у BookmarkController: общий — для set/add/remove, по пользователю — для sync
        var global = new SemaphorManager("Sync", TimeSpan.FromSeconds(30));
        var user = new SemaphorManager($"Sync:{area}", TimeSpan.FromSeconds(30));

        if (!await global.WaitAsync())
            throw new TimeoutException("Sync занят, попробуйте ещё раз");

        try
        {
            if (!await user.WaitAsync())
                throw new TimeoutException("Sync занят, попробуйте ещё раз");

            try
            {
                return Apply(area, edit);
            }
            finally
            {
                user.Release();
            }
        }
        finally
        {
            global.Release();
        }
    }

    static int Apply(string area, Action<Dictionary<string, Row>, long> edit)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();

        var rows = new Dictionary<string, Row>(StringComparer.Ordinal);
        long last = 0;

        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT card_id, card, categories, updated_at FROM bookmarks WHERE user = $u";
            cmd.Parameters.AddWithValue("$u", area);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var row = ReadRow(r);
                row.exists = true;
                rows[row.card_id] = row;
                last = Math.Max(last, row.updated_at);
            }
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long stamp = now > last ? now : last + 1;

        edit(rows, stamp);

        int changed = 0;
        foreach (var row in rows.Values)
        {
            if (!row.changed || string.IsNullOrEmpty(row.card_id))
                continue;

            // Новой строке без карточки показывать в Lampa нечего
            if (!row.exists && row.card == null)
                continue;

            string categories = (row.categories ?? new JObject()).ToString(Formatting.None);
            string card = row.card?.ToString(Formatting.None);

            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;

            if (row.exists)
            {
                cmd.CommandText = card == null
                    ? "UPDATE bookmarks SET categories = $c, changed_at = $s, updated_at = $s WHERE user = $u AND card_id = $id"
                    : "UPDATE bookmarks SET categories = $c, card = $card, changed_at = $s, updated_at = $s WHERE user = $u AND card_id = $id";
            }
            else
            {
                cmd.CommandText = "INSERT INTO bookmarks (user, card_id, card, categories, changed_at, updated_at) VALUES ($u, $id, $card, $c, $s, $s)";
            }

            cmd.Parameters.AddWithValue("$u", area);
            cmd.Parameters.AddWithValue("$id", row.card_id);
            cmd.Parameters.AddWithValue("$c", categories);
            cmd.Parameters.AddWithValue("$s", stamp);
            if (card != null || !row.exists)
                cmd.Parameters.AddWithValue("$card", (object)card ?? DBNull.Value);

            changed += cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return changed;
    }

    static Row ReadRow(SqliteDataReader r) => new Row
    {
        card_id = r.GetString(0),
        card = r.IsDBNull(1) ? null : ParseObject(r.GetString(1)),
        categories = r.IsDBNull(2) ? new JObject() : (ParseObject(r.GetString(2)) ?? new JObject()),
        updated_at = r.IsDBNull(3) ? 0 : r.GetInt64(3)
    };

    public static JObject ParseObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try { return JObject.Parse(json); }
        catch { return null; }
    }

    static SqliteConnection Open()
    {
        var db = new SqliteConnection(_connection);
        db.Open();
        return db;
    }
}
