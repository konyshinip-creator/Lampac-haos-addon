using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Shared;
using Shared.Attributes;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace LampacHub;

/// <summary>
/// Плагин hub.js и API устройства. Вход по коду: устройство берёт код (/hub/device/code), показывает
/// его на экране и опрашивает /hub/device/poll, пока код не подтвердят в админке /hub.
/// Токен устройства ходит в параметре hub_token — не token, потому что token Lampac считает
/// идентичностью пользователя.
/// </summary>
public class HubDeviceController : BaseController
{
    bool Off => !ModInit.conf.enable || !HubStore.Ready;

    ActionResult Reply(object value, int status = 200)
    {
        Response.StatusCode = status;
        Response.Headers["Cache-Control"] = "no-store";
        return Content(JsonConvert.SerializeObject(value), "application/json; charset=utf-8");
    }

    [HttpGet, AllowAnonymous]
    [Route("hub.js")]
    public ActionResult Plugin()
    {
        if (Off)
            return NotFound();

        string js = System.IO.File.ReadAllText(Path.Combine(ModInit.modpath, "hub.js"), Encoding.UTF8)
            .Replace("{localhost}", host)
            .Replace("{load_sync}", ModInit.conf.load_sync ? "true" : "false");

        Response.Headers["Cache-Control"] = "no-cache";
        return Content(js, "application/javascript; charset=utf-8");
    }

    [HttpPost, AllowAnonymous]
    [Route("hub/device/code")]
    public ActionResult Code(string name)
    {
        if (Off)
            return NotFound();

        var p = DeviceCodes.Create(name, requestInfo.IP, requestInfo.UserAgent, ModInit.conf.code_ttl);
        if (p == null)
            return Reply(new { error = "Слишком много незавершённых входов, попробуйте позже" }, 429);

        return Reply(new
        {
            code = p.code,
            key = p.key,
            ttl = (int)(p.expires - DateTime.UtcNow).TotalSeconds,
            url = host + "/hub"
        });
    }

    [HttpGet, AllowAnonymous]
    [Route("hub/device/poll")]
    public ActionResult Poll(string key)
    {
        if (Off)
            return NotFound();

        var p = DeviceCodes.FindByKey(key);
        if (p == null)
            return Reply(new { status = "expired" }, 404);

        if (p.token == null)
            return Reply(new { status = "pending", ttl = (int)(p.expires - DateTime.UtcNow).TotalSeconds });

        // Токен отдаётся один раз
        DeviceCodes.Remove(p.code);
        return Me(p.token);
    }

    [HttpGet, AllowAnonymous]
    [Route("hub/device/me")]
    public ActionResult Me(string hub_token)
    {
        if (Off)
            return NotFound();

        var device = HubStore.FindDevice(hub_token);
        if (device == null)
            return Reply(new { status = "revoked" }, 401);

        var account = HubStore.GetAccount(device.account_id);
        if (account == null)
            return Reply(new { status = "revoked" }, 401);

        HubStore.TouchDevice(device.id, requestInfo.IP, requestInfo.UserAgent);

        var profiles = HubStore.ListProfiles(account.id);

        return Reply(new
        {
            status = "ok",
            token = hub_token,
            uid = HubStore.EffectiveUid(account),
            account = new { id = account.id, name = account.name },
            device = new { id = device.id, name = device.name },
            profile_id = device.profile_id,
            profiles = profiles.Select(i => new { i.id, i.name, i.avatar, i.child })
        });
    }

    [HttpPost, AllowAnonymous]
    [Route("hub/device/profile")]
    public ActionResult DeviceProfile(string hub_token, string profile_id)
    {
        if (Off)
            return NotFound();

        var device = HubStore.FindDevice(hub_token);
        if (device == null)
            return Reply(new { status = "revoked" }, 401);

        var profile = HubStore.GetProfile(profile_id);
        if (profile == null || profile.account_id != device.account_id)
            return Reply(new { error = "Профиль не найден" }, 404);

        HubStore.SetDeviceProfile(device.id, profile.id);
        return Reply(new { status = "ok" });
    }
}

/// <summary>
/// Админка /hub. Пароль — root-пароль Lampac (файл passwd), та же cookie accspasswd, что у
/// /adminpanel: проверяет её middleware Lampac по атрибуту Authorization, включая блокировку
/// после 10 неверных паролей с одного IP за день.
/// </summary>
public class HubAdminController : BaseController
{
    bool Off => !ModInit.conf.enable || !HubStore.Ready;

    ActionResult Reply(object value, int status = 200)
    {
        Response.StatusCode = status;
        Response.Headers["Cache-Control"] = "no-store";
        return Content(JsonConvert.SerializeObject(value), "application/json; charset=utf-8");
    }

    ActionResult Page(string file)
    {
        string html = System.IO.File.ReadAllText(Path.Combine(ModInit.modpath, file), Encoding.UTF8);
        Response.Headers["Cache-Control"] = "no-store";
        return Content(html, "text/html; charset=utf-8");
    }

