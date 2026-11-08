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
