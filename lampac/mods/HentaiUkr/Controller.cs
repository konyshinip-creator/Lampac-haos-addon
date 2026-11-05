using Microsoft.AspNetCore.Mvc;
using Shared;
using Shared.Attributes;
using Shared.Models.SISI.Base;
using Shared.Models.SISI.OnResult;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace HentaiUkr;

/// <summary>
/// hentaiukr.com. Весь каталог лежит в /search/objects.json, используется только раздел hanime (видео).
/// Файлы серий и качества описаны в /video/{slug}/plur.cfg.json, сами mp4 открываются без заголовков.
///
/// Тайтлы с признаками школьного возраста персонажей не показываются, и это не настройка. Тайтл без тегов
/// проверить нельзя, поэтому он тоже не показывается. Плеер проверяет slug по этому же списку.
/// </summary>
public class HentaiUkrController : BaseSisiController
{
    public HentaiUkrController() : base(ModInit.conf) { }

    const int pageSize = 28;

    static readonly HashSet<string> excludedTags = new(StringComparer.OrdinalIgnoreCase) { "Школярки", "Школа" };
    static readonly Regex rxExcludedName = new("лолі|лоли|шота|школ|loli|shota|school", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex rxSlug = new("^/video/([^/\"]+)/$", RegexOptions.Compiled);

    [HttpGet, Staticache]
    [Route("hentaiukr")]
    async public Task<ActionResult> Index(string search, int pg = 1)
    {
        if (await IsRequestBlocked(rch: false))
            return badInitMsg;

        if (pg < 1)
            pg = 1;

        var cache = await InvokeCacheResult("hentaiukr:catalog", 60, jsonContext.ListPlaylistItem, LoadCatalog);
        if (!cache.IsSuccess)
            return OnError(cache.ErrorMsg);

        IEnumerable<PlaylistItem> items = cache.Value;
        if (!string.IsNullOrWhiteSpace(search))
            items = items.Where(i => i.name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));

        var filtered = items.ToList();
        var page = filtered.Skip((pg - 1) * pageSize).Take(pageSize).ToList();
        if (page.Count == 0)
            return OnError("playlists", false);

        return PlaylistResult(page, cache.ISingleCache, null, total_pages: (filtered.Count + pageSize - 1) / pageSize);
    }

    [HttpGet, Staticache(manually: true)]
    [Route("hentaiukr/vidosik")]
    async public Task<ActionResult> Video(string uri)
    {
        if (await IsRequestBlocked(rch: false))
            return badInitMsg;

        if (string.IsNullOrEmpty(uri))
            return OnError("uri", false);

        var catalog = await InvokeCacheResult("hentaiukr:catalog", 60, jsonContext.ListPlaylistItem, LoadCatalog);
        if (!catalog.IsSuccess)
            return OnError(catalog.ErrorMsg);

        if (!catalog.Value.Any(i => i.bookmark?.href == uri))
            return OnError("uri", false);

        var cache = await InvokeCacheResult($"hentaiukr:view:{uri}", 60, jsonContext.StreamItem, async e =>
        {
            string json = await httpHydra.Get($"{init.host}/video/{uri}/plur.cfg.json", safety: true);
            if (string.IsNullOrEmpty(json))
                return e.Fail("json", refresh_proxy: true);

            var qualitys = ParseStreams(json, $"{init.host}/video/{uri}/");
            if (qualitys.Count == 0)
                return e.Fail("files");

            return e.Success(new StreamItem { qualitys = qualitys });
        });

        return OnResult(cache);
    }

    #region Parse
    async Task<Shared.Models.CacheResult<List<PlaylistItem>>> LoadCatalog(Shared.Models.CacheResult<List<PlaylistItem>> e)
    {
        string json = await httpHydra.Get($"{init.host}/search/objects.json", safety: true);
        if (string.IsNullOrEmpty(json))
            return e.Fail("json", refresh_proxy: true);

        var list = ParseCatalog(json);
        if (list.Count == 0)
            return e.Fail("playlists");

        return e.Success(list);
    }

    List<PlaylistItem> ParseCatalog(string json)
    {
        var list = new List<(string date, PlaylistItem item)>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("hanime", out var hanime) || hanime.ValueKind != JsonValueKind.Array)
                return new List<PlaylistItem>();

            foreach (var obj in hanime.EnumerateArray())
            {
                string name = Str(obj, "name").Trim();
                string eng = Str(obj, "eng_name").Trim();
                var slug = rxSlug.Match(Str(obj, "url"));

                if (string.IsNullOrEmpty(name) || !slug.Success || IsExcluded(obj, name, eng))
                    continue;

                string thumb = Str(obj, "thumb");
                if (thumb.StartsWith("/"))
                    thumb = init.host + thumb;

                list.Add((Str(obj, "add_date"), new PlaylistItem()
                {
                    name = string.IsNullOrEmpty(eng) || eng == name ? name : $"{name} / {eng}",
                    video = $"hentaiukr/vidosik?uri={HttpUtility.UrlEncode(slug.Groups[1].Value)}",
                    picture = thumb,
                    json = true,
                    bookmark = new Bookmark()
                    {
                        site = "hentaiukr",
                        href = slug.Groups[1].Value,
                        image = thumb
                    }
                }));
            }
        }
        catch (JsonException)
        {
            return new List<PlaylistItem>();
        }

        return list.OrderByDescending(i => i.date, StringComparer.Ordinal).Select(i => i.item).ToList();
    }

    static bool IsExcluded(JsonElement obj, string name, string eng)
    {
        if (rxExcludedName.IsMatch(name) || rxExcludedName.IsMatch(eng))
            return true;

        if (!obj.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array || tags.GetArrayLength() == 0)
            return true;

        return tags.EnumerateArray().Any(t => excludedTags.Contains(Str(t, "name").Trim()));
    }

    static Dictionary<string, string> ParseStreams(string json, string baseUrl)
    {
        var episodes = new List<List<(int size, string url)>>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return new Dictionary<string, string>();

            foreach (var ep in doc.RootElement.EnumerateArray())
            {
                if (!ep.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
                    continue;

                var files = new List<(int size, string url)>();
                foreach (var s in sources.EnumerateArray())
                {
                    string src = Str(s, "src");
                    if (string.IsNullOrEmpty(src))
                        continue;

                    int.TryParse(Str(s, "size"), out int size);
                    files.Add((size, src.StartsWith("http") ? src : baseUrl + src.TrimStart('/')));
                }

                if (files.Count > 0)
                    episodes.Add(files.OrderByDescending(f => f.size).ToList());
            }
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }

        var qualitys = new Dictionary<string, string>();

        if (episodes.Count == 1)
        {
            foreach (var f in episodes[0])
                qualitys.TryAdd(f.size > 0 ? $"{f.size}p" : "auto", f.url);
        }
        else
        {
            for (int i = 0; i < episodes.Count; i++)
                qualitys.TryAdd($"{i + 1} серия", episodes[i][0].url);
        }

        return qualitys;
    }

    static string Str(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v))
            return string.Empty;

        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : v.ToString();
    }
    #endregion
}
