using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LampacHub;

/// <summary>
/// TMDB для сопоставления импорта. Ключ — свой из init.conf (LampacHub.tmdb_key) или тот, что
/// Lampac и так использует (cub.api_key). Сначала напрямую, а если api.themoviedb.org недоступен —
/// через TmdbProxy самого Lampac (/tmdb/api), с его прокси-настройками.
/// </summary>
public static class Tmdb
{
    static readonly HttpClient http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    static string _proxyBase;
    static volatile bool _direct = true;

    static string Key
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ModInit.conf.tmdb_key))
                return ModInit.conf.tmdb_key.Trim();

            string key = CoreInit.conf?.cub?.api_key;
            return string.IsNullOrEmpty(key) ? "4ef0d7355d9ffb5151e987764708ce96" : key;
        }
    }

    public static void Prepare(string host)
    {
        _proxyBase = string.IsNullOrEmpty(host) ? null : host.TrimEnd('/') + "/tmdb/api";
        _direct = true;
    }

    /// <summary>JSON ответа, null — 404. Исключение — TMDB не ответил ни напрямую, ни через прокси</summary>
    public static async Task<JObject> Get(string path, string query = null)
    {
        string q = "api_key=" + Uri.EscapeDataString(Key) + "&language=ru-RU" + (string.IsNullOrEmpty(query) ? string.Empty : "&" + query);

        if (_direct)
        {
            var (ok, obj) = await Fetch("https://api.themoviedb.org/3/" + path + "?" + q);
            if (ok)
                return obj;
            _direct = false;
        }

        if (!string.IsNullOrEmpty(_proxyBase))
        {
            var (ok, obj) = await Fetch(_proxyBase + "/3/" + path + "?" + q);
            if (ok)
                return obj;
        }

        throw new HttpRequestException("TMDB недоступен");
    }

    static async Task<(bool ok, JObject obj)> Fetch(string url)
    {
        try
        {
            using var resp = await http.GetAsync(url);
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return (true, null);
            if (!resp.IsSuccessStatusCode)
                return (false, null);
            return (true, JObject.Parse(await resp.Content.ReadAsStringAsync()));
        }
        catch
        {
            return (false, null);
        }
    }

    /// <summary>Карточка для Lampa из ответа TMDB (поиск, find или подробности)</summary>
    public static JObject Card(JObject t, string kind)
    {
        if (t == null || t["id"] == null)
            return null;

        bool tv = kind == "tv";
        var card = new JObject { ["id"] = t["id"], ["source"] = "tmdb" };

        foreach (string k in tv
            ? new[] { "name", "original_name", "first_air_date", "origin_country" }
            : new[] { "title", "original_title", "release_date" })
        {
            if (t[k] != null && t[k].Type != JTokenType.Null)
                card[k] = t[k];
        }

        foreach (string k in new[] { "poster_path", "backdrop_path", "vote_average", "original_language", "genre_ids" })
        {
            if (t[k] != null && t[k].Type != JTokenType.Null)
                card[k] = t[k];
        }

        if (card["genre_ids"] == null && t["genres"] is JArray genres)
            card["genre_ids"] = new JArray(genres.Select(g => g["id"]).Where(g => g != null));

        string overview = (string)t["overview"];
        if (!string.IsNullOrEmpty(overview))
            card["overview"] = overview.Length > 500 ? overview.Substring(0, 500) : overview;

        // Lampa отличает сериал по name/original_name
        if (tv && card["name"] == null)
            card["name"] = t["title"] ?? t["original_name"] ?? string.Empty;
        if (!tv && card["title"] == null)
            card["title"] = t["name"] ?? t["original_title"] ?? string.Empty;

        return card;
    }

    /// <summary>Кандидат для очереди ручного сопоставления</summary>
    public static JObject Brief(JObject t, string kind)
    {
        string date = (string)(t["release_date"] ?? t["first_air_date"]) ?? string.Empty;
        return new JObject
        {
            ["id"] = t["id"],
            ["kind"] = kind,
            ["title"] = (string)(t["title"] ?? t["name"]) ?? string.Empty,
            ["original"] = (string)(t["original_title"] ?? t["original_name"]) ?? string.Empty,
            ["year"] = date.Length >= 4 ? date.Substring(0, 4) : string.Empty,
            ["poster"] = (string)t["poster_path"] ?? string.Empty
        };
    }

    public static async Task<List<(JObject item, string kind)>> Search(string query, string kind, int year = 0)
    {
        var result = new List<(JObject, string)>();
        if (string.IsNullOrWhiteSpace(query))
            return result;

        foreach (string k in kind == null ? new[] { "tv", "movie" } : new[] { kind })
        {
            string q = "query=" + Uri.EscapeDataString(query.Trim()) + "&include_adult=false";
            if (year > 0)
                q += (k == "tv" ? "&first_air_date_year=" : "&year=") + year;

            var json = await Get("search/" + k, q);
            if (json?["results"] is JArray arr)
            {
                foreach (var r in arr.OfType<JObject>())
                    result.Add((r, k));
            }
        }

        return result;
    }
}

