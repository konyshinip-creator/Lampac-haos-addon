using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using Shared.Attributes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

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

    /// <summary>Список серверов для меню выбора в Lampa (только имена и адреса)</summary>
    [HttpGet, AllowAnonymous]
    [Route("hub/servers")]
    public ActionResult Servers()
    {
        if (Off)
            return NotFound();

        return Reply(new { servers = HubStore.GetServers() });
    }

    /// <summary>Админка: сохранить список серверов. data — JSON [{name, url}]</summary>
    [HttpPost]
    [Authorization(accessDeniedMessage: "unauthorized")]
    [Route("hub/api/servers")]
    public ActionResult ServersSave(string data)
    {
        if (Off)
            return NotFound();

        if (CrossSite())
            return Reply(new { error = "forbidden" }, 403);

        JArray input;
        try { input = JArray.Parse(data ?? "[]"); }
        catch { return Reply(new { error = "Неверный список" }, 400); }

        var list = new JArray();
        foreach (var s in input.OfType<JObject>().Take(30))
        {
            string url = HubStore.Trim((string)s["url"], 300).TrimEnd('/');
            if (url.Length > 0 && !Regex.IsMatch(url, "^https?://", RegexOptions.IgnoreCase))
                url = "http://" + url;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                return Reply(new { error = "Неверный адрес: " + (string)s["url"] }, 400);

            string name = HubStore.Trim((string)s["name"], 60);
            list.Add(new JObject { ["name"] = name.Length > 0 ? name : uri.Authority, ["url"] = url });
        }

        HubStore.SetServers(list);
        return Reply(new { status = "ok", servers = list });
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
            pin = HubStore.HasPin,
            profiles = profiles.Select(i => new { i.id, i.name, i.avatar, i.child, i.age_limit })
        });
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int count, DateTime since)> _pinTries = new();

    /// <summary>Проверка PIN перед выходом из детского профиля. Не больше 5 попыток за 10 минут на устройство.</summary>
    [HttpPost, AllowAnonymous]
    [Route("hub/device/pin")]
    public ActionResult CheckPin(string hub_token, string pin)
    {
        if (Off)
            return NotFound();

        var device = HubStore.FindDevice(hub_token);
        if (device == null)
            return Reply(new { status = "revoked" }, 401);

        var now = DateTime.UtcNow;
        var tries = _pinTries.TryGetValue(device.id, out var t) && now - t.since < TimeSpan.FromMinutes(10) ? t : (0, now);
        if (tries.Item1 >= 5)
            return Reply(new { ok = false, error = "Слишком много попыток, подождите 10 минут" }, 429);

        if (HubStore.CheckPin(pin))
        {
            _pinTries.TryRemove(device.id, out _);
            return Reply(new { ok = true });
        }

        _pinTries[device.id] = (tries.Item1 + 1, tries.Item2);
        return Reply(new { ok = false, error = "Неверный PIN" }, 403);
    }

    /// <summary>Готовые аватары из папки модуля avatars/</summary>
    [HttpGet, AllowAnonymous]
    [Route("hub/avatars/{name}")]
    public ActionResult Preset(string name)
    {
        if (Off || string.IsNullOrEmpty(name) || !Regex.IsMatch(name, "^[a-z0-9_-]+\\.svg$"))
            return NotFound();

        string path = Path.Combine(ModInit.modpath, "avatars", name);
        if (!System.IO.File.Exists(path))
            return NotFound();

        Response.Headers["Cache-Control"] = "public, max-age=86400";
        return PhysicalFile(Path.GetFullPath(path), "image/svg+xml");
    }

    /// <summary>Загруженные аватары: database/LampacHub/avatars/</summary>
    [HttpGet, AllowAnonymous]
    [Route("hub/avatar/{name}")]
    public ActionResult Uploaded(string name)
    {
        if (Off || string.IsNullOrEmpty(name) || !Regex.IsMatch(name, "^[a-z0-9]+\\.(jpg|png|webp)$"))
            return NotFound();

        string path = Path.Combine(HubAvatars.Dir, name);
        if (!System.IO.File.Exists(path))
            return NotFound();

        string type = name.EndsWith(".png") ? "image/png" : name.EndsWith(".webp") ? "image/webp" : "image/jpeg";
        Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
        return PhysicalFile(Path.GetFullPath(path), type);
    }

    /// <summary>Устройство и профиль одного аккаунта — иначе null</summary>
    (HubDevice device, HubProfile profile) DeviceProfileOf(string hub_token, string profile_id)
    {
        var device = HubStore.FindDevice(hub_token);
        if (device == null)
            return (null, null);

        var profile = HubStore.GetProfile(string.IsNullOrEmpty(profile_id) ? HubStore.MainProfile : profile_id);
        if (profile == null || profile.account_id != device.account_id)
            return (device, null);

        return (device, profile);
    }

    /// <summary>Настройки Lampa профиля (язык, тема, интерфейс, источники; плагины — если включено у профиля)</summary>
    [HttpGet, AllowAnonymous]
    [Route("hub/device/settings")]
    public ActionResult GetSettings(string hub_token, string profile_id)
    {
        if (Off)
            return NotFound();

        var (device, profile) = DeviceProfileOf(hub_token, profile_id);
        if (device == null)
            return Reply(new { status = "revoked" }, 401);
        if (profile == null)
            return Reply(new { error = "Профиль не найден" }, 404);

        var (data, updated) = HubStore.GetSettings(profile.id);
        JObject obj = null;
        if (data != null)
        {
            try { obj = JObject.Parse(data); } catch { }
        }

        if (obj != null && !profile.sync_plugins)
            obj.Remove("plugins");

        return Reply(new { status = "ok", updated, plugins = profile.sync_plugins, data = obj });
    }

    [HttpPost, AllowAnonymous]
    [RequestSizeLimit(2_000_000)]
    [Route("hub/device/settings")]
    public ActionResult SaveSettings(string hub_token, string profile_id, string data)
    {
        if (Off)
            return NotFound();

        var (device, profile) = DeviceProfileOf(hub_token, profile_id);
        if (device == null)
            return Reply(new { status = "revoked" }, 401);
        if (profile == null)
            return Reply(new { error = "Профиль не найден" }, 404);

        if (string.IsNullOrEmpty(data) || data.Length > 1_000_000)
            return Reply(new { error = "Пустые или слишком большие настройки" }, 400);

        JObject obj;
        try { obj = JObject.Parse(data); }
        catch { return Reply(new { error = "Неверный формат" }, 400); }

        long updated = HubStore.SaveSettings(profile.id, obj, profile.sync_plugins);
        return Reply(new { status = "ok", updated, plugins = profile.sync_plugins });
    }

    /// <summary>Свои категории профиля («Пересматриваю»): карточки целиком, свежие первыми</summary>
    [HttpGet, AllowAnonymous]
    [Route("hub/device/lists")]
    public ActionResult DeviceLists(string hub_token, string profile_id)
    {
        if (Off)
            return NotFound();

        var (device, profile) = DeviceProfileOf(hub_token, profile_id);
        if (device == null)
            return Reply(new { status = "revoked" }, 401);
        if (profile == null)
            return Reply(new { error = "Профиль не найден" }, 404);

        var items = HubStore.ListItems(profile.id);
        var lists = HubStore.OwnCategories.ToDictionary(c => c, c => items.Where(i => i.category == c).Select(i => i.card).ToList());
        return Reply(new { status = "ok", lists });
    }

    [HttpPost, AllowAnonymous]
    [RequestSizeLimit(200_000)]
    [Route("hub/device/lists/set")]
    public ActionResult DeviceListSet(string hub_token, string profile_id, string category, string card, bool on)
    {
        if (Off)
            return NotFound();

        var (device, profile) = DeviceProfileOf(hub_token, profile_id);
        if (device == null)
            return Reply(new { status = "revoked" }, 401);
        if (profile == null)
            return Reply(new { error = "Профиль не найден" }, 404);

        if (!HubStore.OwnCategories.Contains(category))
            return Reply(new { error = "Неизвестная категория" }, 400);

        var obj = SyncBookmarks.ParseObject(card);
        string id = obj?["id"]?.ToString();
        if (string.IsNullOrEmpty(id) || id.Length > 64)
            return Reply(new { error = "Нет карточки" }, 400);

        HubStore.SetListItem(profile.id, category, id, obj, on);
        return Reply(new { status = "ok" });
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
        var settings = HubStore.SettingsSummary();
        var now = DateTime.UtcNow;

        return Reply(new
        {
            account = new { account.id, account.name },
            host,
            code_ttl = ModInit.conf.code_ttl,
            profiles,
            presets = HubAvatars.Presets(),
            pin = HubStore.HasPin,
            servers = HubStore.GetServers(),
            settings = settings.ToDictionary(i => i.Key, i => new { keys = i.Value.keys, updated = i.Value.updated }),
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
    public ActionResult SaveProfile(string id, string name, string avatar, bool child, int age_limit = 12, bool sync_plugins = false)
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
            return Reply(new { status = "ok", profile = HubStore.CreateProfile(HubStore.MainAccount, name, avatar, child, age_limit, sync_plugins) });

        var profile = HubStore.GetProfile(id);
        if (profile == null || profile.account_id != HubStore.MainAccount)
            return Reply(new { error = "Профиль не найден" }, 404);

        if (profile.avatar != avatar)
            HubAvatars.DeleteUploaded(profile.avatar);

        HubStore.UpdateProfile(id, name, avatar, child, age_limit, sync_plugins);
        return Reply(new { status = "ok", profile = HubStore.GetProfile(id) });
    }

    /// <summary>Картинка с ПК: страница уже уменьшила её до 256×256, сюда приходит data URL</summary>
    [HttpPost]
    [RequestSizeLimit(4_000_000)]
    [Authorization(accessDeniedMessage: "unauthorized")]
    [Route("hub/api/profile/avatar")]
    public ActionResult UploadAvatar(string id, string data)
    {
        if (Off)
            return NotFound();

        if (CrossSite())
            return Reply(new { error = "forbidden" }, 403);

        var profile = HubStore.GetProfile(id);
        if (profile == null || profile.account_id != HubStore.MainAccount)
            return Reply(new { error = "Профиль не найден" }, 404);

        string url = HubAvatars.Save(data, out string error);
        if (url == null)
            return Reply(new { error }, 400);

        HubAvatars.DeleteUploaded(profile.avatar);
        HubStore.SetAvatar(id, url);
        return Reply(new { status = "ok", profile = HubStore.GetProfile(id) });
    }

    [HttpPost]
    [Authorization(accessDeniedMessage: "unauthorized")]
    [Route("hub/api/pin")]
    public ActionResult SetPin(string pin)
    {
        if (Off)
            return NotFound();

        if (CrossSite())
            return Reply(new { error = "forbidden" }, 403);

        pin = (pin ?? string.Empty).Trim();
        if (pin.Length != 0 && (pin.Length < 4 || pin.Length > 8 || !pin.All(char.IsAsciiDigit)))
            return Reply(new { error = "PIN — от 4 до 8 цифр" }, 400);

        HubStore.SetPin(pin);
        return Reply(new { status = "ok", pin = pin.Length != 0 });
    }

    #region lists
    HubProfile AdminProfile(string id)
    {
        var profile = HubStore.GetProfile(string.IsNullOrEmpty(id) ? HubStore.MainProfile : id);
        return profile != null && profile.account_id == HubStore.MainAccount ? profile : null;
    }

    static List<string> Csv(string value)
        => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();

    /// <summary>id жанров TMDB карточки: genre_ids или genres[].id</summary>
    static int[] Genres(JObject c)
    {
        var src = c["genre_ids"] as JArray ?? new JArray((c["genres"] as JArray ?? new JArray()).Select(g => g is JObject o ? o["id"] : g));
        return src.Select(g => int.TryParse(g?.ToString(), out int id) ? id : 0).Where(id => id > 0).Distinct().ToArray();
    }

    static object Brief(HubLists.Entry e)
    {
        var c = e.card;
        string date = (string)c["release_date"] ?? (string)c["first_air_date"] ?? string.Empty;
        return new
        {
            e.id,
            title = (string)c["title"] ?? (string)c["name"] ?? e.id,
            original = (string)c["original_title"] ?? (string)c["original_name"] ?? string.Empty,
            year = date.Length >= 4 ? date.Substring(0, 4) : string.Empty,
            poster = (string)c["poster_path"] ?? (string)c["poster"] ?? (string)c["img"] ?? string.Empty,
            tv = c["name"] != null || c["first_air_date"] != null || (string)c["method"] == "tv",
            source = (string)c["source"] ?? string.Empty,
            genres = Genres(c),
            lang = (string)c["original_language"] ?? string.Empty,
            vote = double.TryParse(c["vote_average"]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? Math.Round(v, 1) : 0,
            e.categories,
            e.updated
        };
    }

    [HttpGet]
    [Authorization(accessDeniedMessage: "unauthorized")]
    [Route("hub/api/lists")]
    public ActionResult Lists(string profile_id)
    {
        if (Off)
            return NotFound();

        var profile = AdminProfile(profile_id);
        if (profile == null)
            return Reply(new { error = "Профиль не найден" }, 404);

        return Reply(new
        {
            sync = SyncBookmarks.Available,
            profile = profile.id,
            items = HubLists.Entries(profile).Select(Brief)
        });
    }

    [HttpPost]
    [Authorization(accessDeniedMessage: "unauthorized")]
    [Route("hub/api/lists/change")]
    public async Task<ActionResult> ListsChange(string profile_id, string ids, string add, string remove)
    {
        if (Off)
            return NotFound();

        if (CrossSite())
            return Reply(new { error = "forbidden" }, 403);

        var profile = AdminProfile(profile_id);
        if (profile == null)
            return Reply(new { error = "Профиль не найден" }, 404);

        try
        {
            foreach (string id in Csv(ids))
                await HubLists.Change(profile, id, Csv(add), Csv(remove));
        }
        catch (TimeoutException ex)
        {
            return Reply(new { error = ex.Message }, 503);
        }

        return Reply(new { status = "ok" });
    }

    /// <summary>Сезоны, серии, статус и следующая серия из TMDB (кеш в базе). ids: tv:123,movie:456 — до 60 за раз</summary>
    [HttpPost]
    [Authorization(accessDeniedMessage: "unauthorized")]
    [Route("hub/api/titles/info")]
    public async Task<ActionResult> TitlesInfo(string ids)
    {
        if (Off)
            return NotFound();

        if (CrossSite())
            return Reply(new { error = "forbidden" }, 403);

        var keys = Csv(ids)
            .Where(k => System.Text.RegularExpressions.Regex.IsMatch(k, @"^(tv|movie):\d{1,9}$"))
            .Take(60)
            .ToList();

        var (info, failed) = await TitleInfo.Get(keys, host);
        return Reply(new { info, failed });
    }

    [HttpPost]
    [Authorization(accessDeniedMessage: "unauthorized")]
    [Route("hub/api/lists/remove")]
    public async Task<ActionResult> ListsRemove(string profile_id, string ids)
    {
        if (Off)
            return NotFound();

        if (CrossSite())
            return Reply(new { error = "forbidden" }, 403);

        var profile = AdminProfile(profile_id);
        if (profile == null)
            return Reply(new { error = "Профиль не найден" }, 404);

        try
        {
            int n = await HubLists.RemoveAll(profile, Csv(ids));
            return Reply(new { status = "ok", count = n });
        }
        catch (TimeoutException ex)
        {
            return Reply(new { error = ex.Message }, 503);
        }
    }

    /// <summary>Скопировать или перенести карточки в другой профиль</summary>
    [HttpPost]
    [Authorization(accessDeniedMessage: "unauthorized")]
    [Route("hub/api/lists/copy")]
    public async Task<ActionResult> ListsCopy(string profile_id, string to, string ids, bool move)
    {
        if (Off)
            return NotFound();

        if (CrossSite())
            return Reply(new { error = "forbidden" }, 403);

        var from = AdminProfile(profile_id);
        var target = AdminProfile(to);
        if (from == null || target == null)
            return Reply(new { error = "Профиль не найден" }, 404);
        if (from.id == target.id)
            return Reply(new { error = "Выберите другой профиль" }, 400);

        var list = Csv(ids);
        try
        {
            int n = await HubLists.Merge(target, HubLists.Export(from, list));
            if (move && n > 0)
                await HubLists.RemoveAll(from, list);
            return Reply(new { status = "ok", count = n });
        }
        catch (TimeoutException ex)
        {
            return Reply(new { error = ex.Message }, 503);
        }
    }

    /// <summary>Экспорт списков в JSON: одного профиля или всех, если profile_id пуст</summary>
    [HttpGet]
    [Authorization(accessDeniedMessage: "unauthorized")]
    [Route("hub/api/lists/export")]
    public ActionResult ListsExport(string profile_id)
    {
        if (Off)
            return NotFound();

        var profiles = string.IsNullOrEmpty(profile_id)
            ? HubStore.ListProfiles(HubStore.MainAccount)
            : new List<HubProfile> { AdminProfile(profile_id) }.Where(p => p != null).ToList();

        if (profiles.Count == 0)
            return Reply(new { error = "Профиль не найден" }, 404);

        var doc = new
        {
            format = "lampachub-lists",
            version = 1,
            exported = DateTimeOffset.UtcNow.ToString("o"),
            profiles = profiles.Select(p => new { p.id, p.name, items = HubLists.Export(p) })
        };

        string name = "lampachub-" + (profiles.Count == 1 ? profiles[0].id : "all") + "-" + DateTime.Now.ToString("yyyyMMdd") + ".json";
        Response.Headers["Cache-Control"] = "no-store";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"" + name + "\"";
        return Content(JsonConvert.SerializeObject(doc, Formatting.Indented), "application/json; charset=utf-8");
    }

    /// <summary>
    /// Импорт JSON в профиль. Тело — файл экспорта LampacHub (все его профили вливаются в выбранный)
    /// или просто массив записей { id, card, categories }.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(50_000_000)]
    [Authorization(accessDeniedMessage: "unauthorized")]
    [Route("hub/api/lists/import")]
    public async Task<ActionResult> ListsImport([FromQuery] string profile_id)
    {
        if (Off)
            return NotFound();

        if (CrossSite())
            return Reply(new { error = "forbidden" }, 403);

        var profile = AdminProfile(profile_id);
        if (profile == null)
            return Reply(new { error = "Профиль не найден" }, 404);

        JToken doc;
        try
        {
            using var reader = new StreamReader(Request.Body, Encoding.UTF8);
            doc = JToken.Parse(await reader.ReadToEndAsync());
        }
        catch
        {
            return Reply(new { error = "Файл не похож на JSON" }, 400);
        }

        var items = new List<HubLists.Portable>();
        try
        {
            IEnumerable<JToken> source = doc is JArray arr ? arr
                : (doc["profiles"] as JArray)?.SelectMany(p => (p["items"] as JArray) ?? new JArray()) ?? Enumerable.Empty<JToken>();

            foreach (var t in source.OfType<JObject>())
            {
                var card = t["card"] as JObject;
                string id = (string)t["id"] ?? card?["id"]?.ToString();
                var cats = (t["categories"] as JArray)?.Select(c => (string)c).Where(c => c != null).ToList();
                if (!string.IsNullOrEmpty(id) && card != null && cats != null)
                    items.Add(new HubLists.Portable { id = id, card = card, categories = cats });
            }
        }
        catch
        {
            return Reply(new { error = "Неизвестный формат файла" }, 400);
        }

        if (items.Count == 0)
            return Reply(new { error = "В файле нет записей LampacHub" }, 400);

        try
        {
            int n = await HubLists.Merge(profile, items);
            return Reply(new { status = "ok", count = n, skipped = items.Count - n });
        }
        catch (TimeoutException ex)
        {
            return Reply(new { error = ex.Message }, 503);
        }
    }
    #endregion

    #region import
    [HttpGet]
    [Authorization(accessDeniedMessage: "unauthorized")]
    [Route("hub/api/import/state")]
    public ActionResult ImportStatus()
    {
        if (Off)
            return NotFound();

        var queue = HubStore.QueueList();
        return Reply(new
        {
            state = Importer.State,
            queue = queue.GroupBy(q => q.profile_id).ToDictionary(g => g.Key, g => g.Count())
        });
    }

    /// <summary>Импорт Yummy в фоне. Токен (для закрытых списков) не сохраняется и не пишется в лог</summary>
    [HttpPost]
    [Authorization(accessDeniedMessage: "unauthorized")]
    [Route("hub/api/import/yummy")]
    public ActionResult ImportYummy(string profile_id, string user, string token)
    {
        if (Off)
            return NotFound();

        if (CrossSite())
            return Reply(new { error = "forbidden" }, 403);

        var profile = AdminProfile(profile_id);
        if (profile == null)
            return Reply(new { error = "Профиль не найден" }, 404);

        if (string.IsNullOrWhiteSpace(user))
            return Reply(new { error = "Укажите ник, id или ссылку на профиль Yummy" }, 400);

        string u = user, t = token;
        bool started = Importer.Start("yummy", profile, host, state => YummySource.Fetch(u, t, state));
        return started
            ? Reply(new { status = "ok", state = Importer.State })
            : Reply(new { error = "Импорт уже идёт" }, 409);
    }

    /// <summary>Код входа AniLiberty: его подтверждают на сайте, пароль на сервер не попадает</summary>
    [HttpPost]
    [Authorization(accessDeniedMessage: "unauthorized")]
    [Route("hub/api/import/aniliberty/otp")]
    public async Task<ActionResult> AniLibertyOtp()
    {
        if (Off)
            return NotFound();

        if (CrossSite())
            return Reply(new { error = "forbidden" }, 403);

        try
        {
            var otp = await AniLibertySource.RequestOtp();
            return Reply(new { code = otp.code, remaining = (int)(otp.expires - DateTime.UtcNow).TotalSeconds });
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is System.Net.Http.HttpRequestException || ex is TaskCanceledException)
        {
            return Reply(new { error = ex is InvalidOperationException ? ex.Message : "AniLiberty недоступен" }, 502);
        }
    }

    /// <summary>Импорт AniLiberty в фоне: по подтверждённому коду или по логину и паролю (не сохраняются)</summary>
    [HttpPost]
    [Authorization(accessDeniedMessage: "unauthorized")]
    [Route("hub/api/import/aniliberty")]
    public ActionResult ImportAniLiberty(string profile_id, string login, string password, bool otp = false)
    {
        if (Off)
            return NotFound();

        if (CrossSite())
            return Reply(new { error = "forbidden" }, 403);

        var profile = AdminProfile(profile_id);
        if (profile == null)
            return Reply(new { error = "Профиль не найден" }, 404);

        if (otp && AniLibertySource.Pending == null)
            return Reply(new { error = "Сначала получите код входа AniLiberty" }, 400);

        if (!otp && (string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password)))
            return Reply(new { error = "Укажите логин и пароль AniLiberty или войдите по коду" }, 400);

        string l = login, pw = password;
        bool started = Importer.Start("aniliberty", profile, host, state => AniLibertySource.Fetch(l, pw, otp, state));
        return started
            ? Reply(new { status = "ok", state = Importer.State })
            : Reply(new { error = "Импорт уже идёт" }, 409);
    }

    /// <summary>Импорт открытого списка Shikimori или MyAnimeList по нику, без входа</summary>
    [HttpPost]
    [Authorization(accessDeniedMessage: "unauthorized")]
    [Route("hub/api/import/{service:regex(^(shikimori|mal)$)}")]
    public ActionResult ImportRates(string service, string profile_id, string user)
    {
        if (Off)
            return NotFound();

        if (CrossSite())
            return Reply(new { error = "forbidden" }, 403);

        var profile = AdminProfile(profile_id);
        if (profile == null)
            return Reply(new { error = "Профиль не найден" }, 404);

        if (string.IsNullOrWhiteSpace(user))
            return Reply(new { error = "Укажите ник или ссылку на профиль" }, 400);

        string u = user;
        bool started = service == "mal"
            ? Importer.Start("mal", profile, host, state => MalSource.Fetch(u, state))
            : Importer.Start("shikimori", profile, host, state => ShikimoriSource.Fetch(u, state));
        return started
            ? Reply(new { status = "ok", state = Importer.State })
            : Reply(new { error = "Импорт уже идёт" }, 409);
    }

    /// <summary>Файл экспорта Yummy (JSON/HTML), Shikimori (JSON/XML) или MyAnimeList (XML, .gz) — сопоставление в фоне</summary>
    [HttpPost]
    [RequestSizeLimit(50_000_000)]
    [Authorization(accessDeniedMessage: "unauthorized")]
    [Route("hub/api/import/file")]
    public async Task<ActionResult> ImportFile([FromQuery] string profile_id)
    {
        if (Off)
            return NotFound();

        if (CrossSite())
            return Reply(new { error = "forbidden" }, 403);

        var profile = AdminProfile(profile_id);
        if (profile == null)
            return Reply(new { error = "Профиль не найден" }, 404);

        string source;
        List<ExternalTitle> titles;
        try
        {
            using var ms = new MemoryStream();
            await Request.Body.CopyToAsync(ms);
            (source, titles) = ExportFileSource.Parse(ms.ToArray());
        }
        catch (InvalidOperationException ex)
        {
            return Reply(new { error = ex.Message }, 400);
        }
        catch
        {
            return Reply(new { error = "Не удалось прочитать файл экспорта" }, 400);
        }

        if (titles.Count == 0)
            return Reply(new { error = "В файле нет тайтлов со статусом" }, 400);

        var list = titles;
        string name = source;
        bool started = Importer.Start(source, profile, host, state =>
        {
            state.notes.Add("Файл экспорта (" + name + "): " + list.Count + " тайтлов");
            return Task.FromResult(list);
        });
        return started
            ? Reply(new { status = "ok", source, count = titles.Count, state = Importer.State })
            : Reply(new { error = "Импорт уже идёт" }, 409);
    }

    /// <summary>Резервная копия Lampa / CUB (JSON) — карточки уже с id TMDB, вливаются сразу</summary>
    [HttpPost]
    [RequestSizeLimit(50_000_000)]
    [Authorization(accessDeniedMessage: "unauthorized")]
    [Route("hub/api/import/lampa")]
    public async Task<ActionResult> ImportLampa([FromQuery] string profile_id)
    {
        if (Off)
            return NotFound();

        if (CrossSite())
            return Reply(new { error = "forbidden" }, 403);

        var profile = AdminProfile(profile_id);
        if (profile == null)
            return Reply(new { error = "Профиль не найден" }, 404);

        List<HubLists.Portable> items;
        try
        {
            using var reader = new StreamReader(Request.Body, Encoding.UTF8);
            items = LampaBackupSource.Parse(JToken.Parse(await reader.ReadToEndAsync()));
        }
        catch (InvalidOperationException ex)
        {
            return Reply(new { error = ex.Message }, 400);
        }
        catch
        {
            return Reply(new { error = "Файл не похож на резервную копию Lampa" }, 400);
        }

        try
        {
            int n = await HubLists.Merge(profile, items);
            return Reply(new { status = "ok", count = n, skipped = items.Count - n });
        }
        catch (TimeoutException ex)
        {
            return Reply(new { error = ex.Message }, 503);
        }
    }

    [HttpGet]
    [Authorization(accessDeniedMessage: "unauthorized")]
    [Route("hub/api/import/queue")]
    public ActionResult ImportQueue(string profile_id)
    {
        if (Off)
            return NotFound();

        var profile = AdminProfile(profile_id);
        if (profile == null)
            return Reply(new { error = "Профиль не найден" }, 404);

        return Reply(new { items = HubStore.QueueList(profile.id) });
    }

    [HttpPost]
    [Authorization(accessDeniedMessage: "unauthorized")]
    [Route("hub/api/import/queue/resolve")]
    public async Task<ActionResult> ImportResolve(string id, string kind, string tmdb_id)
    {
        if (Off)
            return NotFound();

        if (CrossSite())
            return Reply(new { error = "forbidden" }, 403);

        var item = HubStore.QueueGet(id);
        var profile = item == null ? null : AdminProfile(item.profile_id);
        if (item == null || profile == null)
            return Reply(new { error = "Запись очереди не найдена" }, 404);

        if ((kind != "tv" && kind != "movie") || string.IsNullOrEmpty(tmdb_id) || !tmdb_id.All(char.IsDigit))
            return Reply(new { error = "Неверный тайтл TMDB" }, 400);

        try
        {
            await Importer.ResolveQueued(item, profile, kind, tmdb_id, host);
            return Reply(new { status = "ok" });
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is System.Net.Http.HttpRequestException || ex is TimeoutException)
        {
            return Reply(new { error = ex.Message }, 502);
        }
    }

    [HttpPost]
    [Authorization(accessDeniedMessage: "unauthorized")]
    [Route("hub/api/import/queue/skip")]
    public ActionResult ImportSkip(string id, string profile_id, bool all = false)
    {
        if (Off)
            return NotFound();

        if (CrossSite())
            return Reply(new { error = "forbidden" }, 403);

        if (all)
        {
            var profile = AdminProfile(profile_id);
            if (profile == null)
                return Reply(new { error = "Профиль не найден" }, 404);
            return Reply(new { status = "ok", count = HubStore.QueueClear(profile.id) });
        }

        return Reply(new { status = "ok", count = HubStore.QueueDelete(id) });
    }

    /// <summary>Поиск в TMDB для ручного сопоставления</summary>
    [HttpGet]
    [Authorization(accessDeniedMessage: "unauthorized")]
    [Route("hub/api/tmdb/search")]
    public async Task<ActionResult> TmdbSearch(string q, string kind)
    {
        if (Off)
            return NotFound();

        if (kind != "tv" && kind != "movie")
            kind = null;

        try
        {
            Tmdb.Prepare(host);
            var found = await Tmdb.Search(q, kind);
            return Reply(new { results = found.Take(20).Select(r => Tmdb.Brief(r.item, r.kind)) });
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            return Reply(new { error = ex.Message }, 502);
        }
    }
    #endregion

    /// <summary>Забыть сохранённые настройки профиля: следующее устройство профиля отправит свои</summary>
    [HttpPost]
    [Authorization(accessDeniedMessage: "unauthorized")]
    [Route("hub/api/profile/settings/reset")]
    public ActionResult ResetSettings(string id)
    {
        if (Off)
            return NotFound();

        if (CrossSite())
            return Reply(new { error = "forbidden" }, 403);

        var profile = HubStore.GetProfile(id);
        if (profile == null || profile.account_id != HubStore.MainAccount)
            return Reply(new { error = "Профиль не найден" }, 404);

        HubStore.ResetSettings(profile.id);
        return Reply(new { status = "ok" });
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

        var old = HubStore.GetProfile(id);
        if (old != null)
            HubAvatars.DeleteUploaded(old.avatar);

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
