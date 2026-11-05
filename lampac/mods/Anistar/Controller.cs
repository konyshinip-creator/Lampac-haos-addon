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
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace Anistar;

/// <summary>
/// anistar.org (DLE, поиск в CP1251) + собственный плеер /test/player2/videoas.php?id=ID[&amp;sub=1].
/// Потоки HLS на sf2.an-media.org требуют Referer anistar.org → всегда через прокси Lampac.
/// </summary>
public class AnistarController : BaseOnlineController
{
    public AnistarController() : base(ModInit.conf) { }

    record SearchItem(long id, string title, string year, string img);

    record Entry(string voice, int episode, string name, List<(string quality, string url)> streams);

    static readonly Regex rxBlock = new("<div class=\"title_left\"><a href=\"(?:https?://[^/\"]+)?/(\\d+)-[^\"]+\\.html\"\\s*>([^<]+)</a>", RegexOptions.Compiled);
    static readonly Regex rxYear = new("/filter/year/(\\d{4})/", RegexOptions.Compiled);
    static readonly Regex rxImg = new("<img itemprop=\"image\" src=\"([^\"]+)\"", RegexOptions.Compiled);
    static readonly Regex rxEntry = new("title:\"([^\"]*)\",\\s*media_id:\"(\\d+)\",[\\s\\S]*?type_dub:'(\\d+)',[\\s\\S]*?file:\"([^\"]+)\"", RegexOptions.Compiled);
    static readonly Regex rxVideo = new("/video/([0-9a-f]{32})/(\\d+)\\.mp4", RegexOptions.Compiled);
    static readonly Regex rxNum = new("(\\d+)", RegexOptions.Compiled);
    static readonly Regex rxStudio = new("\\(([^)]+)\\)", RegexOptions.Compiled);