/// <summary>
/// Соответствия аниме-баз: MAL id → TMDB / TVDB / IMDb (github.com/Fribb/anime-lists). Файл
/// кешируется в database/LampacHub/anime-list.json на 7 дней; без сети берётся старый.
/// </summary>
public static class AnimeMap
{
    public class Entry
    {
        public int? tmdb_movie;
        public int? tmdb_tv;
        public int? tmdb;
        public int? tvdb;
        public string imdb;
        public string type;
    }

    static Dictionary<int, Entry> _map;
    static DateTime _loaded;

    static string FilePath => Path.Combine("database", "LampacHub", "anime-list.json");

    public static async Task<Dictionary<int, Entry>> Load(Action<string> note)
    {
        if (_map != null && DateTime.UtcNow - _loaded < TimeSpan.FromHours(12))
            return _map;

        string path = FilePath;
        bool fresh = File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromDays(7);

        if (!fresh && !string.IsNullOrWhiteSpace(ModInit.conf.anime_map_url))
        {
            try
            {
                using var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(90) };
                byte[] data = await http.GetByteArrayAsync(ModInit.conf.anime_map_url);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                await File.WriteAllBytesAsync(tmp, data);
                File.Move(tmp, path, true);
            }
            catch (Exception ex)
            {
                note?.Invoke("Таблица соответствий MAL → TMDB не скачалась (" + ex.Message + ")" + (File.Exists(path) ? ", беру сохранённую" : ", сопоставляю только поиском по названию"));
            }
        }

        var map = new Dictionary<int, Entry>();
        if (File.Exists(path))
        {
            try
            {
                foreach (var o in JArray.Parse(await File.ReadAllTextAsync(path)).OfType<JObject>())
                {
                    int? mal = Int(o["mal_id"]);
                    if (mal == null)
                        continue;

                    var e = new Entry { tvdb = Int(o["thetvdb_id"] ?? o["tvdb_id"]), type = ((string)o["type"] ?? string.Empty).ToUpperInvariant() };

                    var tmdb = o["themoviedb_id"] ?? o["tmdb_id"];
                    if (tmdb is JObject to)
                    {
                        e.tmdb_movie = Int(to["movie"]);
                        e.tmdb_tv = Int(to["tv"]);
                    }
                    else
                    {
                        e.tmdb = Int(tmdb);
                    }

                    string imdb = (string)(o["imdb_id"] is JArray ia ? ia.FirstOrDefault() : o["imdb_id"]);
                    if (!string.IsNullOrEmpty(imdb))
                        e.imdb = imdb.Split(',')[0].Trim();

                    map[mal.Value] = e;
                }
            }
            catch (Exception ex)
            {
                note?.Invoke("Таблица соответствий повреждена: " + ex.Message);
            }
        }

        _map = map;
        _loaded = DateTime.UtcNow;
        return map;
    }

    static int? Int(JToken t)
    {
        if (t == null || t.Type == JTokenType.Null)
            return null;
        if (t.Type == JTokenType.Integer)
            return (int)t;
        return int.TryParse(t.ToString(), out int v) && v > 0 ? v : null;
    }
}

/// <summary>Тайтл из внешнего сервиса, который надо найти в TMDB</summary>
public class ExternalTitle
{
    public string source { get; set; }
    public string ext_id { get; set; }
    public string title { get; set; }
    public List<string> alt_titles { get; set; } = new List<string>();
    public int year { get; set; }
    /// <summary>tv, movie или пусто — неизвестно</summary>
    public string kind { get; set; }
    public int? mal_id { get; set; }
    public List<string> categories { get; set; } = new List<string>();
}

public class ImportState
{
    public bool running { get; set; }
    public string source { get; set; }
    public string profile_id { get; set; }
    public string stage { get; set; }
    public int total { get; set; }
    public int processed { get; set; }
    public int by_ids { get; set; }
    public int by_search { get; set; }
    public int merged { get; set; }
    public int queued { get; set; }
    public int imported { get; set; }
    public string error { get; set; }
    public long started { get; set; }
    public long finished { get; set; }
    public List<string> notes { get; set; } = new List<string>();
}

