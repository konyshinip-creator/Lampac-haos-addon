using Microsoft.AspNetCore.Mvc;
using Shared;
using Shared.Attributes;
using Shared.Models.SISI.Base;
using Shared.Models.SISI.OnResult;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace Hentasis;

/// <summary>
/// hentasis1.top (DLE). Видео — Playerjs прямо на странице тайтла, прямые mp4 без заголовков.
/// Номер серии и дорожка берутся из имени файла: Name_1_rus_(...).mp4 / Name_1_sub_(...).mp4.
/// </summary>
public class HentasisController : BaseSisiController
{
    public HentasisController() : base(ModInit.conf) { }

    static readonly Regex rxSearch = new("<a class=\"sres-wrap clearfix\" href=\"(?:https?://[^/\"]+)?/(\\d+-[^\"]+\\.html)\">[\\s\\S]*?<img src=\"([^\"]+)\"[\\s\\S]*?<h2>([^<]+)</h2>", RegexOptions.Compiled);
    static readonly Regex rxAnchor = new("<a\\s([^>]*?)href=\"(?:https?://[^/\"]+)?/(\\d+-[^\"/]+\\.html)\"([^>]*)>([\\s\\S]{0,2000}?)</a>", RegexOptions.Compiled);
    static readonly Regex rxImg = new("<img\\s[^>]*>", RegexOptions.Compiled);
    static readonly Regex rxSrc = new("\\s(?:data-src|src)=\"([^\"]+)\"", RegexOptions.Compiled);
    static readonly Regex rxAlt = new("\\salt=\"([^\"]*)\"", RegexOptions.Compiled);
    static readonly Regex rxFile = new("\\{file:\"([^\"]*)\",title:\"([^\"]*)\"\\}", RegexOptions.Compiled);
    static readonly Regex rxEpisode = new("_(\\d+)_(rus|sub)_", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [HttpGet, Staticache]
    [Route("hentasis")]
    async public Task<ActionResult> Index(string search, int pg = 1)
    {
        if (await IsRequestBlocked(rch: false))
            return badInitMsg;

        if (pg < 1)
            pg = 1;

        string url = !string.IsNullOrWhiteSpace(search)
            ? $"{init.host}/index.php?do=search&subaction=search&story={HttpUtility.UrlEncode(search)}" + (pg > 1 ? $"&search_start={pg}&full_search=0&result_from={(pg - 1) * 10 + 1}" : string.Empty)
            : pg > 1 ? $"{init.host}/page/{pg}/" : $"{init.host}/";

        var cache = await InvokeCacheResult($"hentasis:list:{search}:{pg}", 30, jsonContext.ListPlaylistItem, async e =>
        {
            string html = await httpHydra.Get(url, safety: true);
            if (string.IsNullOrEmpty(html))
                return e.Fail("html", refresh_proxy: true);

            var playlists = !string.IsNullOrWhiteSpace(search)
                ? ParseSearch(html)
                : ParseListing(html);

            if (playlists.Count == 0)
                return e.Fail("playlists");

            return e.Success(playlists);
        });

        return PlaylistResult(cache);
    }

    [HttpGet, Staticache(manually: true)]
    [Route("hentasis/vidosik")]
    async public Task<ActionResult> Video(string uri)
    {
        if (await IsRequestBlocked(rch: false))
            return badInitMsg;

        if (string.IsNullOrEmpty(uri) || !Regex.IsMatch(uri, "^\\d+-[^/]+\\.html$"))
            return OnError("uri", false);

        var cache = await InvokeCacheResult($"hentasis:view:{uri}", 60, jsonContext.StreamItem, async e =>
        {
            string html = await httpHydra.Get($"{init.host}/{uri}", safety: true);
            if (string.IsNullOrEmpty(html))
                return e.Fail("html", refresh_proxy: true);

            var files = new List<(int episode, bool sub, string url)>();
            foreach (Match m in rxFile.Matches(html))
            {
                string file = m.Groups[1].Value.Replace("\\/", "/");
                if (!file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                    continue;

                var em = rxEpisode.Match(file);
                int episode = em.Success && int.TryParse(em.Groups[1].Value, out int n) ? n : files.Count + 1;
                bool sub = em.Success && em.Groups[2].Value.Equals("sub", StringComparison.OrdinalIgnoreCase);

                // скобки в имени файла — кодируем, чтобы плееры не спотыкались
                file = file.Replace("(", "%28").Replace(")", "%29");

                if (!files.Any(f => f.url == file))
                    files.Add((episode, sub, file));
            }

            if (files.Count == 0)
                return e.Fail("files");

            var qualitys = new Dictionary<string, string>();
            foreach (var f in files.OrderBy(f => f.episode).ThenBy(f => f.sub))
            {
                string label = $"{f.episode} серия{(f.sub ? " (субтитры)" : string.Empty)}";
                qualitys.TryAdd(label, f.url);
            }

            return e.Success(new StreamItem { qualitys = qualitys });
        });

        return OnResult(cache);
    }

    #region Parse
    List<PlaylistItem> ParseSearch(string html)
    {
        var list = new List<PlaylistItem>();

        foreach (Match m in rxSearch.Matches(html))
            Add(list, m.Groups[1].Value, m.Groups[3].Value, m.Groups[2].Value);

        return list;
    }

    static readonly Regex rxShortLink = new("<a\\s[^>]*class=\"short-link[^\"]*\"[^>]*href=\"(?:https?://[^/\"]+)?/(\\d+-[^\"/]+\\.html)\"[^>]*>([^<]*)</a>", RegexOptions.Compiled);
    static readonly Regex rxShortImg = new("<div class=\"short-img\">\\s*(<img\\s[^>]*>)", RegexOptions.Compiled);

    /// <summary>
    /// Главная/страницы каталога (DLE, шаблон short-item):
    /// div.short-item → h3 > a.short-link (ссылка и название) + div.short-img > img (постер).
    /// Записи из рубрики «Новости сайта» (/news/) — не тайтлы, пропускаем.
    /// </summary>
    List<PlaylistItem> ParseListing(string html)
    {
        var list = new List<PlaylistItem>();

        foreach (string item in html.Split("<div class=\"short-item\">").Skip(1))
        {
            if (item.Contains("/news/\"", StringComparison.OrdinalIgnoreCase))
                continue;

            var link = rxShortLink.Match(item);
            if (!link.Success)
                continue;

            string name = link.Groups[2].Value, src = null;

            var img = rxShortImg.Match(item);
            if (img.Success)
            {
                src = rxSrc.Match(img.Groups[1].Value).Groups[1].Value;
                string alt = rxAlt.Match(img.Groups[1].Value).Groups[1].Value;
                if (string.IsNullOrWhiteSpace(name))
                    name = alt;
            }

            Add(list, link.Groups[1].Value, name, src);
        }

        if (list.Count > 0)
            return list;

        // запасной вариант для другой вёрстки: любые ссылки на тайтлы с картинкой внутри
        foreach (Match m in rxAnchor.Matches(html))
        {
            var im = rxImg.Match(m.Groups[4].Value);
            if (!im.Success)
                continue;

            string src = rxSrc.Match(im.Value).Groups[1].Value;
            if (string.IsNullOrEmpty(src))
                continue;

            string name = rxAlt.Match(im.Value).Groups[1].Value;
            if (string.IsNullOrWhiteSpace(name))
                name = Regex.Replace(m.Groups[4].Value, "<[^>]+>", " ").Trim();

            Add(list, m.Groups[2].Value, name, src);
        }

        return list;
    }

    void Add(List<PlaylistItem> list, string href, string name, string img)
    {
        name = WebUtility.HtmlDecode(Regex.Replace(name ?? string.Empty, "\\s+", " ")).Trim();
        if (string.IsNullOrEmpty(href) || string.IsNullOrEmpty(name) || list.Any(i => i.bookmark?.href == href))
            return;

        if (!string.IsNullOrEmpty(img) && img.StartsWith("/"))
            img = init.host + img;

        list.Add(new PlaylistItem()
        {
            name = name,
            video = $"hentasis/vidosik?uri={HttpUtility.UrlEncode(href)}",
            picture = img,
            json = true,
            bookmark = new Bookmark()
            {
                site = "hentasis",
                href = href,
                image = img
            }
        });
    }
    #endregion
}
