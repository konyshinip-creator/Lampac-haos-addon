using Microsoft.AspNetCore.Mvc;
using Shared;
using Shared.Attributes;
using Shared.Models.Base;
using Shared.Models.Templates;
using Shared.Services.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace AniTokyo;

/// <summary>
/// anitokyo.tv (DLE, UTF-8). На странице тайтла RalodePlayer.init({...}): озвучки и серии, каждая серия —
/// iframe /video.php?id=N&amp;cat=M, а внутри него плеер. Показываем серии Kodik (codetype 110 и 33),
/// поток отдаёт встроенный модуль Kodik. Серии Sibnet (13) и VK (1) не поддерживаются.
/// </summary>
public class AniTokyoController : BaseOnlineController
{
    public AniTokyoController() : base(ModInit.conf) { }

    record SearchItem(string href, string title, string img);

    record Episode(int number, string name, long id, string cat);

    record Voice(string id, string name, List<Episode> episodes);

    static readonly HashSet<string> kodikTypes = new() { "110", "33" };

    static readonly Regex rxTitle = new("<h2 class=\"story-title\"><a href=\"(?:https?://[^/\"]+)?/([a-z]+/\\d+-[^\"/]+\\.html)\">([^<]+)</a>", RegexOptions.Compiled);
    static readonly Regex rxImg = new("<img src=\"([^\"]+)\" class=\"poster\"", RegexOptions.Compiled);
    static readonly Regex rxScode = new("video\\.php\\?id=(\\d+)&(?:amp;)?cat=([\\d,]+)", RegexOptions.Compiled);
    static readonly Regex rxNum = new("(\\d+)", RegexOptions.Compiled);
    static readonly Regex rxKodik = new("<iframe[^>]*\\ssrc=\"(?:https?:)?//[^\"/]*kodik[^\"/]*/((?:serial|video|seria)/\\d+/[0-9a-f]+)", RegexOptions.Compiled);

    [HttpGet, Staticache(manually: true)]
    [Route("lite/anitokyo")]
    async public Task<ActionResult> Index(string title, string original_title, int year, string href = null, string t = null, bool rjson = false, bool similar = false)
    {
        if (await IsRequestBlocked(rch: false))
            return badInitMsg;

        string enc_title = HttpUtility.UrlEncode(title);
        string enc_original_title = HttpUtility.UrlEncode(original_title);

        #region Поиск
        if (string.IsNullOrEmpty(href))
        {
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(original_title))
                return OnError();

            var search = await InvokeCacheResult<List<SearchItem>>($"anitokyo:search:{title}:{original_title}", TimeSpan.FromHours(4), async e =>
            {
                var list = new List<SearchItem>();
                bool reqOk = false;

                foreach (string q in new[] { title, original_title }.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct())
                {
                    string html = await httpHydra.Get($"{init.host}/index.php?do=search&subaction=search&story={HttpUtility.UrlEncode(q)}", safety: true);
                    if (string.IsNullOrEmpty(html))
                        continue;

                    reqOk = true;

                    foreach (string block in html.Split("<article class=\"story shortstory\">").Skip(1))
                    {
                        var m = rxTitle.Match(block);
                        if (!m.Success || list.Any(i => i.href == m.Groups[1].Value))
                            continue;

                        string img = rxImg.Match(block).Groups[1].Value;
                        if (img.StartsWith("/"))
                            img = init.host + img;

                        list.Add(new SearchItem(m.Groups[1].Value, WebUtility.HtmlDecode(m.Groups[2].Value).Trim(), img));
                    }

                    if (list.Count > 0)
                        break;
                }

                if (list.Count == 0)
                    return e.Fail("search", refresh_proxy: !reqOk);

                return e.Success(list);
            });

            if (!search.IsSuccess)
                return OnError(search.ErrorMsg);

            // поиск на сайте нечёткий — оставляем тайтлы, у которых одно из названий совпадает
            var found = search.Value;
            if (!similar)
            {
                string stitle = SearchNameTo.Convert(title);
                string sorig = SearchNameTo.Convert(original_title);

                var matched = found.Where(i => i.title.Split(" / ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(p =>
                    (stitle != null && SearchNameTo.Contains(p, stitle)) ||
                    (sorig != null && SearchNameTo.Contains(p, sorig)))).ToList();

                if (matched.Count == 1)
                    return LocalRedirect(accsArgs($"/lite/anitokyo?rjson={rjson}&title={enc_title}&original_title={enc_original_title}&year={year}&href={HttpUtility.UrlEncode(matched[0].href)}"));

                if (matched.Count > 0)
                    found = matched;
            }

            var stpl = new SimilarTpl(found.Count);
            foreach (var item in found)
            {
                stpl.Append(
                    item.title,
                    string.Empty,
                    string.Empty,
                    $"{host}/lite/anitokyo?rjson={rjson}&title={enc_title}&original_title={enc_original_title}&year={year}&href={HttpUtility.UrlEncode(item.href)}",
                    PosterApi.Size(item.img)
                );
            }

            return ContentTpl(stpl);
        }
        #endregion

        #region Серии
        if (!Regex.IsMatch(href, "^[a-z]+/\\d+-[^/]+\\.html$"))
            return OnError("href");

