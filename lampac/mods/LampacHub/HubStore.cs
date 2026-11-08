using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LampacHub;

public class HubAccount
{
    public string id { get; set; }
    public string name { get; set; }

    /// <summary>
    /// Идентичность Sync/TimeCode (uid в запросах Lampa). Пусто — общая идентичность сервера
    /// (database/instance.uid), которую Lampac и так раздаёт всем клиентам, пока accsdb пуст.
    /// </summary>
    public string uid { get; set; }

    public long created { get; set; }
}

public class HubProfile
{
    /// <summary>
    /// Значение lampac_profile_id в Lampa. "0" — основная область данных пользователя (та, куда
    /// Sync и TimeCode пишут без профиля), поэтому накопленные закладки остаются в основном профиле.
    /// </summary>
    public string id { get; set; }
    public string account_id { get; set; }
    public string name { get; set; }
    public string avatar { get; set; }
    public bool child { get; set; }

    /// <summary>Возрастной лимит детского профиля: 0, 6, 12 или 16</summary>
    public int age_limit { get; set; } = 12;

    /// <summary>Переносить между устройствами профиля и вручную установленные плагины Lampa</summary>
    public bool sync_plugins { get; set; }

    public int sort { get; set; }
    public long created { get; set; }
}

public class HubDevice
{
    public string id { get; set; }
    public string account_id { get; set; }
    public string profile_id { get; set; }
    public string name { get; set; }
    public long created { get; set; }
    public long last_seen { get; set; }
    public string last_ip { get; set; }
    public string user_agent { get; set; }
}

/// <summary>Аккаунты, профили и устройства — database/LampacHub.sql</summary>
public static class HubStore
{
    public const string MainAccount = "main";
    public const string MainProfile = "0";

    static readonly object _lock = new object();

    static readonly string _connection = new SqliteConnectionStringBuilder
    {
        DataSource = "database/LampacHub.sql",
        Cache = SqliteCacheMode.Shared,
        DefaultTimeout = 10,
        Pooling = true
    }.ToString();

    public static bool Ready { get; private set; }

