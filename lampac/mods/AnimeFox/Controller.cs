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

namespace AnimeFox;

/// <summary>
/// animefox.org (DLE, UTF-8). На странице тайтла один iframe Kodik. Из его страницы берутся озвучки и
/// серии, а сам поток отдаёт встроенный модуль Kodik (/lite/kodik/video), поэтому он должен быть включён.
/// Раздел сайта /hentai/ этим модулем не показывается, только /anime/.
/// </summary>
public class AnimeFoxController : BaseOnlineController
{
    public AnimeFoxController() : base(ModInit.conf) { }

    const string kodikHost = "https://kodikplayer.com";

    record SearchItem(string href, string title, string year, string img);

    record Voice(string name, string path);

    record Episode(int number, string name, string link);

    record KodikPage(string season, string voice, List<Voice> voices, List<Episode> episodes);

    static readonly Regex rxTitle = new("class=\"short-title\" href=\"(?:https?://[^/\"]+)?/anime/(\\d+-[^\"/]+\\.html)\"[^>]*><h2>([^<]+)</h2>", RegexOptions.Compiled);
    static readonly Regex rxImg = new("<img src=\"([^\"]+)\"", RegexOptions.Compiled);
    static readonly Regex rxYear = new("<div class=\"short-cat\">[^,<]*,\\s*(\\d{4})\\s*,", RegexOptions.Compiled);
    static readonly Regex rxIframe = new("<iframe[^>]*\\ssrc=\"(?:https?:)?//[^\"/]*kodik[^\"/]*/((?:serial|video|seria)/\\d+/[0-9a-f]+)", RegexOptions.Compiled);
    static readonly Regex rxPath = new("^(?:serial|video|seria)/\\d+/[0-9a-f]+$", RegexOptions.Compiled);
    static readonly Regex rxTranslation = new("<option\\s+value=\"\\d+\"\\s+data-id=\"\\d+\"\\s+data-translation-type=\"(\\w+)\"\\s+data-media-id=\"(\\d+)\"\\s+data-media-hash=\"([0-9a-f]+)\"\\s+data-media-type=\"(serial|video|seria)\"\\s+data-title=\"([^\"]*)\"", RegexOptions.Compiled);
    static readonly Regex rxSerie = new("<option\\s+value=\"(\\d+)\"\\s+data-id=\"(\\d+)\"\\s+data-hash=\"([0-9a-f]+)\"\\s+data-title=\"([^\"]*)\"", RegexOptions.Compiled);
    static readonly Regex rxSeason = new("<option\\s+value=\"(\\d+)\"[^>]*\\sselected", RegexOptions.Compiled);
    static readonly Regex rxCurrent = new("var translationTitle = \"([^\"]*)\"", RegexOptions.Compiled);

    [HttpGet, Staticache(manually: true)]
    [Route("lite/animefox")]
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