        var cache = await InvokeCacheResult<List<Voice>>($"anitokyo:page:{href}", TimeSpan.FromMinutes(40), async e =>
        {
            string html = await httpHydra.Get($"{init.host}/{href}", safety: true);
            if (string.IsNullOrEmpty(html))
                return e.Fail("page", refresh_proxy: true);

            var voices = ParsePlayer(html);
            if (voices.Count == 0)
                return e.Fail("У этого тайтла на AniTokyo нет серий в плеере Kodik");

            return e.Success(voices);
        });

        if (!cache.IsSuccess)
            return OnError(cache.ErrorMsg);

        var selected = cache.Value.FirstOrDefault(v => v.id == t) ?? cache.Value[0];

        var vtpl = new VoiceTpl(cache.Value.Count);
        foreach (var v in cache.Value)
            vtpl.Append(v.name, v == selected, $"{host}/lite/anitokyo?rjson={rjson}&title={enc_title}&original_title={enc_original_title}&year={year}&href={HttpUtility.UrlEncode(href)}&t={v.id}");

        var etpl = new EpisodeTpl(vtpl, selected.episodes.Count);
        foreach (var ep in selected.episodes)
        {
            string args = $"?title={enc_title}&original_title={enc_original_title}&id={ep.id}&cat={HttpUtility.UrlEncode(ep.cat)}&episode={ep.number}";

            etpl.Append(
                ep.name,
                title ?? original_title,
                "1",
                ep.number.ToString(),
                $"{host}/lite/anitokyo/video{args}",
                "call",
                streamlink: accsArgs($"{host}/lite/anitokyo/video{args}&play=true"),
                voice_name: selected.name,
                vast: init.vast
            );
        }

        return ContentTpl(etpl);
        #endregion
    }

    /// <summary>Находит ссылку Kodik для серии и передаёт её встроенному модулю Kodik.</summary>
    [HttpGet, Staticache(manually: true)]
    [Route("lite/anitokyo/video")]
    async public Task<ActionResult> Video(string title, string original_title, long id, string cat, int episode, bool play = false)
    {
        if (await IsRequestBlocked(rch: false))
            return badInitMsg;

        if (id <= 0 || string.IsNullOrEmpty(cat) || !Regex.IsMatch(cat, "^[\\d,]+$"))
            return OnError("id");

        var cache = await InvokeCacheResult<string>($"anitokyo:video:{id}", TimeSpan.FromHours(4), async e =>
        {
            string html = await httpHydra.Get($"{init.host}/video.php?id={id}&cat={cat}", addheaders: HeadersModel.Init(("referer", $"{init.host}/")), safety: true);
            if (string.IsNullOrEmpty(html))
                return e.Fail("video", refresh_proxy: true);

            string path = rxKodik.Match(html).Groups[1].Value;
            if (string.IsNullOrEmpty(path))
                return e.Fail("Плеер этой серии не поддерживается");

            return e.Success(path);
        });

        if (!cache.IsSuccess)
            return OnError(cache.ErrorMsg);

        string link = HttpUtility.UrlEncode($"//kodikplayer.com/{cache.Value}/720p");
        return LocalRedirect(accsArgs($"/lite/kodik/video?title={HttpUtility.UrlEncode(title)}&original_title={HttpUtility.UrlEncode(original_title)}&link={link}&episode={episode}&play={play.ToString().ToLower()}"));
    }

    #region Helpers
    static List<Voice> ParsePlayer(string html)
    {
        var list = new List<(int sort, Voice voice)>();

        const string marker = "RalodePlayer.init(";
        int start = html.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return new List<Voice>();

        try
        {
            // после объекта идут другие аргументы init(...), поэтому читаем только первое значение
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(html.Substring(start + marker.Length)));
            using var doc = JsonDocument.ParseValue(ref reader);

            foreach (var group in doc.RootElement.EnumerateObject())
            {
                if (!group.Value.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
                    continue;

                var episodes = new List<Episode>();
                foreach (var item in items.EnumerateObject())
                {
                    if (!kodikTypes.Contains(Str(item.Value, "codetype")))
                        continue;

                    var m = rxScode.Match(Str(item.Value, "scode"));
                    if (!m.Success || !long.TryParse(m.Groups[1].Value, out long id))
                        continue;

                    string name = Str(item.Value, "aname").Trim();
                    var num = rxNum.Match(name);
                    int number = num.Success && int.TryParse(num.Groups[1].Value, out int n) && n > 0
                        ? n
                        : int.TryParse(Str(item.Value, "lssort"), out int s) && s > 0 ? s : episodes.Count + 1;

                    episodes.Add(new Episode(number, string.IsNullOrEmpty(name) ? $"{number} серия" : name, id, m.Groups[2].Value));
                }

                if (episodes.Count == 0)
                    continue;

                string voice = WebUtility.HtmlDecode(Str(group.Value, "name")).Trim();
                int.TryParse(Str(group.Value, "sort"), out int sort);

                list.Add((sort, new Voice(group.Name, string.IsNullOrEmpty(voice) ? "Kodik" : voice, episodes.OrderBy(e => e.number).ToList())));
            }
        }
        catch (JsonException)
        {
            return new List<Voice>();
        }

        return list.OrderBy(i => i.sort).Select(i => i.voice).ToList();
    }

    static string Str(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v))
            return string.Empty;

        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : v.ToString();
    }
    #endregion
}
