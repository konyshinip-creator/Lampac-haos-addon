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
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace Animaunt;

/// <summary>
/// animaunt.org (DLE, UTF-8). Видео — iframe play.openni.ru/?cid=N (Playerjs, прямые mp4 на gdr.trn.su).
/// Тайтлы, у которых плеер Kodik, этим модулем не показываются (их отдаёт балансер Kodik).
/// </summary>
public class AnimauntController : BaseOnlineController
{
    public AnimauntController() : base(ModInit.conf) { }

    record SearchItem(string href, string title, string img);

    record Episode(int number, string name, List<(string quality, string url)> streams);

    record Page(string player, List<Episode> episodes);

    static readonly Regex rxSearch = new("<a class=\"th-title\" href=\"(?:https?://[^/\"]+)?/(\\d+-[^\"]+\\.html)\">([^<]+)</a>", RegexOptions.Compiled);
    static readonly Regex rxSearchImg = new("<img src=\"([^\"]+)\" alt=", RegexOptions.Compiled);
    static readonly Regex rxIframe = new("<iframe[^>]*\\ssrc=\"((?:https?:)?//(?:play\\.openni\\.ru|[^\"/]*kodik[^\"/]*)/[^\"]+)\"", RegexOptions.Compiled);
    static readonly Regex rxEntry = new("\\{\"title\":\"([^\"]+)\",id:\"(\\d+)\",poster:\"[^\"]*\",\"file\":\"([^\"]+)\"\\}", RegexOptions.Compiled);
    static readonly Regex rxQuality = new("/mp4/(\\d+)/", RegexOptions.Compiled);
    static readonly Regex rxNum = new("(\\d+)", RegexOptions.Compiled);

    [HttpGet, Staticache(manually: true)]
    [Route("lite/animaunt")]
    async public Task<ActionResult> Index(string title, string original_title, int year, string href = null, bool rjson = false, bool similar = false)
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

            var search = await InvokeCacheResult<List<SearchItem>>($"animaunt:search:{title}:{original_title}", TimeSpan.FromHours(4), async e =>
            {
                var list = new List<SearchItem>();
                bool reqOk = false;

                foreach (string q in new[] { title, original_title }.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct())
                {
                    string html = await httpHydra.Get($"{init.host}/index.php?do=search&subaction=search&story={HttpUtility.UrlEncode(q)}", safety: true);
                    if (string.IsNullOrEmpty(html))
                        continue;

                    reqOk = true;

                    foreach (string block in html.Split("<div class=\"thumb ").Skip(1))
                    {
                        var m = rxSearch.Match(block);
                        if (!m.Success || list.Any(i => i.href == m.Groups[1].Value))
                            continue;

                        string img = rxSearchImg.Match(block).Groups[1].Value;
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

            var found = search.Value;
            if (!similar)
            {
                string stitle = SearchNameTo.Convert(title);
                var exact = stitle == null ? new List<SearchItem>() : found.Where(i => SearchNameTo.Equals(i.title, stitle)).ToList();

                if (exact.Count == 1)
                    return LocalRedirect(accsArgs($"/lite/animaunt?rjson={rjson}&title={enc_title}&original_title={enc_original_title}&year={year}&href={HttpUtility.UrlEncode(exact[0].href)}"));

                var matched = stitle == null ? new List<SearchItem>() : found.Where(i => SearchNameTo.Contains(i.title, stitle)).ToList();
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
                    $"{host}/lite/animaunt?rjson={rjson}&title={enc_title}&original_title={enc_original_title}&year={year}&href={HttpUtility.UrlEncode(item.href)}",
                    PosterApi.Size(item.img)
                );
            }

            return ContentTpl(stpl);
        }
        #endregion

        #region Серии
        var cache = await InvokeCacheResult<Page>($"animaunt:page:{href}", TimeSpan.FromMinutes(40), async e =>
        {
            string html = await httpHydra.Get($"{init.host}/{href.TrimStart('/')}", safety: true);
            if (string.IsNullOrEmpty(html))
                return e.Fail("page", refresh_proxy: true);

            string iframe = rxIframe.Match(html).Groups[1].Value;
            if (string.IsNullOrEmpty(iframe))
                return e.Fail("На странице AniMaunt нет плеера");

            if (iframe.StartsWith("//"))
                iframe = "https:" + iframe;

            if (iframe.Contains("kodik", StringComparison.OrdinalIgnoreCase))
                return e.Success(new Page("kodik", new List<Episode>()));

            string player = await httpHydra.Get(iframe, addheaders: HeadersModel.Init(("referer", $"{init.host}/")), safety: true);
            if (string.IsNullOrEmpty(player))
                return e.Fail("player", refresh_proxy: true);

            var episodes = ParsePlayer(player);
            if (episodes.Count == 0)
                return e.Fail("episodes");

            return e.Success(new Page("openni", episodes));
        });

        if (!cache.IsSuccess)
            return OnError(cache.ErrorMsg);

        if (cache.Value.player == "kodik")
            return OnError("Этот тайтл на AniMaunt идёт через плеер Kodik — выберите источник Kodik");

        var etpl = new EpisodeTpl(cache.Value.episodes.Count);
        foreach (var ep in cache.Value.episodes)
        {
            var streamquality = new StreamQualityTpl();
            foreach (var s in ep.streams.OrderByDescending(s => int.TryParse(s.quality, out int q) ? q : 0))
                streamquality.Append(HostStreamProxy(s.url), $"{s.quality}p");

            var first = streamquality.Firts();
            if (first == null)
                continue;

            etpl.Append(
                ep.name,
                title ?? original_title,
                "1",
                ep.number.ToString(),
                first.link,
                streamquality: streamquality,
                voice_name: "AniMaunt",
                vast: init.vast
            );
        }

        return ContentTpl(etpl);
        #endregion
    }

    #region Helpers
    static List<Episode> ParsePlayer(string html)
    {
        var list = new List<Episode>();
        int index = 0;

        foreach (Match m in rxEntry.Matches(html))
        {
            index++;
            string name = m.Groups[1].Value.Trim();

            var streams = new List<(string quality, string url)>();
            foreach (string part in Regex.Split(m.Groups[3].Value, ",(?=\\[)"))
            {
                var pm = Regex.Match(part, "^\\[([^\\]]+)\\](.+)$");
                if (!pm.Success)
                    continue;

                // "url1 or url2 or url3" — первый вариант основной, остальные запасные пониже качеством
                string url = pm.Groups[2].Value.Split(" or ")[0].Trim();
                string quality = rxQuality.Match(url).Groups[1].Value;
                if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(quality) || streams.Any(s => s.quality == quality))
                    continue;

                streams.Add((quality, url));
            }

            if (streams.Count == 0)
                continue;

            var num = rxNum.Match(name);
            int number = num.Success && int.TryParse(num.Groups[1].Value, out int n) && n > 0 ? n : index;

            list.Add(new Episode(number, name, streams));
        }

        return list.OrderBy(i => i.number).ToList();
    }
    #endregion
}
