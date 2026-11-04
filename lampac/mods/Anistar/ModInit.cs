using Microsoft.AspNetCore.Http;
using Shared;
using Shared.Models.Base;
using Shared.Models.Events;
using Shared.Models.Module;
using Shared.Models.Module.Interfaces;
using Shared.Models.Online.Settings;
using Shared.Services;
using System.Collections.Generic;

namespace Anistar;

public class ModInit : IModuleLoaded, IModuleOnline
{
    public static OnlinesSettings conf;

    public List<ModuleOnlineItem> Invoke(HttpContext httpContext, RequestModel requestInfo, string host, OnlineEventsModel args)
    {
        if (!args.isanime)
            return null;

        return new List<ModuleOnlineItem>()
        {
            new(conf, "anistar", "AniStar")
        };
    }

    public void Loaded(InitspaceModel baseconf)
    {
        CoreInit.conf.online.with_search.Add("anistar");

        updateConf();
        EventListener.UpdateInitFile += updateConf;
        EventListener.OnlineApiQuality += onlineApiQuality;
    }

    public void Dispose()
    {
        EventListener.UpdateInitFile -= updateConf;
        EventListener.OnlineApiQuality -= onlineApiQuality;
    }

    void updateConf()
    {
        // Поток на *.an-media.org отдаётся только с Referer anistar.org,
        // поэтому streamproxy включён: Lampac проксирует m3u8 и сегменты с нужными заголовками.
        conf = ModuleInvoke.Init("Anistar", new OnlinesSettings("Anistar", "https://anistar.org", streamproxy: true)
        {
            displayindex = 165,
            headers_stream = HeadersModel.Init(
                ("origin", "https://anistar.org"),
                ("referer", "https://anistar.org/")
            ).ToDictionary()
        });
    }

    string onlineApiQuality(EventOnlineApiQuality e)
    {
        return e.balanser == "anistar" ? " ~ 720p" : null;
    }
}
