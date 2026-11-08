using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LampacHub;

/// <summary>
/// Yummy Anime (api.yani.tv). Публичные списки читаются без входа; закрытые — с токеном
/// пользователя (Authorization: Bearer), который живёт только на время импорта.
/// /users/{id}/lists отдаёт всё со статусами, но «Любимые» без статуса есть только в /lists/4.
/// </summary>
public static class YummySource
{
    const string Api = "https://api.yani.tv";

    /// <summary>Статусы Yummy → категории: 0 Смотрю, 1 В планах, 2 Просмотрено, 3 Брошено, 5 Отложено (4 — Любимые)</summary>
    static readonly Dictionary<int, string> Statuses = new()
    {
        [0] = "look",
        [1] = "scheduled",
        [2] = "viewed",
        [3] = "thrown",
        [5] = "continued"
    };

    public static async Task<List<ExternalTitle>> Fetch(string user, string token, ImportState state)
    {
        user = (user ?? string.Empty).Trim();
        var m = Regex.Match(user, @"yummyani\.[a-z]+/users/(?:id)?([^/?#]+)", RegexOptions.IgnoreCase);
        if (m.Success)
            user = m.Groups[1].Value;
        if (user.StartsWith("id", StringComparison.OrdinalIgnoreCase) && user.Length > 2 && user.Substring(2).All(char.IsDigit))
            user = user.Substring(2);

        if (string.IsNullOrEmpty(user))
            throw new InvalidOperationException("Укажите id или ник пользователя Yummy");

        using var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(60) };

        async Task<JToken> Get(string path)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Api + path);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.TryAddWithoutValidation("Lang", "ru");
            if (!string.IsNullOrWhiteSpace(ModInit.conf.yummy_app))
                req.Headers.TryAddWithoutValidation("X-Application", ModInit.conf.yummy_app.Trim());
            if (!string.IsNullOrWhiteSpace(token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());

            using var resp = await http.SendAsync(req);
            if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                throw new InvalidOperationException("Yummy: списки закрыты. Откройте их в настройках приватности профиля Yummy или укажите токен");
            if (resp.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidOperationException("Yummy: пользователь не найден");
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException("Yummy ответил " + (int)resp.StatusCode);

            return JToken.Parse(await resp.Content.ReadAsStringAsync())["response"];
        }

        string id = user;
        if (!user.All(char.IsDigit))
        {
            var profile = await Get("/users/" + Uri.EscapeDataString(user));
            id = profile?["id"]?.ToString();
            if (string.IsNullOrEmpty(id))
                throw new InvalidOperationException("Yummy: пользователь не найден");
        }

        state.stage = "Загрузка списков Yummy";
        var all = (await Get("/users/" + id + "/lists")) as JArray ?? new JArray();
        var fav = (await Get("/users/" + id + "/lists/4")) as JArray ?? new JArray();

        var titles = new Dictionary<string, ExternalTitle>();

        void Add(JObject a, bool favourite)
        {
            string animeId = a["anime_id"]?.ToString();
            if (string.IsNullOrEmpty(animeId))
                return;

            if (!titles.TryGetValue(animeId, out var t))
            {
                string alias = ((string)a["type"]?["alias"] ?? string.Empty).ToLowerInvariant();
                string typeName = ((string)a["type"]?["name"] ?? string.Empty).ToLowerInvariant();
                var ids = a["remote_ids"];
                int? mal = IntOf(ids?["myanimelist_id"]) ?? IntOf(ids?["shikimori_id"]);

                t = new ExternalTitle
                {
                    source = "yummy",
                    ext_id = animeId,
                    title = (string)a["title"] ?? animeId,
                    year = IntOf(a["year"]) ?? 0,
                    kind = alias.Contains("movie") || alias.Contains("film") || typeName.Contains("фильм") ? "movie" : alias == "tv" ? "tv" : string.Empty,
                    mal_id = mal
                };
                titles[animeId] = t;
            }

            int? status = IntOf(a["user"]?["list"]?["list"]?["id"]);
            if (status != null && Statuses.TryGetValue(status.Value, out string cat) && !t.categories.Contains(cat))
                t.categories.Add(cat);

            bool isFav = favourite || a["user"]?["list"]?["is_fav"]?.Type == JTokenType.Boolean && (bool)a["user"]["list"]["is_fav"];
            if (isFav && !t.categories.Contains("like"))
                t.categories.Add("like");
        }

        foreach (var a in all.OfType<JObject>())
            Add(a, false);
        foreach (var a in fav.OfType<JObject>())
            Add(a, true);

        var list = titles.Values.Where(t => t.categories.Count > 0).ToList();
        state.notes.Add("Yummy: " + list.Count + " тайтлов в списках, из них в «Любимых» " + list.Count(t => t.categories.Contains("like")));
        return list;
    }

    static int? IntOf(JToken t)
    {
        if (t == null || t.Type == JTokenType.Null)
            return null;
        return int.TryParse(t.ToString(), out int v) && v > 0 ? v : null;
    }
}