/// <summary>
/// Импорт во внешний профиль: внешние тайтлы → TMDB → HubLists.Merge. Несовпавшее — в очередь
/// ручного сопоставления. Один импорт за раз; токены и cookie живут только в замыкании задачи.
/// </summary>
public static class Importer
{
    static ImportState _state = new ImportState();
    static readonly object _lock = new object();

    public static ImportState State => _state;

    /// <summary>Сезоны одного сериала в TMDB — одна карточка: берём «самый живой» статус</summary>
    static readonly string[] StatusPriority = { "look", "rewatch", "continued", "scheduled", "thrown", "viewed" };

    public static bool Start(string source, HubProfile profile, string host, Func<ImportState, Task<List<ExternalTitle>>> fetch)
    {
        lock (_lock)
        {
            if (_state.running)
                return false;

            _state = new ImportState { running = true, source = source, profile_id = profile.id, stage = "Загрузка списков", started = HubStore.Now() };
        }

        var state = _state;
        Tmdb.Prepare(host);

        _ = Task.Run(async () =>
        {
            try
            {
                var titles = await fetch(state);
                await Run(state, profile, titles);
            }
            catch (Exception ex)
            {
                state.error = ex.Message;
            }
            finally
            {
                state.stage = state.error == null ? "Готово" : "Ошибка";
                state.finished = HubStore.Now();
                state.running = false;
            }
        });

        return true;
    }