    /// <summary>Изменяющие запросы только со своей страницы: чужой сайт не сможет отозвать устройство руками браузера админа</summary>
    bool CrossSite()
    {
        string origin = Request.Headers["Origin"];
        if (string.IsNullOrEmpty(origin))
            return Request.Headers["X-Hub"] != "1";

        return !Uri.TryCreate(origin, UriKind.Absolute, out var u)
            || !string.Equals(u.Authority, Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    [HttpGet, AllowAnonymous]
    [Route("hub/auth")]
    public ActionResult Auth()
        => Off ? NotFound() : Page("auth.html");

    [HttpGet]
    [Authorization(redirectUri: "/hub/auth")]
    [Route("hub")]
    public ActionResult Index()
        => Off ? NotFound() : Page("admin.html");

    [HttpGet]
    [Authorization(accessDeniedMessage: "unauthorized")]
    [Route("hub/api/state")]
    public ActionResult State()
    {
        if (Off)
            return NotFound();

        var account = HubStore.GetAccount(HubStore.MainAccount);
        var profiles = HubStore.ListProfiles(HubStore.MainAccount);
        var now = DateTime.UtcNow;

        return Reply(new
        {
            account = new { account.id, account.name },
            host,
            code_ttl = ModInit.conf.code_ttl,
            profiles,
            devices = HubStore.ListDevices(),
            pending = DeviceCodes.Pending().Select(p => new
            {
                p.code,
                p.name,
                p.ip,
                p.user_agent,
                ttl = (int)(p.expires - now).TotalSeconds
            })
        });
    }

    [HttpPost]
    [Authorization(accessDeniedMessage: "unauthorized")]
    [Route("hub/api/code/confirm")]
    public ActionResult Confirm(string code, string profile_id, string name)
    {
        if (Off)
            return NotFound();

        if (CrossSite())
            return Reply(new { error = "forbidden" }, 403);

        var p = DeviceCodes.FindByCode(code);
        if (p == null)
            return Reply(new { error = "Код не найден или истёк" }, 404);

        if (p.token != null)
            return Reply(new { error = "Код уже подтверждён" }, 409);

        var profile = HubStore.GetProfile(profile_id) ?? HubStore.GetProfile(HubStore.MainProfile);
        if (profile == null || profile.account_id != HubStore.MainAccount)
            return Reply(new { error = "Профиль не найден" }, 404);

        string deviceName = string.IsNullOrWhiteSpace(name) ? p.name : name;
        p.token = HubStore.CreateDevice(HubStore.MainAccount, profile.id, deviceName, p.ip, p.user_agent, out var device);
        p.device_id = device.id;

        return Reply(new { status = "ok", device });
    }

    [HttpPost]
    [Authorization(accessDeniedMessage: "unauthorized")]
    [Route("hub/api/profile/save")]
    public ActionResult SaveProfile(string id, string name, string avatar, bool child)
    {
        if (Off)
            return NotFound();

        if (CrossSite())
            return Reply(new { error = "forbidden" }, 403);

        name = HubStore.Trim(name, 40);
        avatar = HubStore.Trim(avatar, 300);

        if (string.IsNullOrEmpty(name))
            return Reply(new { error = "Укажите имя профиля" }, 400);

        if (string.IsNullOrEmpty(id))
            return Reply(new { status = "ok", profile = HubStore.CreateProfile(HubStore.MainAccount, name, avatar, child) });

        var profile = HubStore.GetProfile(id);
        if (profile == null || profile.account_id != HubStore.MainAccount)
            return Reply(new { error = "Профиль не найден" }, 404);

        HubStore.UpdateProfile(id, name, avatar, child);
        return Reply(new { status = "ok", profile = HubStore.GetProfile(id) });
    }

    [HttpPost]
    [Authorization(accessDeniedMessage: "unauthorized")]
    [Route("hub/api/profile/delete")]
    public ActionResult DeleteProfile(string id)
    {
        if (Off)
            return NotFound();

        if (CrossSite())
            return Reply(new { error = "forbidden" }, 403);

        if (id == HubStore.MainProfile)
            return Reply(new { error = "Основной профиль удалить нельзя" }, 400);

        return HubStore.DeleteProfile(id)
            ? Reply(new { status = "ok" })
            : Reply(new { error = "Профиль не найден" }, 404);
    }

    [HttpPost]
    [Authorization(accessDeniedMessage: "unauthorized")]
    [Route("hub/api/device/rename")]
    public ActionResult RenameDevice(string id, string name)
    {
        if (Off)
            return NotFound();

        if (CrossSite())
            return Reply(new { error = "forbidden" }, 403);

        return HubStore.RenameDevice(id, name)
            ? Reply(new { status = "ok" })
            : Reply(new { error = "Устройство не найдено" }, 404);
    }

    [HttpPost]
    [Authorization(accessDeniedMessage: "unauthorized")]
    [Route("hub/api/device/revoke")]
    public ActionResult RevokeDevice(string id)
    {
        if (Off)
            return NotFound();

        if (CrossSite())
            return Reply(new { error = "forbidden" }, 403);

        return HubStore.DeleteDevice(id)
            ? Reply(new { status = "ok" })
            : Reply(new { error = "Устройство не найдено" }, 404);
    }
}
