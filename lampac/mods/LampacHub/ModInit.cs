using Shared.Models.Events;
using Shared.Models.Module;
using Shared.Models.Module.Interfaces;
using Shared.Services;
using System;

namespace LampacHub;

public class HubConf
{
    /// <summary>Модуль выключен: /hub, /hub.js и API отвечают 404, база не трогается</summary>
    public bool enable { get; set; } = true;

    /// <summary>Сколько минут живёт код входа устройства</summary>
    public int code_ttl { get; set; } = 10;

    /// <summary>Имя аккаунта по умолчанию, создаётся при первом запуске</summary>
    public string account_name { get; set; } = "Основной";

    /// <summary>hub.js сам подгружает bookmark.js и timecode.js, если их ещё нет в Lampa</summary>
    public bool load_sync { get; set; } = true;
}

public class ModInit : IModuleLoaded
{
    public static HubConf conf = new HubConf();
    public static string modpath;

    public void Loaded(InitspaceModel baseconf)
    {
        modpath = baseconf.path;
        updateConf();
        EventListener.UpdateInitFile += updateConf;

        try
        {
            HubStore.Init(conf.account_name);
        }
        catch (Exception ex)
        {
            Console.WriteLine("LampacHub: не удалось открыть database/LampacHub.sql: " + ex.Message);
        }
    }

    public void Dispose()
    {
        EventListener.UpdateInitFile -= updateConf;
    }

    void updateConf()
    {
        var c = ModuleInvoke.Init("LampacHub", new HubConf());

        if (c.code_ttl < 1)
            c.code_ttl = 1;

        if (c.code_ttl > 60)
            c.code_ttl = 60;

        if (string.IsNullOrWhiteSpace(c.account_name))
            c.account_name = "Основной";

        conf = c;
    }
}
