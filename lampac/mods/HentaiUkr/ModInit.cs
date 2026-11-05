using Microsoft.AspNetCore.Http;
using Shared.Models.Base;
using Shared.Models.Events;
using Shared.Models.Module;
using Shared.Models.Module.Interfaces;
using Shared.Models.SISI.Base;
using Shared.Services;
using System.Collections.Generic;

namespace HentaiUkr;

/// <summary>
/// 18+ источник для раздела SISI. Выключен по умолчанию: включается в init.conf
/// { "HentaiUkr": { "enable": true } }
/// </summary>
public class ModInit : IModuleLoaded, IModuleSisi
{
    public static SisiSettings conf;

    public List<SisiModuleItem> Invoke(HttpContext httpContext, RequestModel requestInfo, string host, SisiEventsModel args)
    {
        return new List<SisiModuleItem>()
        {
            new("HentaiUkr", conf, "hentaiukr")
        };
    }

    public void Loaded(InitspaceModel baseconf)
    {
        updateConf();
        EventListener.UpdateInitFile += updateConf;
    }

    public void Dispose()
    {
        EventListener.UpdateInitFile -= updateConf;
    }

    void updateConf()
    {
        conf = ModuleInvoke.Init("HentaiUkr", new SisiSettings("HentaiUkr", "https://hentaiukr.com", enable: false)
        {
            displayindex = 51
        });
    }
}
