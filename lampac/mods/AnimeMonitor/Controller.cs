using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace AnimeMonitor;

public class AnimeMonitorController : Controller
{
    /// <summary>JSON для Home Assistant (REST sensor) и других систем мониторинга</summary>
    [HttpGet]
    [Route("animemon/status")]
    async public Task<ActionResult> Status(bool refresh = false)
    {
        if (refresh || SourceMonitor.LastRun == null)
            await SourceMonitor.RunOnce();

        var items = SourceMonitor.Snapshot();

        string json = JsonConvert.SerializeObject(new
        {
            updated = SourceMonitor.LastRun,
            total = items.Count,
            up = items.Count(i => i.state == "up"),
            down = items.Where(i => i.state != "up").Select(i => i.name).ToArray(),
            sources = items.ToDictionary(i => i.name, i => i)
        }, Formatting.Indented);

        return Content(json, "application/json; charset=utf-8");
    }

    /// <summary>Простая страница со статусом источников</summary>
    [HttpGet]
    [Route("animemon")]
    async public Task<ActionResult> Index(bool refresh = false)
    {
        if (refresh || SourceMonitor.LastRun == null)
            await SourceMonitor.RunOnce();

        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>Источники аниме</title><style>");
        sb.Append("body{font-family:system-ui,sans-serif;margin:16px;background:#111;color:#eee}table{border-collapse:collapse;width:100%;max-width:900px}");
        sb.Append("td,th{padding:6px 10px;border-bottom:1px solid #333;text-align:left;font-size:14px}.up{color:#4caf50}.down{color:#f44336}.challenge{color:#ff9800}.unknown{color:#999}a{color:#8ab4f8}");
        sb.Append("</style></head><body><h2>Доступность источников</h2>");
        sb.Append($"<p>Последняя проверка: {(SourceMonitor.LastRun?.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") ?? "—")} · <a href=\"?refresh=true\">проверить сейчас</a> · <a href=\"animemon/status\">JSON</a></p>");
        sb.Append("<table><tr><th>Источник</th><th>Состояние</th><th>Код</th><th>Время, мс</th><th>С момента</th><th>Ошибка</th></tr>");

        foreach (var s in SourceMonitor.Snapshot())
        {
            sb.Append("<tr>");
            sb.Append($"<td><a href=\"{WebUtility.HtmlEncode(s.url)}\" target=\"_blank\" rel=\"noreferrer\">{WebUtility.HtmlEncode(s.name)}</a></td>");
            sb.Append($"<td class=\"{s.state}\">{s.state}</td>");
            sb.Append($"<td>{(s.code > 0 ? s.code.ToString() : "—")}</td>");
            sb.Append($"<td>{s.ms}</td>");
            sb.Append($"<td>{(s.since?.ToLocalTime().ToString("dd.MM HH:mm") ?? "—")}</td>");
            sb.Append($"<td>{WebUtility.HtmlEncode(s.error ?? string.Empty)}</td>");
            sb.Append("</tr>");
        }

        sb.Append("</table></body></html>");
        return Content(sb.ToString(), "text/html; charset=utf-8");
    }
}