/// <summary>
/// AniLiberty (бывшая AniLibria), aniliberty.top/api/v1. Списки есть только у своего аккаунта
/// (/accounts/users/me/...), поэтому нужен вход: кодом устройства (пароль не попадает на сервер)
/// или логином и паролем. Токен и пароль живут только в замыкании задачи импорта.
/// </summary>
public static class AniLibertySource
{
    static string Api => string.IsNullOrWhiteSpace(ModInit.conf.aniliberty_api) ? "https://aniliberty.top/api/v1" : ModInit.conf.aniliberty_api.TrimEnd('/');

    static readonly Dictionary<string, string> Collections = new()
    {
        ["WATCHING"] = "look",
        ["PLANNED"] = "scheduled",
        ["WATCHED"] = "viewed",
        ["POSTPONED"] = "continued",
        ["ABANDONED"] = "thrown"
    };

    /// <summary>Код входа, который ждёт подтверждения на сайте. Один на сервер, только в памяти</summary>
    public class Otp
    {
        public string code;
        public string device_id;
        public DateTime expires;
    }

    static Otp _otp;

    public static Otp Pending => _otp != null && _otp.expires > DateTime.UtcNow ? _otp : null;

    static HttpClient Client() => new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(30) };

    static async Task<(HttpStatusCode status, JToken json)> Send(HttpClient http, HttpMethod method, string path, string token = null, JObject body = null)
    {
        using var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.TryAddWithoutValidation("User-Agent", "LampacHub");
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null)
            req.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), System.Text.Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req);
        string text = await resp.Content.ReadAsStringAsync();
        JToken json = null;
        try { json = string.IsNullOrWhiteSpace(text) ? null : JToken.Parse(text); } catch { }
        return (resp.StatusCode, json);
    }

    /// <summary>Новый код входа: его подтверждают на сайте AniLiberty в своём аккаунте</summary>
    public static async Task<Otp> RequestOtp()
    {
        using var http = Client();
        string device = "lampachub-" + HubStore.RandomId(16);
        var (status, json) = await Send(http, HttpMethod.Post, "/accounts/otp/get", body: new JObject { ["device_id"] = device });

        string code = json?["otp"]?["code"]?.ToString();
        if ((int)status >= 400 || string.IsNullOrEmpty(code))
            throw new InvalidOperationException("AniLiberty не выдал код входа (HTTP " + (int)status + ")");

        int seconds = int.TryParse(json?["remaining_time"]?.ToString(), out int r) && r > 0 ? r : 300;
        _otp = new Otp { code = code, device_id = device, expires = DateTime.UtcNow.AddSeconds(seconds) };
        return _otp;
    }

    public static async Task<List<ExternalTitle>> Fetch(string login, string password, bool useOtp, ImportState state)
    {
        using var http = Client();
        string token;

        if (useOtp)
        {
            var otp = Pending ?? throw new InvalidOperationException("Код входа AniLiberty истёк, получите новый");
            state.stage = "Жду подтверждения кода " + otp.code + " на сайте AniLiberty";
            token = null;
            while (token == null && DateTime.UtcNow < otp.expires)
            {
                var (status, json) = await Send(http, HttpMethod.Post, "/accounts/otp/login", body: new JObject { ["code"] = otp.code, ["device_id"] = otp.device_id });
                token = status == HttpStatusCode.OK ? json?["token"]?.ToString() : null;
                if (token == null)
                    await Task.Delay(3000);
            }
            _otp = null;
            if (string.IsNullOrEmpty(token))
                throw new InvalidOperationException("Код не подтвердили на сайте AniLiberty, пока он действовал");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password))
                throw new InvalidOperationException("Укажите логин и пароль AniLiberty");

            state.stage = "Вход в AniLiberty";
            var (status, json) = await Send(http, HttpMethod.Post, "/accounts/users/auth/login", body: new JObject { ["login"] = login.Trim(), ["password"] = password });
            token = json?["token"]?.ToString();
            if (string.IsNullOrEmpty(token))
                throw new InvalidOperationException(status == HttpStatusCode.Unauthorized || status == HttpStatusCode.UnprocessableEntity || status == HttpStatusCode.Forbidden
                    ? "AniLiberty: неверный логин или пароль"
                    : "AniLiberty не пустил (HTTP " + (int)status + ")");
        }

        async Task<JToken> Get(string path)
        {
            var (status, json) = await Send(http, HttpMethod.Get, path, token);
            if (status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden)
                throw new InvalidOperationException("AniLiberty: вход не принят");
            if ((int)status >= 400)
                throw new InvalidOperationException("AniLiberty ответил " + (int)status + " на " + path);
            return json;
        }

        static JArray Items(JToken t) => t as JArray ?? t?["data"] as JArray ?? new JArray();

        state.stage = "Загрузка списков AniLiberty";
        var cats = new Dictionary<string, List<string>>();

        void Add(string id, string cat)
        {
            if (string.IsNullOrEmpty(id) || cat == null)
                return;
            if (!cats.TryGetValue(id, out var list))
                cats[id] = list = new List<string>();
            if (!list.Contains(cat))
                list.Add(cat);
        }

        foreach (var pair in Items(await Get("/accounts/users/me/collections/ids")))
        {
            if (pair is JArray a && a.Count >= 2)
                Add(a[0]?.ToString(), Collections.TryGetValue(a[1]?.ToString() ?? string.Empty, out string c) ? c : null);
            else if (pair is JObject o)
                Add((o["release_id"] ?? o["id"])?.ToString(), Collections.TryGetValue((string)(o["type_of_collection"] ?? o["type"]) ?? string.Empty, out string c2) ? c2 : null);
        }

        foreach (var id in Items(await Get("/accounts/users/me/favorites/ids")))
            Add((id is JObject fo ? fo["id"] : id)?.ToString(), "like");

        state.stage = "Загрузка тайтлов AniLiberty";
        var titles = new List<ExternalTitle>();
        var ids = cats.Keys.ToList();

        for (int i = 0; i < ids.Count; i += 50)
        {
            var batch = ids.Skip(i).Take(50).ToList();
            string q = string.Join("&", batch.Select(b => "ids[]=" + Uri.EscapeDataString(b))) + "&limit=50&include=id,name,year,type,shikimori,mal";
            var found = new HashSet<string>();

            foreach (var r in Items(await Get("/anime/releases/list?" + q)).OfType<JObject>())
            {
                string id = r["id"]?.ToString();
                if (string.IsNullOrEmpty(id) || !cats.TryGetValue(id, out var list) || !found.Add(id))
                    continue;

                string type = ((string)r["type"]?["value"] ?? string.Empty).ToUpperInvariant();
                var alt = new List<string>();
                foreach (var n in new[] { (string)r["name"]?["english"], (string)r["name"]?["alternative"] })
                {
                    if (!string.IsNullOrWhiteSpace(n))
                        alt.Add(n);
                }

                titles.Add(new ExternalTitle
                {
                    source = "aniliberty",
                    ext_id = id,
                    title = (string)r["name"]?["main"] ?? alt.FirstOrDefault() ?? id,
                    alt_titles = alt,
                    year = int.TryParse(r["year"]?.ToString(), out int y) ? y : 0,
                    kind = type == "MOVIE" ? "movie" : type == "TV" || type == "ONA" || type == "WEB" ? "tv" : string.Empty,
                    mal_id = IntOf(r["mal"]?["id"]) ?? IntOf(r["shikimori"]?["id"]),
                    categories = list
                });
            }

            int lost = batch.Count - found.Count;
            if (lost > 0)
                state.notes.Add("AniLiberty не отдал " + lost + " тайтлов из списков (удалены с сайта?)");
        }

        state.notes.Add("AniLiberty: " + titles.Count + " тайтлов в списках, из них в избранном " + titles.Count(t => t.categories.Contains("like")));
        return titles;
    }

    static int? IntOf(JToken t)
        => t == null || t.Type == JTokenType.Null ? null : int.TryParse(t.ToString(), out int v) && v > 0 ? v : null;
}

