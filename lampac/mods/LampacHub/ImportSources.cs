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

/// <summary>Статусы Shikimori / MyAnimeList → категории Lampa</summary>
static class RateStatus
{
    public static string Map(string status) => (status ?? string.Empty).Trim().ToLowerInvariant().Replace(" ", "_").Replace("-", "_") switch
    {
        "watching" or "1" => "look",
        "rewatching" => "rewatch",
        "planned" or "plan_to_watch" or "6" => "scheduled",
        "completed" or "2" => "viewed",
        "on_hold" or "onhold" or "3" => "continued",
        "dropped" or "4" => "thrown",
        _ => null
    };

    /// <summary>Вид тайтла Shikimori / MAL → tv, movie или пусто</summary>
    public static string Kind(string kind) => (kind ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "movie" => "movie",
        "tv" or "tv_13" or "tv_24" or "tv_48" or "ona" or "ova" or "special" or "tv_special" => "tv",
        _ => string.Empty
    };

    public static int Year(string date)
    {
        var m = Regex.Match(date ?? string.Empty, @"(19|20)\d{2}");
        return m.Success ? int.Parse(m.Value) : 0;
    }

    public static int? Int(JToken t)
    {
        if (t == null || t.Type == JTokenType.Null)
            return null;
        return int.TryParse(t.ToString(), out int v) && v > 0 ? v : null;
    }

    public static void Add(ExternalTitle t, string category)
    {
        if (!string.IsNullOrEmpty(category) && !t.categories.Contains(category))
            t.categories.Add(category);
    }

    /// <summary>Ник из ссылки на профиль: shikimori.io/Nick, myanimelist.net/profile/Nick, …/animelist/Nick</summary>
    public static string User(string value, string pattern)
    {
        value = (value ?? string.Empty).Trim();
        var m = Regex.Match(value, pattern, RegexOptions.IgnoreCase);
        return Uri.UnescapeDataString((m.Success ? m.Groups[1].Value : value).Trim('/', ' '));
    }

    public static HttpClient Http()
    {
        var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(60) };
        // Shikimori требует осмысленный User-Agent с именем приложения
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "LampacHub");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }
}