            var search = await InvokeCacheResult<List<SearchItem>>($"animefox:search:{title}:{original_title}", TimeSpan.FromHours(4), async e =>
            {
                var list = new List<SearchItem>();
                bool reqOk = false;

                foreach (string q in new[] { title, original_title }.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct())
                {
                    string html = await httpHydra.Get($"{init.host}/index.php?do=search&subaction=search&story={HttpUtility.UrlEncode(q)}", safety: true);
                    if (string.IsNullOrEmpty(html))
                        continue;

                    reqOk = true;

                    foreach (string block in html.Split("<article class=\"short").Skip(1))
                    {
                        var m = rxTitle.Match(block);
                        if (!m.Success || list.Any(i => i.href == m.Groups[1].Value))
                            continue;

                        string img = rxImg.Match(block).Groups[1].Value;
                        if (img.StartsWith("/"))
                            img = init.host + img;

                        list.Add(new SearchItem(m.Groups[1].Value, WebUtility.HtmlDecode(m.Groups[2].Value).Trim(), rxYear.Match(block).Groups[1].Value, img));
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
                    return LocalRedirect(accsArgs($"/lite/animefox?rjson={rjson}&title={enc_title}&original_title={enc_original_title}&year={year}&href={HttpUtility.UrlEncode(matched[0].href)}"));

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
                    $"{host}/lite/animefox?rjson={rjson}&title={enc_title}&original_title={enc_original_title}&year={year}&href={HttpUtility.UrlEncode(item.href)}",
                    PosterApi.Size(item.img)
                );
            }

            return ContentTpl(stpl);
        }
        #endregion

        #region Плеер
        if (!Regex.IsMatch(href, "^\\d+-[^/]+\\.html$"))
            return OnError("href");

        var iframe = await InvokeCacheResult<string>($"animefox:iframe:{href}", TimeSpan.FromHours(4), async e =>
        {
            string html = await httpHydra.Get($"{init.host}/anime/{href}", safety: true);
            if (string.IsNullOrEmpty(html))
                return e.Fail("page", refresh_proxy: true);

            string path = rxIframe.Match(html).Groups[1].Value;
            if (string.IsNullOrEmpty(path))
                return e.Fail("На странице AnimeFox нет плеера Kodik");

            return e.Success(path);
        });

        if (!iframe.IsSuccess)
            return OnError(iframe.ErrorMsg);

        string kodikPath = !string.IsNullOrEmpty(t) && rxPath.IsMatch(t) ? t : iframe.Value;

        var cache = await InvokeCacheResult<KodikPage>($"animefox:kodik:{kodikPath}", TimeSpan.FromMinutes(40), async e =>
        {
            string html = await httpHydra.Get($"{kodikHost}/{kodikPath}/720p", addheaders: HeadersModel.Init(("referer", $"{init.host}/")), safety: true);
            if (string.IsNullOrEmpty(html))
                return e.Fail("kodik", refresh_proxy: true);

            return e.Success(ParseKodik(html));
        });

        if (!cache.IsSuccess)
            return OnError(cache.ErrorMsg);

        var page = cache.Value;

        // озвучка из самого iframe может отсутствовать в списке переводов
        var voices = page.voices.ToList();
        if (voices.Count == 0 || !voices.Any(v => v.path == kodikPath || v.name == page.voice))
            voices.Insert(0, new Voice(string.IsNullOrEmpty(page.voice) ? "Kodik" : page.voice, kodikPath));

        var selected = voices.FirstOrDefault(v => v.path == kodikPath) ?? voices.FirstOrDefault(v => v.name == page.voice) ?? voices[0];

        if (page.episodes.Count == 0)
        {
            var mtpl = new MovieTpl(title, original_title, voices.Count);
            foreach (var v in voices)
            {
                var links = KodikUrls(enc_title, enc_original_title, $"//kodikplayer.com/{v.path}/720p", 0);
                mtpl.Append(v.name, links.url, "call", links.stream);
            }

            return ContentTpl(mtpl);
        }

        var vtpl = new VoiceTpl(voices.Count);
        foreach (var v in voices)
            vtpl.Append(v.name, v == selected, $"{host}/lite/animefox?rjson={rjson}&title={enc_title}&original_title={enc_original_title}&year={year}&href={HttpUtility.UrlEncode(href)}&t={HttpUtility.UrlEncode(v.path)}");

        var etpl = new EpisodeTpl(vtpl, page.episodes.Count);
        foreach (var ep in page.episodes)
        {
            var links = KodikUrls(enc_title, enc_original_title, ep.link, ep.number);
            etpl.Append(ep.name, title ?? original_title, page.season, ep.number.ToString(), links.url, "call", streamlink: links.stream, voice_name: selected.name, vast: init.vast);
        }

        return ContentTpl(etpl);
        #endregion
    }

    #region Helpers
    (string url, string stream) KodikUrls(string enc_title, string enc_original_title, string link, int episode)
    {
        string args = $"?title={enc_title}&original_title={enc_original_title}&link={HttpUtility.UrlEncode(link)}" + (episode > 0 ? $"&episode={episode}" : string.Empty);
        return ($"{host}/lite/kodik/video{args}", accsArgs($"{host}/lite/kodik/video.m3u8{args}&play=true"));
    }

    static IEnumerable<string> TitleParts(string title)
        => title.Split(" / ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Текст select внутри блока div.name (те же классы встречаются раньше, в CSS страницы).</summary>
    static string Select(string html, string name)
    {
        int start = html.IndexOf($"<div class=\"{name}\">", StringComparison.Ordinal);
        if (start < 0)
            return string.Empty;

        int end = html.IndexOf("</select>", start, StringComparison.Ordinal);
        return end < 0 ? html.Substring(start) : html.Substring(start, end - start);
    }

    static KodikPage ParseKodik(string html)
    {
        var voices = new List<Voice>();
        string translations = Select(html, "serial-translations-box");
        if (translations.Length == 0)
            translations = Select(html, "movie-translations-box");

        foreach (Match m in rxTranslation.Matches(translations))
        {
            string name = WebUtility.HtmlDecode(m.Groups[5].Value).Trim();
            if (m.Groups[1].Value == "subtitles")
                name += " (субтитры)";

            string path = $"{m.Groups[4].Value}/{m.Groups[2].Value}/{m.Groups[3].Value}";
            if (!voices.Any(v => v.path == path))
                voices.Add(new Voice(name, path));
        }

        var episodes = new List<Episode>();
        foreach (Match m in rxSerie.Matches(Select(html, "serial-series-box")))
        {
            if (int.TryParse(m.Groups[1].Value, out int number))
                episodes.Add(new Episode(number, WebUtility.HtmlDecode(m.Groups[4].Value).Trim(), $"//kodikplayer.com/seria/{m.Groups[2].Value}/{m.Groups[3].Value}/720p"));
        }

        var season = rxSeason.Match(Select(html, "serial-seasons-box"));

        return new KodikPage(
            season.Success ? season.Groups[1].Value : "1",
            WebUtility.HtmlDecode(rxCurrent.Match(html).Groups[1].Value).Trim(),
            voices,
            episodes.OrderBy(e => e.number).ToList()
        );
    }
    #endregion
}
