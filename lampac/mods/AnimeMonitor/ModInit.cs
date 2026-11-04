using Shared.Models.Events;
using Shared.Models.Module;
using Shared.Models.Module.Interfaces;
using Shared.Services;
using System.Collections.Generic;

namespace AnimeMonitor;

public class MonitorTarget
{
    /// <summary>Имя в отчёте (совпадает с именем модуля-источника, если он есть)</summary>
    public string name { get; set; }

    /// <summary>Адрес, который проверяется GET-запросом</summary>
    public string url { get; set; }

    /// <summary>Текст, который должен быть в ответе, чтобы считать сайт рабочим (необязательно)</summary>
    public string marker { get; set; }
}

public class MonitorConf
{
    public bool enable { get; set; } = true;

    /// <summary>Интервал проверок, минуты</summary>
    public int interval { get; set; } = 10;

    /// <summary>Таймаут одного запроса, секунды</summary>
    public int timeout { get; set; } = 15;

    /// <summary>URL, на который при смене состояния источника отправляется POST с JSON (например, webhook Home Assistant)</summary>
    public string webhook { get; set; }

    public string useragent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    public List<MonitorTarget> targets { get; set; }
}

public class ModInit : IModuleLoaded
{
    public static MonitorConf conf;

    /// <summary>
    /// Лёгкие проверки: JSON-поиск senku и robots.txt у DLE-сайтов (маркер отличает настоящий ответ
    /// от заглушки Cloudflare/DDoS-Guard), плюс плееры, через которые идут потоки.
    /// </summary>
    public static readonly List<MonitorTarget> DefaultTargets = new()
    {
        new() { name = "Senku",    url = "https://senku.one/senku-api/api/v2/search?q=naruto", marker = "\"ok\":true" },
        new() { name = "Anistar",  url = "https://anistar.org/robots.txt",   marker = "User-agent" },
        new() { name = "Animaunt", url = "https://animaunt.org/robots.txt",  marker = "User-agent" },
        new() { name = "Hentasis", url = "https://hentasis1.top/robots.txt", marker = "User-agent" },
        new() { name = "OpenNI (плеер AniMaunt)", url = "https://play.openni.ru/?cid=2398", marker = "Playerjs" },
        new() { name = "Kodik",    url = "https://kodikplayer.com/" }
    };

    public void Loaded(InitspaceModel baseconf)
    {
        updateConf();
        EventListener.UpdateInitFile += updateConf;
        SourceMonitor.Start();
    }

    public void Dispose()
    {
        EventListener.UpdateInitFile -= updateConf;
        SourceMonitor.Stop();
    }

    void updateConf()
    {
        var c = ModuleInvoke.Init("AnimeMonitor", new MonitorConf());
        if (c.targets == null || c.targets.Count == 0)
            c.targets = DefaultTargets;

        if (c.interval < 1)
            c.interval = 1;

        if (c.timeout < 3)
            c.timeout = 3;

        conf = c;
    }
}