    #region init
    public static void Init(string accountName)
    {
        Directory.CreateDirectory("database");

        lock (_lock)
        {
            using var db = Open();
            Exec(db, @"
CREATE TABLE IF NOT EXISTS accounts (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    uid TEXT NOT NULL DEFAULT '',
    created INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS profiles (
    id TEXT PRIMARY KEY,
    account_id TEXT NOT NULL,
    name TEXT NOT NULL,
    avatar TEXT,
    child INTEGER NOT NULL DEFAULT 0,
    sort INTEGER NOT NULL DEFAULT 0,
    created INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS devices (
    id TEXT PRIMARY KEY,
    account_id TEXT NOT NULL,
    profile_id TEXT,
    name TEXT,
    token_hash TEXT NOT NULL UNIQUE,
    created INTEGER NOT NULL,
    last_seen INTEGER NOT NULL DEFAULT 0,
    last_ip TEXT,
    user_agent TEXT
);
CREATE TABLE IF NOT EXISTS meta (
    key TEXT PRIMARY KEY,
    value TEXT
);
CREATE TABLE IF NOT EXISTS profile_settings (
    profile_id TEXT PRIMARY KEY,
    data TEXT NOT NULL,
    updated INTEGER NOT NULL
);");

            if (Scalar(db, "SELECT COUNT(*) FROM pragma_table_info('profiles') WHERE name = 'age_limit'") == 0)
                Exec(db, "ALTER TABLE profiles ADD COLUMN age_limit INTEGER NOT NULL DEFAULT 12");

            if (Scalar(db, "SELECT COUNT(*) FROM pragma_table_info('profiles') WHERE name = 'sync_plugins'") == 0)
                Exec(db, "ALTER TABLE profiles ADD COLUMN sync_plugins INTEGER NOT NULL DEFAULT 0");

            if (Scalar(db, "SELECT COUNT(*) FROM accounts WHERE id = $id", ("$id", MainAccount)) == 0)
            {
                Exec(db, "INSERT INTO accounts (id, name, uid, created) VALUES ($id, $name, '', $now)",
                    ("$id", MainAccount), ("$name", accountName), ("$now", Now()));
            }

            if (Scalar(db, "SELECT COUNT(*) FROM profiles WHERE account_id = $a", ("$a", MainAccount)) == 0)
            {
                Exec(db, "INSERT INTO profiles (id, account_id, name, avatar, child, sort, created) VALUES ($id, $a, $name, '', 0, 0, $now)",
                    ("$id", MainProfile), ("$a", MainAccount), ("$name", "Основной"), ("$now", Now()));
            }
        }

        RefreshChildren();
        Ready = true;
    }
    #endregion

    #region accounts
    public static HubAccount GetAccount(string id)
    {
        using var db = Open();
        using var cmd = Command(db, "SELECT id, name, uid, created FROM accounts WHERE id = $id", ("$id", id));
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return null;

        return new HubAccount
        {
            id = r.GetString(0),
            name = r.GetString(1),
            uid = r.IsDBNull(2) ? string.Empty : r.GetString(2),
            created = r.GetInt64(3)
        };
    }

    /// <summary>
    /// uid, с которым устройства аккаунта ходят в Sync/TimeCode.
    ///
    /// Пока accsdb пуст, Lampac сам раздаёт всем клиентам одну идентичность сервера, а timecode.js
    /// при каждом запуске возвращает lampac_unic_id к ней. Поэтому основной аккаунт берёт именно её:
    /// иначе timecode.js и hub.js перетягивали бы uid друг у друга. Если accsdb заполнен и общей
    /// идентичности нет, аккаунт получает свой постоянный uid.
    /// </summary>
    public static string EffectiveUid(HubAccount account)
    {
        if (account == null)
            return null;

        if (!string.IsNullOrEmpty(account.uid))
            return account.uid;

        string assigned = InstanceIdentity.Assigned;
        if (!string.IsNullOrEmpty(assigned))
            return assigned;

        lock (_lock)
        {
            string key = "uid:" + account.id;
            string stored = GetMeta(key);
            if (string.IsNullOrEmpty(stored))
            {
                stored = RandomId(16);
                SetMeta(key, stored);
            }
            return stored;
        }
    }
    #endregion

    #region profiles
    public static List<HubProfile> ListProfiles(string accountId)
    {
        var list = new List<HubProfile>();
        using var db = Open();
        using var cmd = Command(db, "SELECT id, account_id, name, avatar, child, sort, created, age_limit, sync_plugins FROM profiles WHERE account_id = $a ORDER BY sort, created", ("$a", accountId));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadProfile(r));
        return list;
    }

    public static HubProfile GetProfile(string id)
    {
        if (string.IsNullOrEmpty(id))
            return null;

        using var db = Open();
        using var cmd = Command(db, "SELECT id, account_id, name, avatar, child, sort, created, age_limit, sync_plugins FROM profiles WHERE id = $id", ("$id", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadProfile(r) : null;
    }

    static HubProfile ReadProfile(SqliteDataReader r) => new HubProfile
    {
        id = r.GetString(0),
        account_id = r.GetString(1),
        name = r.GetString(2),
        avatar = r.IsDBNull(3) ? string.Empty : r.GetString(3),
        child = r.GetInt64(4) != 0,
        sort = r.GetInt32(5),
        created = r.GetInt64(6),
        age_limit = r.GetInt32(7),
        sync_plugins = r.GetInt64(8) != 0
    };

    public static HubProfile CreateProfile(string accountId, string name, string avatar, bool child, int ageLimit, bool syncPlugins = false)
    {
        string id;

        lock (_lock)
        {
            using var db = Open();
            do
            {
                id = "p" + RandomId(7);
            }
            while (Scalar(db, "SELECT COUNT(*) FROM profiles WHERE id = $id", ("$id", id)) > 0);

            long sort = Scalar(db, "SELECT COALESCE(MAX(sort), 0) + 1 FROM profiles WHERE account_id = $a", ("$a", accountId));

            Exec(db, "INSERT INTO profiles (id, account_id, name, avatar, child, sort, created, age_limit, sync_plugins) VALUES ($id, $a, $name, $avatar, $child, $sort, $now, $age, $plugins)",
                ("$id", id), ("$a", accountId), ("$name", name), ("$avatar", avatar ?? string.Empty),
                ("$child", child ? 1 : 0), ("$sort", sort), ("$now", Now()), ("$age", NormalizeAge(ageLimit)), ("$plugins", syncPlugins ? 1 : 0));
        }

        RefreshChildren();
        return GetProfile(id);
    }

    public static bool UpdateProfile(string id, string name, string avatar, bool child, int ageLimit, bool syncPlugins)
    {
        bool ok;
        lock (_lock)
        {
            using var db = Open();
            ok = Exec(db, "UPDATE profiles SET name = $name, avatar = $avatar, child = $child, age_limit = $age, sync_plugins = $plugins WHERE id = $id",
                ("$id", id), ("$name", name), ("$avatar", avatar ?? string.Empty), ("$child", child ? 1 : 0), ("$age", NormalizeAge(ageLimit)),
                ("$plugins", syncPlugins ? 1 : 0)) > 0;
        }

        RefreshChildren();
        return ok;
    }

    public static bool SetAvatar(string id, string avatar)
    {
        lock (_lock)
        {
            using var db = Open();
            return Exec(db, "UPDATE profiles SET avatar = $avatar WHERE id = $id", ("$id", id), ("$avatar", avatar ?? string.Empty)) > 0;
        }
    }

    public static int NormalizeAge(int age)
    {
        if (age >= 16) return 16;
        if (age >= 12) return 12;
        if (age >= 6) return 6;
        return 0;
    }

    static volatile HashSet<string> _children = new HashSet<string>();

    /// <summary>Детский профиль: по нему middleware закрывает раздел 18+. Читается на каждый запрос, поэтому из памяти.</summary>
    public static bool IsChild(string profileId)
        => !string.IsNullOrEmpty(profileId) && _children.Contains(profileId);

    static void RefreshChildren()
    {
        var set = new HashSet<string>();
        using (var db = Open())
        using (var cmd = Command(db, "SELECT id FROM profiles WHERE child = 1"))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
                set.Add(r.GetString(0));
        }
        _children = set;
    }

    /// <summary>Закладки и история профиля остаются в базе Sync/TimeCode, удаляется только запись профиля</summary>
    public static bool DeleteProfile(string id)
    {
        if (id == MainProfile)
            return false;

        lock (_lock)
        {
            using var db = Open();
            Exec(db, "UPDATE devices SET profile_id = NULL WHERE profile_id = $id", ("$id", id));
            bool ok = Exec(db, "DELETE FROM profiles WHERE id = $id", ("$id", id)) > 0;
            Exec(db, "DELETE FROM profile_settings WHERE profile_id = $id", ("$id", id));
            if (ok)
                RefreshChildren();
            return ok;
        }
    }
    #endregion

    #region devices
    public static List<HubDevice> ListDevices()
    {
        var list = new List<HubDevice>();
        using var db = Open();
        using var cmd = Command(db, "SELECT id, account_id, profile_id, name, created, last_seen, last_ip, user_agent FROM devices ORDER BY last_seen DESC");
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadDevice(r));
        return list;
    }

    static HubDevice ReadDevice(SqliteDataReader r) => new HubDevice
    {
        id = r.GetString(0),
        account_id = r.GetString(1),
        profile_id = r.IsDBNull(2) ? null : r.GetString(2),
        name = r.IsDBNull(3) ? string.Empty : r.GetString(3),
        created = r.GetInt64(4),
        last_seen = r.GetInt64(5),
        last_ip = r.IsDBNull(6) ? string.Empty : r.GetString(6),
        user_agent = r.IsDBNull(7) ? string.Empty : r.GetString(7)
    };

    /// <summary>Создаёт устройство и возвращает его токен. В базе хранится только SHA-256 токена.</summary>
    public static string CreateDevice(string accountId, string profileId, string name, string ip, string userAgent, out HubDevice device)
    {
        string token = RandomId(40);
        long now = Now();

        device = new HubDevice
        {
            id = "d" + RandomId(11),
            account_id = accountId,
            profile_id = profileId,
            name = Trim(name, 80),
            created = now,
            last_seen = now,
            last_ip = ip ?? string.Empty,
            user_agent = Trim(userAgent, 300)
        };

        lock (_lock)
        {
            using var db = Open();
            Exec(db, "INSERT INTO devices (id, account_id, profile_id, name, token_hash, created, last_seen, last_ip, user_agent) VALUES ($id, $a, $p, $name, $hash, $now, $now, $ip, $ua)",
                ("$id", device.id), ("$a", accountId), ("$p", (object)profileId ?? DBNull.Value), ("$name", device.name),
                ("$hash", Hash(token)), ("$now", now), ("$ip", device.last_ip), ("$ua", device.user_agent));
        }

        return token;
    }

    public static HubDevice FindDevice(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128)
            return null;

        using var db = Open();
        using var cmd = Command(db, "SELECT id, account_id, profile_id, name, created, last_seen, last_ip, user_agent FROM devices WHERE token_hash = $h", ("$h", Hash(token)));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadDevice(r) : null;
    }

    public static void TouchDevice(string id, string ip, string userAgent)
    {
        lock (_lock)
        {
            using var db = Open();
            Exec(db, "UPDATE devices SET last_seen = $now, last_ip = $ip, user_agent = $ua WHERE id = $id",
                ("$id", id), ("$now", Now()), ("$ip", ip ?? string.Empty), ("$ua", Trim(userAgent, 300)));
        }
    }

    public static bool SetDeviceProfile(string id, string profileId)
    {
        lock (_lock)
        {
            using var db = Open();
            return Exec(db, "UPDATE devices SET profile_id = $p WHERE id = $id", ("$id", id), ("$p", (object)profileId ?? DBNull.Value)) > 0;
        }
    }

    public static bool RenameDevice(string id, string name)
    {
        lock (_lock)
        {
            using var db = Open();
            return Exec(db, "UPDATE devices SET name = $name WHERE id = $id", ("$id", id), ("$name", Trim(name, 80))) > 0;
        }
    }

    public static bool DeleteDevice(string id)
    {
        lock (_lock)
        {
            using var db = Open();
            return Exec(db, "DELETE FROM devices WHERE id = $id", ("$id", id)) > 0;
        }
    }
    #endregion

    #region settings
    /// <summary>Настройки Lampa профиля: JSON-объект «ключ Storage → значение» и метка изменения в мс</summary>
    public static (string data, long updated) GetSettings(string profileId)
    {
        using var db = Open();
        using var cmd = Command(db, "SELECT data, updated FROM profile_settings WHERE profile_id = $id", ("$id", profileId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.GetInt64(1)) : (null, 0);
    }

    /// <summary>
    /// Пришедшие ключи накладываются на сохранённые, а не заменяют их: два устройства, поменявшие
    /// разные настройки почти одновременно, не затирают друг друга. Метка строго растёт.
    /// </summary>
    public static long SaveSettings(string profileId, JObject incoming, bool allowPlugins)
    {
        if (!allowPlugins)
            incoming.Remove("plugins");

        lock (_lock)
        {
            using var db = Open();
            var (old, last) = ReadSettings(db, profileId);

            var merged = old ?? new JObject();
            foreach (var prop in incoming.Properties())
                merged[prop.Name] = prop.Value;

            if (!allowPlugins)
                merged.Remove("plugins");

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long updated = now > last ? now : last + 1;

            Exec(db, "INSERT INTO profile_settings (profile_id, data, updated) VALUES ($id, $data, $u) ON CONFLICT(profile_id) DO UPDATE SET data = excluded.data, updated = excluded.updated",
                ("$id", profileId), ("$data", merged.ToString(Formatting.None)), ("$u", updated));

            return updated;
        }
    }

    static (JObject data, long updated) ReadSettings(SqliteConnection db, string profileId)
    {
        using var cmd = Command(db, "SELECT data, updated FROM profile_settings WHERE profile_id = $id", ("$id", profileId));
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return (null, 0);

        JObject data = null;
        try { data = JObject.Parse(r.GetString(0)); } catch { }
        return (data, r.GetInt64(1));
    }

    public static bool ResetSettings(string profileId)
    {
        lock (_lock)
        {
            using var db = Open();
            return Exec(db, "DELETE FROM profile_settings WHERE profile_id = $id", ("$id", profileId)) > 0;
        }
    }

    /// <summary>Сколько ключей сохранено и когда — для админки</summary>
    public static Dictionary<string, (int keys, long updated)> SettingsSummary()
    {
        var result = new Dictionary<string, (int, long)>();
        using var db = Open();
        using var cmd = Command(db, "SELECT profile_id, data, updated FROM profile_settings");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            int keys = 0;
            try { keys = JObject.Parse(r.GetString(1)).Count; } catch { }
            result[r.GetString(0)] = (keys, r.GetInt64(2));
        }
        return result;
    }
    #endregion

    #region pin
    /// <summary>PIN выхода из детского профиля, общий на аккаунт. Хранится как SHA-256 с солью.</summary>
    public static bool HasPin => !string.IsNullOrEmpty(GetMeta("pin"));

    public static void SetPin(string pin)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(pin))
            {
                SetMeta("pin", string.Empty);
                return;
            }

