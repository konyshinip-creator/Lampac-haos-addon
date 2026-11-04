using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeMonitor;

public class TargetStatus
{
    public string name { get; set; }

    public string url { get; set; }

    /// <summary>up | down | challenge | unknown</summary>
    public string state { get; set; } = "unknown";

    public int code { get; set; }

    public long ms { get; set; }

    public string error { get; set; }

    public DateTime? @checked { get; set; }

    public DateTime? last_up { get; set; }

    /// <summary>Когда состояние сменилось последний раз</summary>
    public DateTime? since { get; set; }

    public int fails { get; set; }
}

public static class SourceMonitor
{
    static readonly ConcurrentDictionary<string, TargetStatus> statuses = new(StringComparer.OrdinalIgnoreCase);

    static CancellationTokenSource cts;

    static readonly HttpClient webhookClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static DateTime? LastRun { get; private set; }

    public static IReadOnlyList<TargetStatus> Snapshot()
    {
        var conf = ModInit.conf;
        if (conf?.targets == null)
            return statuses.Values.ToList();

        return conf.targets
            .Where(t => !string.IsNullOrWhiteSpace(t?.name))
            .Select(t => statuses.TryGetValue(t.name, out var s) ? s : new TargetStatus { name = t.name, url = t.url })
            .ToList();
    }

    public static void Start()
    {
        Stop();
        cts = new CancellationTokenSource();
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            // небольшая пауза, чтобы не мешать старту сервера
            try { await Task.Delay(TimeSpan.FromSeconds(15), token); } catch { return; }

            while (!token.IsCancellationRequested)
            {
                var conf = ModInit.conf;

                try
                {
                    if (conf?.enable == true)
                        await RunOnce(token);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "AnimeMonitor loop");
                }

                try { await Task.Delay(TimeSpan.FromMinutes(conf?.interval > 0 ? conf.interval : 10), token); }
                catch { break; }
            }
        }, token);
    }

    public static void Stop()
    {
        try { cts?.Cancel(); } catch { }
        cts = null;
    }

    public static async Task RunOnce(CancellationToken token = default)
    {
        var conf = ModInit.conf;
        if (conf?.targets == null)
            return;

        var tasks = conf.targets
            .Where(t => !string.IsNullOrWhiteSpace(t?.name) && !string.IsNullOrWhiteSpace(t.url))
            .Select(t => Check(conf, t, token));

        await Task.WhenAll(tasks);
        LastRun = DateTime.UtcNow;
    }

    static async Task Check(MonitorConf conf, MonitorTarget target, CancellationToken token)
    {
        var result = new TargetStatus { name = target.name, url = target.url, @checked = DateTime.UtcNow };
        var sw = Stopwatch.StartNew();

        try
        {
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5,
                AutomaticDecompression = DecompressionMethods.All,
                UseCookies = false
            };

            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(conf.timeout) };
            using var req = new HttpRequestMessage(HttpMethod.Get, target.url);
            req.Headers.TryAddWithoutValidation("User-Agent", conf.useragent);
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.8");
            req.Headers.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9,en;q=0.8");

            using var res = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, token);
            string body = await res.Content.ReadAsStringAsync(token);
            if (body.Length > 200_000)
                body = body.Substring(0, 200_000);

            result.code = (int)res.StatusCode;

            if (IsChallenge(res, body))
            {
                result.state = "challenge";
                result.error = "JS/anti-bot challenge";
            }
            else if (res.IsSuccessStatusCode)
            {
                if (!string.IsNullOrEmpty(target.marker) && !body.Contains(target.marker, StringComparison.OrdinalIgnoreCase))
                {
                    // вместо ожидаемого ответа пришла HTML-страница — обычно это заглушка защиты
                    bool html = res.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true;
                    result.state = html ? "challenge" : "down";
                    result.error = html ? "HTML вместо ожидаемого ответа (защита/заглушка?)" : "marker not found";
                }
                else
                {
                    result.state = "up";
                }
            }
            else if (result.code == 403 || result.code == 429)
            {
                result.state = "challenge";
                result.error = $"HTTP {result.code} (доступ ограничен)";
            }
            else
            {
                result.state = "down";
                result.error = $"HTTP {result.code}";
            }
        }
        catch (TaskCanceledException) when (!token.IsCancellationRequested)
        {
            result.state = "down";
            result.error = "timeout";
        }
        catch (Exception ex)
        {
            result.state = "down";
            result.error = (ex.InnerException ?? ex).Message;
        }

        result.ms = sw.ElapsedMilliseconds;

        statuses.TryGetValue(target.name, out var prev);

        result.last_up = result.state == "up" ? result.@checked : prev?.last_up;
        result.fails = result.state == "up" ? 0 : (prev?.fails ?? 0) + 1;
        result.since = prev == null || prev.state != result.state ? result.@checked : prev.since;

        statuses[target.name] = result;

        if (prev != null && prev.state != result.state && !string.IsNullOrWhiteSpace(conf.webhook))
            await SendWebhook(conf.webhook, prev.state, result);
    }

    static bool IsChallenge(HttpResponseMessage res, string body)
    {
        if (res.Headers.TryGetValues("cf-mitigated", out var mitigated) && mitigated.Any(v => v.Contains("challenge", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (string.IsNullOrEmpty(body))
            return false;

        return body.Contains("cf-chl-", StringComparison.Ordinal)
            || body.Contains("<title>Just a moment...</title>", StringComparison.OrdinalIgnoreCase)
            || body.Contains("challenges.cloudflare.com/turnstile", StringComparison.Ordinal)
            || (body.Length < 20_000 && body.Contains("ddos-guard", StringComparison.OrdinalIgnoreCase) && body.Contains("js-challenge", StringComparison.OrdinalIgnoreCase));
    }

    static async Task SendWebhook(string url, string previous, TargetStatus status)
    {
        try
        {
            string json = JsonConvert.SerializeObject(new
            {
                source = status.name,
                status.url,
                previous,
                status.state,
                status.code,
                status.error,
                status.@checked
            });

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            await webhookClient.PostAsync(url, content);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning("AnimeMonitor webhook failed: {Message}", ex.Message);
        }
    }
}
