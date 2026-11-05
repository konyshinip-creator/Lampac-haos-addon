using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Shared;
using Shared.Attributes;
using Shared.Models.Base;
using Shared.Models.Templates;
using Shared.Services.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web;

namespace Senku;

/// <summary>
/// senku.one — JSON API, ключ тайтла = Shikimori ID.
/// Источники серии: kodik_direct / kodik_subtitles (m3u8 через edge.senku.one, без заголовков)
/// и admin_media (mp4 на cdnlibs, нужен Referer https://senku.one/ — отдаём через прокси Lampac).
/// </summary>
public class SenkuController : BaseOnlineController
{
    public SenkuController() : base(ModInit.conf) { }

    const string edgeHost = "https://edge.senku.one";

    record SearchItem(long id, string title, string year, string poster);

    record StreamItem(string studio, int quality, string url, bool media);

    [HttpGet, Staticache(manually: true)]
    [Route("lite/senku")]
    async public Task<ActionResult> Index(string title, string original_title, int year, long sid = 0, string t = null, bool rjson = false, bool similar = false)
    {
        if (await IsRequestBlocked(rch: false))
            return badInitMsg;

        string enc_title = HttpUtility.UrlEncode(title);
        string enc_original_title = HttpUtility.UrlEncode(original_title);

        #region Поиск
        if (sid == 0)
        {
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(original_title))
                return OnError();

            var search = await InvokeCacheResult<List<SearchItem>>($"senku:search:{title}:{original_title}", TimeSpan.FromHours(4), async e =>
            {
                var list = new List<SearchItem>();

                foreach (string q in new[] { title, original_title }.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct())
                {
                    var root = await httpHydra.Get<JObject>($"{init.apihost}/search?q={HttpUtility.UrlEncode(q)}", safety: true);
                    var results = root?["data"]?["results"] as JArray;
                    if (results == null)
                        continue;

                    foreach (var r in results)
                    {
                        long id = r.Value<long?>("id") ?? 0;
                        string name = r.Value<string>("title");
                        if (id == 0 || string.IsNullOrWhiteSpace(name) || list.Any(i => i.id == id))
                            continue;

                        string aired = r.Value<string>("aired_on");
                        list.Add(new SearchItem(id, name, aired?.Length >= 4 ? aired.Substring(0, 4) : string.Empty, r.Value<string>("poster")));
                    }

                    if (list.Count > 0)
                        break;
                }

                if (list.Count == 0)
                    return e.Fail("search", refresh_proxy: true);

                return e.Success(list);
            });

            if (!search.IsSuccess)
                return OnError(search.ErrorMsg);

            if (!similar)
            {
                // точное совпадение названия + года → сразу открываем
                string stitle = SearchNameTo.Convert(title);
                var exact = search.Value.Where(i => stitle != null && SearchNameTo.Equals(i.title, stitle)).ToList();
                if (exact.Count > 1 && year > 0)
                    exact = exact.Where(i => i.year == year.ToString()).ToList();

                if (exact.Count == 1)
                    return LocalRedirect(accsArgs($"/lite/senku?rjson={rjson}&title={enc_title}&original_title={enc_original_title}&year={year}&sid={exact[0].id}"));
            }

            var stpl = new SimilarTpl(search.Value.Count);
            foreach (var item in search.Value)
            {
                stpl.Append(
                    item.title,
                    item.year,
                    string.Empty,
                    $"{host}/lite/senku?rjson={rjson}&title={enc_title}&original_title={enc_original_title}&year={year}&sid={item.id}",
                    PosterApi.Size(item.poster)
                );
            }

            return ContentTpl(stpl);
        }
        #endregion

        #region Карточка: число серий
        var anime = await InvokeCacheResult<(int episodes, string name)>($"senku:anime:{sid}", TimeSpan.FromMinutes(30), async e =>
        {
            var root = await httpHydra.Get<JObject>($"{init.apihost}/anime/{sid}", safety: true);
            var data = root?["data"];
            if (data == null || root.Value<bool?>("ok") != true)
                return e.Fail("anime", refresh_proxy: true);

            int episodes = data.Value<int?>("available_episodes") ?? 0;
            if (episodes <= 0)
                episodes = data.Value<int?>("senku_available_episodes") ?? data.Value<int?>("kodik_available_episodes") ?? 0;

            if (episodes <= 0 && string.Equals(data.Value<string>("kind"), "movie", StringComparison.OrdinalIgnoreCase))
                episodes = 1;

            if (episodes <= 0)
                return e.Fail("episodes");

            return e.Success((episodes, data.Value<string>("title")));
        });

        if (!anime.IsSuccess)
            return OnError(anime.ErrorMsg);
        #endregion

        #region Озвучки (по первой и последней серии)
        var voices = await InvokeCacheResult<List<string>>($"senku:voices:{sid}:{anime.Value.episodes}", TimeSpan.FromMinutes(40), async e =>
        {
            var names = new List<string>();

            foreach (int ep in new[] { 1, anime.Value.episodes }.Distinct())
            {
                var streams = await LoadSources(sid, ep);
                if (streams == null)
                    continue;

                foreach (string studio in streams.Select(s => s.studio))
                {
                    if (!names.Contains(studio))
                        names.Add(studio);
                }
            }

            if (names.Count == 0)
                return e.Fail("sources", refresh_proxy: true);

            // озвучки выше субтитров
            return e.Success(names.OrderBy(n => IsSubtitles(n) ? 1 : 0).ToList());
        });

