using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LampacHub;

/// <summary>
/// Сведения о тайтле из TMDB для админки: сезоны, серии, статус и дата следующей серии.
/// Кешируются в database/LampacHub.sql (title_info): выходящие сериалы — на 12 часов
/// (и сразу после даты новой серии), завершённые и вышедшие фильмы — на 30 дней.
/// </summary>
public static class TitleInfo
{
    const long Hour = 3600;

    public static async Task<(Dictionary<string, JObject> info, bool failed)> Get(List<string> keys, string host)
    {
        var result = new Dictionary<string, JObject>();
        var cached = HubStore.InfoGet(keys);
        var stale = new List<string>();

        foreach (string key in keys)
        {
            if (cached.TryGetValue(key, out var c))
            {
                result[key] = c.data;
                if (!Fresh(c.data, c.updated))
                    stale.Add(key);
            }
            else
            {
                stale.Add(key);
            }
        }

        if (stale.Count == 0)
            return (result, false);

        Tmdb.Prepare(host);
        bool failed = false;
        using var gate = new SemaphoreSlim(4);

        await Task.WhenAll(stale.Select(async key =>
        {
            await gate.WaitAsync();
            try
            {
                if (failed)
                    return;

                var info = await Load(key);
                if (info == null)
                    return;

                HubStore.InfoSet(key, info);
                lock (result)
                    result[key] = info;
            }
            catch (HttpRequestException)
            {
                failed = true;
            }
            catch { }
            finally
            {
                gate.Release();
            }
        }));

        return (result, failed);
    }

    static bool Fresh(JObject d, long updated)
    {
        long age = HubStore.Now() - updated;
        string status = (string)d["status"];

        if (status == "ended" || status == "canceled" || status == "released")
            return age < 30 * 24 * Hour;

        string next = (string)d["next"]?["date"];
        if (!string.IsNullOrEmpty(next) && DateTime.TryParse(next, out var date) && date.Date < DateTime.UtcNow.Date)
            return false;

        return age < 12 * Hour;
    }

    /// <summary>null — в TMDB такого нет; HttpRequestException — TMDB недоступен</summary>
    static async Task<JObject> Load(string key)
    {
        int i = key.IndexOf(':');
        string kind = key.Substring(0, i), id = key.Substring(i + 1);

        var d = await Tmdb.Get(kind + "/" + id);
        if (d == null)
            return null;

        string raw = (string)d["status"] ?? string.Empty;
        var info = new JObject { ["kind"] = kind, ["status_raw"] = raw };

        if (kind == "tv")
        {
            info["status"] = raw switch
            {
                "Ended" => "ended",
                "Canceled" => "canceled",
                "Returning Series" => "airing",
                _ => "planned"
            };

            // Спецвыпуски (сезон 0) не считаем
            var seasons = (d["seasons"] as JArray)?.OfType<JObject>().Where(s => (int?)s["season_number"] > 0).ToList();
            info["seasons"] = seasons?.Count(s => (int?)s["episode_count"] > 0) ?? (int?)d["number_of_seasons"] ?? 0;
            info["episodes"] = seasons?.Sum(s => (int?)s["episode_count"] ?? 0) ?? (int?)d["number_of_episodes"] ?? 0;
            info["first_air"] = (string)d["first_air_date"] ?? string.Empty;
            info["last_air"] = (string)d["last_air_date"] ?? string.Empty;

            if (d["last_episode_to_air"] is JObject last)
                info["last"] = Episode(last);
            if (d["next_episode_to_air"] is JObject next)
                info["next"] = Episode(next);
        }
        else
        {
            info["status"] = raw switch
            {
                "Released" => "released",
                "Canceled" => "canceled",
                _ => "planned"
            };
            info["release"] = (string)d["release_date"] ?? string.Empty;
            info["runtime"] = (int?)d["runtime"] ?? 0;
        }

        return info;
    }

    static JObject Episode(JObject e) => new JObject
    {
        ["season"] = (int?)e["season_number"] ?? 0,
        ["episode"] = (int?)e["episode_number"] ?? 0,
        ["date"] = (string)e["air_date"] ?? string.Empty
    };
}