            string salt = RandomId(16);
            SetMeta("pin", salt + ":" + Hash(salt + pin));
        }
    }

    public static bool CheckPin(string pin)
    {
        string stored = GetMeta("pin");
        if (string.IsNullOrEmpty(stored))
            return true;

        int i = stored.IndexOf(':');
        if (i < 0 || string.IsNullOrEmpty(pin))
            return false;

        string salt = stored.Substring(0, i);
        byte[] a = Encoding.UTF8.GetBytes(stored.Substring(i + 1));
        byte[] b = Encoding.UTF8.GetBytes(Hash(salt + pin));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
    #endregion

    #region meta
    static string GetMeta(string key)
    {
        using var db = Open();
        using var cmd = Command(db, "SELECT value FROM meta WHERE key = $k", ("$k", key));
        return cmd.ExecuteScalar() as string;
    }

    static void SetMeta(string key, string value)
    {
        using var db = Open();
        Exec(db, "INSERT INTO meta (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$k", key), ("$v", value));
    }
    #endregion

    #region helpers
    static SqliteConnection Open()
    {
        var db = new SqliteConnection(_connection);
        db.Open();
        return db;
    }

    static SqliteCommand Command(SqliteConnection db, string sql, params (string name, object value)[] args)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    static int Exec(SqliteConnection db, string sql, params (string name, object value)[] args)
    {
        using var cmd = Command(db, sql, args);
        return cmd.ExecuteNonQuery();
    }

    static long Scalar(SqliteConnection db, string sql, params (string name, object value)[] args)
    {
        using var cmd = Command(db, sql, args);
        object v = cmd.ExecuteScalar();
        return v == null || v is DBNull ? 0 : Convert.ToInt64(v);
    }

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>Только строчные латинские буквы и цифры — переживает санитайзер Sync/TimeCode и ValidateIdentity</summary>
    public static string RandomId(int length)
    {
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++)
            sb.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        return sb.ToString();
    }

    static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string Trim(string value, int max)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        value = value.Trim();
        return value.Length > max ? value.Substring(0, max) : value;
    }
    #endregion
}