        if (!voices.IsSuccess)
            return OnError(voices.ErrorMsg);

        if (string.IsNullOrEmpty(t) || !voices.Value.Contains(t))
            t = voices.Value[0];

        var vtpl = new VoiceTpl(voices.Value.Count);
        foreach (string v in voices.Value)
            vtpl.Append(v, v == t, $"{host}/lite/senku?rjson={rjson}&title={enc_title}&original_title={enc_original_title}&year={year}&sid={sid}&t={HttpUtility.UrlEncode(v)}");
        #endregion

        string displayTitle = title ?? original_title ?? anime.Value.name;
        var etpl = new EpisodeTpl(vtpl, anime.Value.episodes);

        for (int ep = 1; ep <= anime.Value.episodes; ep++)
        {
            string link = accsArgs($"{host}/lite/senku/video?sid={sid}&ep={ep}&t={HttpUtility.UrlEncode(t)}&title={enc_title}");

            etpl.Append(
                $"{ep} серия",
                displayTitle,
                "1",
                ep.ToString(),
                link,
                "call",
                streamlink: $"{link}&play=true",
                voice_name: t,
                vast: init.vast
            );
        }

        return ContentTpl(etpl);
    }

    #region Video
    [HttpGet]
    [Route("lite/senku/video")]
    async public Task<ActionResult> Video(long sid, int ep, string t, string title, bool play)
    {
        if (await IsRequestBlocked(rch: false, rch_check: !play))
            return badInitMsg;

        var cache = await InvokeCacheResult<List<StreamItem>>($"senku:sources:{sid}:{ep}", TimeSpan.FromMinutes(50), async e =>
        {
            var streams = await LoadSources(sid, ep);
            if (streams == null || streams.Count == 0)
                return e.Fail("sources", refresh_proxy: true);

            return e.Success(streams);
        });

        if (!cache.IsSuccess)
            return OnError(cache.ErrorMsg);

        var items = cache.Value.Where(s => s.studio == t).ToList();
        if (items.Count == 0)
            items = cache.Value.Where(s => s.studio == cache.Value[0].studio).ToList();

        var referer = HeadersModel.Init(("referer", $"{init.host}/"), ("origin", init.host));

        var streamquality = new StreamQualityTpl();
        foreach (var s in items.OrderByDescending(s => s.quality))
        {
            string link = s.media
                ? HostStreamProxy(s.url, referer, force_streamproxy: true)
                : HostStreamProxy(s.url);

            streamquality.Append(link, $"{s.quality}p");
        }

        var first = streamquality.Firts();
        if (first == null)
            return OnError();

        if (play)
            return RedirectToPlay(first.link);

        return ContentTo(VideoTpl.ToJson(
            "play",
            first.link,
            $"{title} ({ep} серия)",
            streamquality: streamquality,
            vast: init.vast,
            httpContext: HttpContext
        ));
    }
    #endregion

    #region Helpers
    async Task<List<StreamItem>> LoadSources(long sid, int ep)
    {
        var root = await httpHydra.Get<JObject>($"{init.apihost}/anime/{sid}/sources?ep={ep}", statusCodeOK: false, safety: true);
        if (root == null || root.Value<bool?>("ok") != true)
            return null;

        var episodes = root["data"]?["episodes"] as JObject;
        var episode = episodes?[ep.ToString()] as JObject ?? episodes?.Properties().FirstOrDefault()?.Value as JObject;
        if (episode == null)
            return null;

        var list = new List<StreamItem>();

        foreach (var studioProp in episode.Properties())
        {
            string studio = NormalizeStudio(studioProp.Name);
            if (string.IsNullOrEmpty(studio) || studioProp.Value is not JObject qualities)
                continue;

            foreach (var qProp in qualities.Properties())
            {
                if (qProp.Value is not JObject item)
                    continue;

                string url = item.Value<string>("url") ?? item.Value<string>("file") ?? item.Value<string>("hls");
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                int.TryParse(item.Value<string>("quality") ?? qProp.Name, out int quality);
                string source = item.Value<string>("source") ?? string.Empty;
                bool media = source == "admin_media" || url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || url.Contains(".mp4?");

                if (url.StartsWith("/"))
                    url = edgeHost + url;

                // одна студия может прийти дважды (2x2 / 2×2) — оставляем первую ссылку на качество
                if (list.Any(i => i.studio == studio && i.quality == quality))
                    continue;

                list.Add(new StreamItem(studio, quality, url, media));
            }
        }

        return list.Count > 0 ? list : null;
    }

    static string NormalizeStudio(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return name.Replace('×', 'x').Trim();
    }

    static bool IsSubtitles(string studio)
        => studio.Contains("Субтитр", StringComparison.OrdinalIgnoreCase) || studio.EndsWith(".Subtitles", StringComparison.OrdinalIgnoreCase);
    #endregion
}
