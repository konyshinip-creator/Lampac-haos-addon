using Microsoft.AspNetCore.Http;
using Shared;
using Shared.Models.Base;
using Shared.Models.Events;
using Shared.Models.Module;
using Shared.Models.Module.Interfaces;
using Shared.Models.Online.Settings;
using Shared.Services;
using System.Collections.Generic;

namespace AniTokyo;

public class ModInit : IModuleLoaded, IModuleOnline
{
    public static OnlinesSettings conf;

    public List<ModuleOnlineItem> Invoke(HttpContext httpContext, RequestModel requestInfo, string host, OnlineEventsModel args)
    {
        if (!args.isanime)
            return null;

        return new List<ModuleOnlineItem>()
        {
            new(conf, "anitokyo", "AniTokyo")
        };
    }

    public void Loaded(InitspaceModel baseconf)
    {
        CoreInit.conf.online.with_search.Add("anitokyo");

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
        conf = ModuleInvoke.Init("AniTokyo", new OnlinesSettings("AniTokyo", "https://anitokyo.tv")
        {
            displayindex = 176
        });
    }

    string onlineApiQuality(EventOnlineApiQuality e)
    {
        return e.balanser == "anitokyo" ? " ~ 720p" : null;
    }
}