/// <summary>
/// Резервная копия Lampa (в том числе с аккаунтом CUB): JSON с ключом favorite — объект или
/// строка, как его хранит localStorage, — или сам объект favorite { card: [...], look: [...] }.
/// Карточки там уже с id TMDB, сопоставлять ничего не нужно.
/// </summary>
public static class LampaBackupSource
{
    public static List<HubLists.Portable> Parse(JToken doc)
    {
        JObject fav = Find(doc);
        if (fav == null)
            throw new InvalidOperationException("В файле нет закладок Lampa (ключ favorite)");

        var cards = new Dictionary<string, JObject>();
        foreach (var c in (fav["card"] as JArray ?? new JArray()).OfType<JObject>())
        {
            string id = c["id"]?.ToString();
            if (!string.IsNullOrEmpty(id))
                cards[id] = c;
        }

        var result = new Dictionary<string, HubLists.Portable>();
        foreach (string category in SyncBookmarks.Categories)
        {
            foreach (var idToken in fav[category] as JArray ?? new JArray())
            {
                string id = idToken?.ToString();
                if (string.IsNullOrEmpty(id) || !cards.TryGetValue(id, out var card))
                    continue;

                if (!result.TryGetValue(id, out var p))
                    result[id] = p = new HubLists.Portable { id = id, card = card, categories = new List<string>() };

                if (!p.categories.Contains(category))
                    p.categories.Add(category);
            }
        }

        // Статус один: если в копии их несколько, оставляем первый по порядку Lampa
        foreach (var p in result.Values)
        {
            var statuses = p.categories.Where(c => SyncBookmarks.Statuses.Contains(c)).ToList();
            foreach (var extra in statuses.Skip(1))
                p.categories.Remove(extra);
        }

        return result.Values.ToList();
    }

    static JObject Find(JToken doc)
    {
        if (doc is not JObject o)
            return null;

        if (o["card"] is JArray)
            return o;

        foreach (string key in new[] { "favorite", "data" })
        {
            var v = o[key];
            if (v is JObject vo)
            {
                var inner = Find(vo);
                if (inner != null)
                    return inner;
            }
            else if (v?.Type == JTokenType.String)
            {
                try
                {
                    var inner = Find(JToken.Parse((string)v));
                    if (inner != null)
                        return inner;
                }
                catch { }
            }
        }

        return null;
    }
}