    [HttpGet, Staticache(manually: true)]
    [Route("lite/anistar")]
    async public Task<ActionResult> Index(string title, string original_title, int year, long aid = 0, string t = null, bool rjson = false, bool similar = false)
    {
        if (await IsRequestBlocked(rch: false))
            return badInitMsg;

        string enc_title = HttpUtility.UrlEncode(title);
        string enc_original_title = HttpUtility.UrlEncode(original_title);

        #region Поиск
        if (aid == 0)
        {
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(original_title))
                return OnError();

            var search = await InvokeCacheResult<List<SearchItem>>($"anistar:search:{title}:{original_title}", TimeSpan.FromHours(4), async e =>
            {
                var cp1251 = Encoding.GetEncoding(1251);
                var list = new List<SearchItem>();
                bool reqOk = false;

                foreach (string q in new[] { title, original_title }.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct())
                {
                    string html = await httpHydra.Get($"{init.host}/index.php?do=search&subaction=search&story={HttpUtility.UrlEncode(q, cp1251)}", encoding: cp1251, safety: true);
                    if (string.IsNullOrEmpty(html))
                        continue;

                    reqOk = true;

                    foreach (string block in html.Split("<div class=\"news\"").Skip(1))
                    {
                        // новости/статьи не содержат блока «Серии» — пропускаем
                        if (!block.Contains("<b>Серии: </b>", StringComparison.Ordinal))
                            continue;

                        var m = rxBlock.Match(block);
                        if (!m.Success || !long.TryParse(m.Groups[1].Value, out long id) || list.Any(i => i.id == id))
                            continue;

                        string name = WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
                        string img = rxImg.Match(block).Groups[1].Value;
                        if (img.StartsWith("/"))
                            img = init.host + img;

                        list.Add(new SearchItem(id, name, rxYear.Match(block).Groups[1].Value, img));
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

                var matched = found.Where(i => TitleParts(i.title).Any(p =>
                    (stitle != null && SearchNameTo.Contains(p, stitle)) ||
                    (sorig != null && SearchNameTo.Contains(p, sorig)))).ToList();

                if (matched.Count > 1 && year > 0)
                {
                    var byYear = matched.Where(i => i.year == year.ToString()).ToList();
                    if (byYear.Count > 0)
                        matched = byYear;
                }

                if (matched.Count == 1)
                    return LocalRedirect(accsArgs($"/lite/anistar?rjson={rjson}&title={enc_title}&original_title={enc_original_title}&year={year}&aid={matched[0].id}"));

                if (matched.Count > 0)
                    found = matched;
            }

            var stpl = new SimilarTpl(found.Count);
            foreach (var item in found)
            {
                stpl.Append(
                    item.title,
                    item.year,
                    string.Empty,
                    $"{host}/lite/anistar?rjson={rjson}&title={enc_title}&original_title={enc_original_title}&year={year}&aid={item.id}",
                    PosterApi.Size(item.img)
                );
            }

            return ContentTpl(stpl);
        }
        #endregion

        #region Плейлист
        var cache = await InvokeCacheResult<List<Entry>>($"anistar:playlist:{aid}", TimeSpan.FromMinutes(40), async e =>
        {
            var entries = new List<Entry>();
            bool reqOk = false;

            foreach (bool sub in new[] { false, true })
            {
                string html = await httpHydra.Get($"{init.host}/test/player2/videoas.php?id={aid}{(sub ? "&sub=1" : string.Empty)}", addheaders: HeadersModel.Init(("referer", $"{init.host}/")), safety: true);
                if (string.IsNullOrEmpty(html))
                    continue;

                reqOk = true;
                entries.AddRange(ParsePlaylist(html, sub));
            }

            if (entries.Count == 0)
                return e.Fail(reqOk ? "Плеер AniStar для этого тайтла недоступен" : "playlist", refresh_proxy: !reqOk);

            return e.Success(entries);
        });

        if (!cache.IsSuccess)
            return OnError(cache.ErrorMsg);

        var voices = cache.Value.Select(i => i.voice).Distinct().ToList();
        if (string.IsNullOrEmpty(t) || !voices.Contains(t))
            t = voices[0];

        var vtpl = new VoiceTpl(voices.Count);
        foreach (string v in voices)
            vtpl.Append(v, v == t, $"{host}/lite/anistar?rjson={rjson}&title={enc_title}&original_title={enc_original_title}&year={year}&aid={aid}&t={HttpUtility.UrlEncode(v)}");

        var headers = StreamHeaders();
        var episodes = cache.Value.Where(i => i.voice == t).OrderBy(i => i.episode).ToList();
        var etpl = new EpisodeTpl(vtpl, episodes.Count);

        foreach (var ep in episodes)
        {
            var streamquality = new StreamQualityTpl();
            foreach (var s in ep.streams.OrderByDescending(s => int.TryParse(s.quality, out int q) ? q : 0))
                streamquality.Append(HostStreamProxy(s.url, headers, force_streamproxy: true), $"{s.quality}p");

            var first = streamquality.Firts();
            if (first == null)
                continue;

            etpl.Append(
                ep.name,
                title ?? original_title,
                "1",
                ep.episode.ToString(),
                first.link,
                streamquality: streamquality,
                voice_name: t,
                vast: init.vast
            );
        }

        return ContentTpl(etpl);
        #endregion
    }

    #region Helpers
    List<HeadersModel> StreamHeaders()
    {
        string site = init.host.TrimEnd('/');
        return HeadersModel.Init(("origin", site), ("referer", $"{site}/"));
    }

    static IEnumerable<string> TitleParts(string title)
        => title.Split(" / ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static List<Entry> ParsePlaylist(string html, bool sub)
    {
        var list = new List<Entry>();
        int index = 0;

        foreach (Match m in rxEntry.Matches(html))
        {
            index++;
            string name = m.Groups[1].Value.Trim();
            string typeDub = m.Groups[3].Value;
            string file = m.Groups[4].Value;

            var streams = ParseFile(file);
            if (streams.Count == 0)
                continue;

            string voice;
            if (sub)
            {
                voice = "Субтитры";
            }
            else
            {
                string studio = rxStudio.Match(name).Groups[1].Value.Trim();
                bool multi = typeDub == "2" || name.Contains("Многоголос", StringComparison.OrdinalIgnoreCase);

                if (!string.IsNullOrEmpty(studio) && !studio.Contains("Многоголос", StringComparison.OrdinalIgnoreCase))
                    voice = studio;
                else
                    voice = multi ? "AniStar (многоголосая)" : "AniStar";
            }

            var num = rxNum.Match(name);
            int episode = num.Success && int.TryParse(num.Groups[1].Value, out int n) && n > 0 ? n : index;

            if (string.IsNullOrEmpty(name))
                name = $"{episode} серия";

            // одна серия может встретиться дважды — оставляем первую
            if (list.Any(i => i.voice == voice && i.episode == episode && i.name == name))
                continue;

            list.Add(new Entry(voice, episode, name, streams));
        }

        return list;
    }

    /// <summary>
    /// file:"/test/player2/playlist_hls.php?360=URL&amp;720=URL&amp;type=.mp4" → (качество, m3u8 на sf2).
    /// Ссылки cdn:'1' (sfv.an-media.org с ключом) переписываются на sf2, они там же лежат без ключа.
    /// </summary>
    static List<(string quality, string url)> ParseFile(string file)
    {
        var result = new List<(string quality, string url)>();
        file = file.Replace("&amp;", "&").Replace("\\/", "/");

        int q = file.IndexOf('?');
        if (q < 0)
            return result;

        foreach (string part in file.Substring(q + 1).Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0)
                continue;

            string key = part.Substring(0, eq);
            if (!int.TryParse(key, out _))
                continue;

            string url = HttpUtility.UrlDecode(part.Substring(eq + 1));
            if (string.IsNullOrWhiteSpace(url))
                continue;

            var m = rxVideo.Match(url);
            if (m.Success)
                url = $"https://sf2.an-media.org/video/{m.Groups[1].Value}/{m.Groups[2].Value}.mp4/index.m3u8";

            if (result.Any(i => i.quality == key))
                continue;

            result.Add((key, url));
        }

        return result;
    }
    #endregion
}