/// <summary>
/// Shikimori (shikimori.io/api). Открытые списки читаются по нику без входа:
/// /users/{id}/anime_rates — всё со статусами, /users/{id}/favourites — «Избранное» → like.
/// id Shikimori у аниме совпадает с id MyAnimeList.
/// </summary>
public static class ShikimoriSource
{
    public static async Task<List<ExternalTitle>> Fetch(string user, ImportState state)
    {
        string nick = RateStatus.User(user, @"shikimori\.[a-z]+/([^/?#]+)");
        if (string.IsNullOrEmpty(nick))
            throw new InvalidOperationException("Укажите ник или ссылку на профиль Shikimori");

        string api = (string.IsNullOrWhiteSpace(ModInit.conf.shikimori_api) ? "https://shikimori.io" : ModInit.conf.shikimori_api.Trim()).TrimEnd('/') + "/api";
        using var http = RateStatus.Http();

        async Task<JToken> Get(string path)
        {
            using var resp = await http.GetAsync(api + path);
            if (resp.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidOperationException("Shikimori: пользователь не найден");
            if (resp.StatusCode == HttpStatusCode.Forbidden || resp.StatusCode == HttpStatusCode.Unauthorized)
                throw new InvalidOperationException("Shikimori: список закрыт настройками приватности профиля");
            if ((int)resp.StatusCode == 429)
                throw new InvalidOperationException("Shikimori: слишком много запросов, попробуйте через минуту");
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException("Shikimori ответил " + (int)resp.StatusCode);
            return JToken.Parse(await resp.Content.ReadAsStringAsync());
        }

        state.stage = "Загрузка списков Shikimori";
        string id = nick.All(char.IsDigit) ? nick : (await Get("/users/" + Uri.EscapeDataString(nick) + "?is_nickname=1"))?["id"]?.ToString();
        if (string.IsNullOrEmpty(id))
            throw new InvalidOperationException("Shikimori: пользователь не найден");

        var titles = new Dictionary<string, ExternalTitle>();

        for (int page = 1; page <= 20; page++)
        {
            var rates = await Get("/users/" + id + "/anime_rates?limit=5000&page=" + page) as JArray ?? new JArray();
            foreach (var r in rates.OfType<JObject>())
            {
                var a = r["anime"] as JObject;
                string animeId = a?["id"]?.ToString();
                if (string.IsNullOrEmpty(animeId))
                    continue;

                if (!titles.TryGetValue(animeId, out var t))
                {
                    titles[animeId] = t = new ExternalTitle
                    {
                        source = "shikimori",
                        ext_id = animeId,
                        title = (string)a["russian"] is string ru && ru.Length > 0 ? ru : (string)a["name"] ?? animeId,
                        year = RateStatus.Year((string)a["aired_on"]),
                        kind = RateStatus.Kind((string)a["kind"]),
                        mal_id = RateStatus.Int(a["id"])
                    };
                    if (!string.IsNullOrEmpty((string)a["name"]) && (string)a["name"] != t.title)
                        t.alt_titles.Add((string)a["name"]);
                }

                RateStatus.Add(t, RateStatus.Map((string)r["status"]));
            }

            // Shikimori отдаёт limit + 1 запись, если есть следующая страница
            if (rates.Count <= 5000)
                break;
        }

        try
        {
            var fav = await Get("/users/" + id + "/favourites");
            foreach (var a in (fav?["animes"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string animeId = a["id"]?.ToString();
                if (string.IsNullOrEmpty(animeId))
                    continue;

                if (!titles.TryGetValue(animeId, out var t))
                {
                    titles[animeId] = t = new ExternalTitle
                    {
                        source = "shikimori",
                        ext_id = animeId,
                        title = (string)a["russian"] is string ru && ru.Length > 0 ? ru : (string)a["name"] ?? animeId,
                        kind = string.Empty,
                        mal_id = RateStatus.Int(a["id"])
                    };
                }
                RateStatus.Add(t, "like");
            }
        }
        catch (InvalidOperationException ex)
        {
            state.notes.Add("Избранное не загрузилось: " + ex.Message);
        }

        var list = titles.Values.Where(t => t.categories.Count > 0).ToList();
        state.notes.Add("Shikimori: " + list.Count + " тайтлов в списках, из них в «Избранном» " + list.Count(t => t.categories.Contains("like")));
        return list;
    }
}

/// <summary>
/// MyAnimeList: открытый список читается без входа и без ключа API через
/// myanimelist.net/animelist/{nick}/load.json (по 300 записей).
/// </summary>
public static class MalSource
{
    public static async Task<List<ExternalTitle>> Fetch(string user, ImportState state)
    {
        string nick = RateStatus.User(user, @"myanimelist\.net/(?:profile|animelist)/([^/?#]+)");
        if (string.IsNullOrEmpty(nick))
            throw new InvalidOperationException("Укажите ник или ссылку на профиль MyAnimeList");

        using var http = RateStatus.Http();
        state.stage = "Загрузка списка MyAnimeList";

        var titles = new Dictionary<string, ExternalTitle>();
        for (int offset = 0; offset < 30000; offset += 300)
        {
            using var resp = await http.GetAsync("https://myanimelist.net/animelist/" + Uri.EscapeDataString(nick) + "/load.json?status=7&offset=" + offset);
            if (resp.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidOperationException("MyAnimeList: пользователь не найден");
            if (resp.StatusCode == HttpStatusCode.BadRequest || resp.StatusCode == HttpStatusCode.Forbidden || resp.StatusCode == HttpStatusCode.Unauthorized)
                throw new InvalidOperationException("MyAnimeList: список закрыт или недоступен. Сделайте его открытым или загрузите файл экспорта (XML)");
            if ((int)resp.StatusCode == 429)
                throw new InvalidOperationException("MyAnimeList: слишком много запросов, попробуйте позже");
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException("MyAnimeList ответил " + (int)resp.StatusCode);

            var page = JToken.Parse(await resp.Content.ReadAsStringAsync()) as JArray ?? new JArray();
            foreach (var a in page.OfType<JObject>())
            {
                string animeId = a["anime_id"]?.ToString();
                if (string.IsNullOrEmpty(animeId) || titles.ContainsKey(animeId))
                    continue;

                var t = new ExternalTitle
                {
                    source = "mal",
                    ext_id = animeId,
                    title = a["anime_title"]?.ToString() ?? animeId,
                    year = RateStatus.Year((string)a["anime_start_date_string"]),
                    kind = RateStatus.Kind((string)a["anime_media_type_string"]),
                    mal_id = RateStatus.Int(a["anime_id"])
                };
                string eng = a["anime_title_eng"]?.ToString();
                if (!string.IsNullOrEmpty(eng) && eng != t.title)
                    t.alt_titles.Add(eng);

                bool rewatching = a["is_rewatching"]?.ToString() is "1" or "True" or "true";
                RateStatus.Add(t, rewatching ? "rewatch" : RateStatus.Map(a["status"]?.ToString()));
                if (t.categories.Count > 0)
                    titles[animeId] = t;
            }

            if (page.Count < 300)
                break;
            await Task.Delay(500);
        }

        var list = titles.Values.ToList();
        state.notes.Add("MyAnimeList: " + list.Count + " тайтлов в списке");
        return list;
    }
}

/// <summary>
/// Файл экспорта списков, формат определяется по содержимому:
/// JSON Shikimori / Yummy (target_id, status), HTML Yummy (data-list-id, data-shiki-id),
/// XML MyAnimeList / Shikimori (myanimelist → anime → series_animedb_id, my_status), можно .gz.
/// </summary>
public static class ExportFileSource
{
    /// <summary>Списки HTML-экспорта Yummy: 0 Смотрю, 1 В планах, 2 Просмотрено, 3 Брошено, 4 Любимые, 5 Отложено</summary>
    static readonly Dictionary<string, string> YummyLists = new()
    {
        ["0"] = "look",
        ["1"] = "scheduled",
        ["2"] = "viewed",
        ["3"] = "thrown",
        ["4"] = "like",
        ["5"] = "continued"
    };

    public static (string source, List<ExternalTitle> titles) Parse(byte[] data)
    {
        if (data.Length > 2 && data[0] == 0x1f && data[1] == 0x8b)
        {
            using var gz = new System.IO.Compression.GZipStream(new System.IO.MemoryStream(data), System.IO.Compression.CompressionMode.Decompress);
            using var ms = new System.IO.MemoryStream();
            gz.CopyTo(ms);
            data = ms.ToArray();
        }

        string text = System.Text.Encoding.UTF8.GetString(data).TrimStart('﻿', ' ', '\r', '\n', '\t');

        if (text.StartsWith("[") || text.StartsWith("{"))
            return Json(JToken.Parse(text));

        if (Regex.IsMatch(text, @"<myanimelist[\s>]", RegexOptions.IgnoreCase))
            return Xml(text);

        if (Regex.IsMatch(text, @"data-list-id\s*=", RegexOptions.IgnoreCase))
            return ("yummy", Html(text));

        throw new InvalidOperationException("Неизвестный формат файла. Подходят экспорт Yummy (JSON или HTML), Shikimori (JSON или XML) и MyAnimeList (XML)");
    }

    static (string, List<ExternalTitle>) Json(JToken doc)
    {
        var rows = doc as JArray ?? (doc["anime"] ?? doc["animes"] ?? doc["data"]) as JArray;
        if (rows == null)
            throw new InvalidOperationException("В файле нет списка тайтлов");

        // Yummy кладёт свой id (ya_id) рядом с id Shikimori; у Shikimori его нет
        string source = rows.OfType<JObject>().Any(r => r["ya_id"] != null) ? "yummy" : "shikimori";
        var titles = new Dictionary<string, ExternalTitle>();

        foreach (var r in rows.OfType<JObject>())
        {
            string type = (string)r["target_type"];
            if (!string.IsNullOrEmpty(type) && !type.Equals("Anime", StringComparison.OrdinalIgnoreCase))
                continue;

            string shiki = r["target_id"]?.ToString();
            string id = r["ya_id"]?.ToString() is string ya && ya.Length > 0 ? ya : shiki;
            if (string.IsNullOrEmpty(id))
                continue;

            if (!titles.TryGetValue(id, out var t))
            {
                titles[id] = t = new ExternalTitle
                {
                    source = source,
                    ext_id = id,
                    title = (string)r["target_title_ru"] is string ru && ru.Length > 0 ? ru : (string)r["target_title"] ?? id,
                    kind = string.Empty,
                    mal_id = RateStatus.Int(r["target_id"])
                };
                string en = (string)r["target_title"];
                if (!string.IsNullOrEmpty(en) && en != t.title)
                    t.alt_titles.Add(en);
            }

            RateStatus.Add(t, (int?)r["rewatches"] > 0 && (string)r["status"] == "watching" ? "rewatch" : RateStatus.Map((string)r["status"]));
            if (r["is_fav"]?.Type == JTokenType.Boolean && (bool)r["is_fav"])
                RateStatus.Add(t, "like");
        }

        return (source, titles.Values.Where(t => t.categories.Count > 0).ToList());
    }

    static (string, List<ExternalTitle>) Xml(string text)
    {
        static string Tag(string block, string name)
        {
            var m = Regex.Match(block, "<" + name + @">\s*(?:<!\[CDATA\[)?(.*?)(?:\]\]>)?\s*</" + name + ">", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : string.Empty;
        }

        string source = Regex.IsMatch(text, "<user_export_type>|<my_watched_episodes>", RegexOptions.IgnoreCase) ? "mal" : "shikimori";
        var titles = new List<ExternalTitle>();

        foreach (Match a in Regex.Matches(text, @"<anime>(.*?)</anime>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            string block = a.Groups[1].Value;
            string id = Tag(block, "series_animedb_id");
            if (string.IsNullOrEmpty(id))
                continue;

            var t = new ExternalTitle
            {
                source = source,
                ext_id = id,
                title = Tag(block, "series_title") is string name && name.Length > 0 ? name : id,
                kind = RateStatus.Kind(Tag(block, "series_type")),
                mal_id = int.TryParse(id, out int mal) && mal > 0 ? mal : null
            };

            RateStatus.Add(t, Tag(block, "my_rewatching") == "1" ? "rewatch" : RateStatus.Map(Tag(block, "my_status")));
            if (t.categories.Count > 0)
                titles.Add(t);
        }

        return (source, titles);
    }

    static List<ExternalTitle> Html(string text)
    {
        var titles = new Dictionary<string, ExternalTitle>();

        // Колонки <td data-list-id="N"> … </td>, в них <li data-shiki-id data-id>Название</li>
        foreach (Match col in Regex.Matches(text, @"<td[^>]*data-list-id\s*=\s*""(\d+)""[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            if (!YummyLists.TryGetValue(col.Groups[1].Value, out string category))
                continue;

            foreach (Match li in Regex.Matches(col.Groups[2].Value, @"<li([^>]*)>(.*?)</li>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                string attrs = li.Groups[1].Value;
                string shiki = Regex.Match(attrs, @"data-shiki-id\s*=\s*""(\d+)""").Groups[1].Value;
                string yid = Regex.Match(attrs, @"data-id\s*=\s*""(\d+)""").Groups[1].Value;
                string key = string.IsNullOrEmpty(yid) ? "s" + shiki : yid;
                if (key == "s")
                    continue;

                // Название — текст до вложенных тегов (оценка <vote>)
                string title = WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(li.Groups[2].Value, @"<vote.*?</vote>", string.Empty, RegexOptions.Singleline), "<[^>]+>", string.Empty)).Trim();

                if (!titles.TryGetValue(key, out var t))
                {
                    titles[key] = t = new ExternalTitle
                    {
                        source = "yummy",
                        ext_id = key,
                        title = string.IsNullOrEmpty(title) ? key : title,
                        kind = string.Empty,
                        mal_id = int.TryParse(shiki, out int mal) && mal > 0 ? mal : null
                    };
                }
                RateStatus.Add(t, category);
            }
        }

        return titles.Values.ToList();
    }
}