    static async Task Run(ImportState state, HubProfile profile, List<ExternalTitle> titles)
    {
        state.total = titles.Count;
        state.stage = "Сопоставление с TMDB";

        var map = await AnimeMap.Load(n => state.notes.Add(n));
        var matched = new System.Collections.Concurrent.ConcurrentBag<(JObject card, string kind, ExternalTitle t)>();

        var cnt = new Counters();
        using var gate = new SemaphoreSlim(4);
        var tasks = titles.Select(async t =>
        {
            await gate.WaitAsync();
            try
            {
                var (card, kind, candidates, viaIds) = await Resolve(t, map);
                if (card != null)
                {
                    matched.Add((card, kind, t));
                    if (viaIds) Interlocked.Increment(ref cnt.byIds); else Interlocked.Increment(ref cnt.bySearch);
                }
                else
                {
                    HubStore.QueueAdd(new HubStore.QueueItem
                    {
                        profile_id = profile.id,
                        source = t.source,
                        ext_id = t.ext_id,
                        title = t.title,
                        year = t.year,
                        kind = t.kind,
                        categories = t.categories,
                        candidates = candidates
                    });
                    Interlocked.Increment(ref cnt.queued);
                }
            }
            catch (HttpRequestException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lock (state.notes)
                {
                    if (state.notes.Count < 30)
                        state.notes.Add(t.title + ": " + ex.Message);
                }
            }
            finally
            {
                Interlocked.Increment(ref cnt.processed);
                state.processed = cnt.processed;
                state.by_ids = cnt.byIds;
                state.by_search = cnt.bySearch;
                state.queued = cnt.queued;
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);

        state.stage = "Запись в списки профиля";

        // Несколько внешних тайтлов (сезоны) → одна карточка TMDB
        var portable = new List<HubLists.Portable>();
        foreach (var g in matched.GroupBy(m => m.kind + ":" + m.card["id"]))
        {
            var cats = g.SelectMany(m => m.t.categories).Distinct().ToList();
            string status = StatusPriority.FirstOrDefault(cats.Contains);
            cats = cats.Where(c => !HubLists.Statuses.Contains(c)).ToList();
            if (status != null)
                cats.Add(status);

            if (g.Count() > 1)
                state.merged += g.Count() - 1;

            var first = g.First();
            portable.Add(new HubLists.Portable { id = first.card["id"].ToString(), card = first.card, categories = cats });
        }

        state.imported = await HubLists.Merge(profile, portable);
    }

    class Counters
    {
        public int processed, byIds, bySearch, queued;
    }

    static string Norm(string s)
        => Regex.Replace((s ?? string.Empty).ToLowerInvariant().Replace('ё', 'е'), "[^a-zа-я0-9]+", string.Empty);

    static int YearOf(JObject t)
    {
        string d = (string)(t["release_date"] ?? t["first_air_date"]) ?? string.Empty;
        return d.Length >= 4 && int.TryParse(d.Substring(0, 4), out int y) ? y : 0;
    }

    /// <summary>По id из таблицы соответствий, потом поиском по названию и году</summary>
    static async Task<(JObject card, string kind, JArray candidates, bool viaIds)> Resolve(ExternalTitle t, Dictionary<int, AnimeMap.Entry> map)
    {
        if (t.mal_id != null && map.TryGetValue(t.mal_id.Value, out var e))
        {
            bool movie = e.type == "MOVIE" || t.kind == "movie";

            if (e.tmdb_tv != null && !movie)
            {
                var d = await Tmdb.Get("tv/" + e.tmdb_tv);
                if (d != null) return (Tmdb.Card(d, "tv"), "tv", null, true);
            }
            if (e.tmdb_movie != null)
            {
                var d = await Tmdb.Get("movie/" + e.tmdb_movie);
                if (d != null) return (Tmdb.Card(d, "movie"), "movie", null, true);
            }
            if (e.tmdb != null)
            {
                string k = movie ? "movie" : "tv";
                var d = await Tmdb.Get(k + "/" + e.tmdb);
                if (d != null && Plausible(d, t)) return (Tmdb.Card(d, k), k, null, true);
            }
            if (!string.IsNullOrEmpty(e.imdb))
            {
                var f = await Tmdb.Get("find/" + Uri.EscapeDataString(e.imdb), "external_source=imdb_id");
                var hit = Found(f, movie);
                if (hit.card != null) return (hit.card, hit.kind, null, true);
            }
            if (e.tvdb != null && !movie)
            {
                var f = await Tmdb.Get("find/" + e.tvdb, "external_source=tvdb_id");
                var hit = Found(f, movie);
                if (hit.card != null) return (hit.card, hit.kind, null, true);
            }
        }

        // Поиск: сначала с годом, затем без; совпадение названия и года ±1 — автоматически
        var names = new[] { t.title }.Concat(t.alt_titles ?? new List<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
        var seen = new Dictionary<string, (JObject item, string kind)>();

        foreach (string name in names)
        {
            foreach (int year in t.year > 0 ? new[] { t.year, 0 } : new[] { 0 })
            {
                foreach (var (item, kind) in await Tmdb.Search(name, string.IsNullOrEmpty(t.kind) ? null : t.kind, year))
                    seen.TryAdd(kind + ":" + item["id"], (item, kind));

                var exact = seen.Values.Where(r =>
                {
                    var titles = new[] { (string)r.item["title"], (string)r.item["name"], (string)r.item["original_title"], (string)r.item["original_name"] }.Select(Norm);
                    int y = YearOf(r.item);
                    return titles.Contains(Norm(name)) && (t.year == 0 || y == 0 || Math.Abs(y - t.year) <= 1);
                }).ToList();

                if (exact.Count == 1)
                    return (Tmdb.Card(exact[0].item, exact[0].kind), exact[0].kind, null, false);

                if (exact.Count > 1)
                {
                    // Несколько одинаковых названий: тот, у кого год совпал точно, если такой один
                    var sameYear = exact.Where(r => YearOf(r.item) == t.year).ToList();
                    if (sameYear.Count == 1)
                        return (Tmdb.Card(sameYear[0].item, sameYear[0].kind), sameYear[0].kind, null, false);
                    break;
                }
            }
        }

        var candidates = new JArray(seen.Values
            .OrderBy(r => Math.Abs(YearOf(r.item) - t.year))
            .Take(6)
            .Select(r => Tmdb.Brief(r.item, r.kind)));
        return (null, null, candidates, false);
    }

    static bool Plausible(JObject d, ExternalTitle t)
    {
        int y = YearOf(d);
        return t.year == 0 || y == 0 || Math.Abs(y - t.year) <= 30;
    }

    static (JObject card, string kind) Found(JObject f, bool movie)
    {
        if (f == null)
            return (null, null);

        var tv = (f["tv_results"] as JArray)?.OfType<JObject>().FirstOrDefault();
        var mv = (f["movie_results"] as JArray)?.OfType<JObject>().FirstOrDefault();

        if (movie && mv != null) return (Tmdb.Card(mv, "movie"), "movie");
        if (tv != null) return (Tmdb.Card(tv, "tv"), "tv");
        if (mv != null) return (Tmdb.Card(mv, "movie"), "movie");
        return (null, null);
    }

    /// <summary>Ручное сопоставление из очереди: карточка TMDB по выбранному id</summary>
    public static async Task<int> ResolveQueued(HubStore.QueueItem item, HubProfile profile, string kind, string tmdbId, string host)
    {
        Tmdb.Prepare(host);
        var d = await Tmdb.Get(kind + "/" + Uri.EscapeDataString(tmdbId));
        var card = Tmdb.Card(d, kind);
        if (card == null)
            throw new InvalidOperationException("В TMDB нет такого тайтла");

        int n = await HubLists.Merge(profile, new[] { new HubLists.Portable { id = card["id"].ToString(), card = card, categories = item.categories } });
        HubStore.QueueDelete(item.id);
        return n;
    }
}
