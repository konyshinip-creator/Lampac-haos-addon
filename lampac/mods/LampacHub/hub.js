(function () {
  'use strict';

  if (window.lampachub_plugin) return;
  window.lampachub_plugin = true;

  var HOST = '{localhost}';
  var LOAD_SYNC = {load_sync};

  var KEY_TOKEN = 'lampachub_token';
  var KEY_ACCOUNT = 'lampachub_account';
  var KEY_PROFILES = 'lampachub_profiles';
  var KEY_PROMPTED = 'lampachub_prompted';
  var KEY_PIN = 'lampachub_pin';
  var KEY_SETTINGS = 'lampachub_settings_mark';

  var ICON = '<svg viewBox="0 0 24 24" fill="currentColor" xmlns="http://www.w3.org/2000/svg"><path d="M12 12a4.5 4.5 0 1 0 0-9 4.5 4.5 0 0 0 0 9Zm0 2c-4.4 0-8 2.2-8 5v1.5c0 .3.2.5.5.5h15c.3 0 .5-.2.5-.5V19c0-2.8-3.6-5-8-5Z"/></svg>';

  var menuItem = null;

  function whenReady(callback) {
    if (window.Lampa && Lampa.Storage && Lampa.Utils && Lampa.Listener && window.$) callback();
    else setTimeout(function () { whenReady(callback); }, 300);
  }

  // --- сеть -----------------------------------------------------------------
  function request(method, path, params, done) {
    var query = [];
    for (var k in params) {
      if (params.hasOwnProperty(k) && params[k] !== undefined && params[k] !== null)
        query.push(encodeURIComponent(k) + '=' + encodeURIComponent(params[k]));
    }

    var url = HOST + path;
    var body = null;
    if (method === 'GET') {
      if (query.length) url += (url.indexOf('?') < 0 ? '?' : '&') + query.join('&');
    } else {
      body = query.join('&');
    }

    var finished = false;
    function finish(status, text) {
      if (finished) return;
      finished = true;
      var json = null;
      try { json = text ? JSON.parse(text) : null; } catch (e) { }
      done(status, json);
    }

    try {
      var xhr = new XMLHttpRequest();
      xhr.open(method, url, true);
      xhr.timeout = 15000;
      if (body !== null) xhr.setRequestHeader('Content-Type', 'application/x-www-form-urlencoded');
      xhr.onreadystatechange = function () { if (xhr.readyState === 4) finish(xhr.status, xhr.responseText); };
      xhr.onerror = function () { finish(0, null); };
      xhr.ontimeout = function () { finish(0, null); };
      xhr.send(body);
    } catch (e) {
      finish(0, null);
    }
  }

  // --- состояние ------------------------------------------------------------
  function token() { return Lampa.Storage.get(KEY_TOKEN, '') || ''; }
  function profiles() { var p = Lampa.Storage.get(KEY_PROFILES, '[]'); return Array.isArray(p) ? p : []; }
  function currentProfileId() { return String(Lampa.Storage.get('lampac_profile_id', '') || ''); }

  function findProfile(id) {
    var list = profiles();
    for (var i = 0; i < list.length; i++) if (String(list[i].id) === String(id)) return list[i];
    return null;
  }

  /** Область данных Sync/TimeCode: пустой profile_id и "0" — одна и та же основная область */
  function area(uid, profileId) {
    var p = String(profileId || '');
    return String(uid || '') + ':' + (p === '0' ? '' : p);
  }

  /**
   * bookmark.js СЛИВАЕТ серверные закладки с локальными, а в пустую область отправляет локальные.
   * Поэтому при смене профиля локальные закладки и тайм-коды прежнего профиля стираем: иначе они
   * перетекли бы в новый профиль. На сервере у прежнего профиля всё остаётся.
   */
  function resetLocalData() {
    Lampa.Storage.set('favorite', {}, true);
    Lampa.Storage.set('file_view', {}, true);
    Lampa.Storage.set('lampac_bookmark_version', '0');
    Lampa.Storage.set('lampac_bookmark_scope', '');
    Lampa.Storage.set('lampac_timecode_cards', []);
  }

  function reload() {
    setTimeout(function () { window.location.reload(); }, 800);
  }

  /** Адрес картинки аватара или null, если аватар — эмодзи/буква */
  function avatarSrc(a) {
    if (!a) return null;
    if (/^https?:\/\//i.test(a)) return a;
    if (a.charAt(0) === '/') return HOST + a;
    return null;
  }

  function profileLabel(p) {
    if (!p) return '';
    var emoji = p.avatar && !avatarSrc(p.avatar) ? p.avatar + ' ' : '';
    return emoji + p.name;
  }

  function currentProfile() { return findProfile(currentProfileId() || '0'); }

  /** Возрастной лимит текущего профиля или -1, если профиль не детский */
  function childLimit() {
    if (!token()) return -1;
    var p = currentProfile();
    return p && p.child ? (typeof p.age_limit === 'number' ? p.age_limit : 12) : -1;
  }

  // --- вход / выход ---------------------------------------------------------
  /**
   * Ответ сервера (/hub/device/me или подтверждённый код) → локальные ключи Lampa.
   * uid основного аккаунта совпадает с общей идентичностью, которую Lampac раздаёт через
   * timecode.js, так что обычно меняется только lampac_profile_id.
   */
  function apply(data, fresh) {
    var oldArea = area(Lampa.Storage.get('lampac_unic_id', ''), currentProfileId());

    Lampa.Storage.set(KEY_TOKEN, data.token);
    Lampa.Storage.set(KEY_ACCOUNT, data.account ? data.account.name : '');
    Lampa.Storage.set(KEY_PROFILES, data.profiles || []);
    Lampa.Storage.set(KEY_PIN, !!data.pin);

    if (data.uid && Lampa.Storage.get('lampac_unic_id', '') !== data.uid)
      Lampa.Storage.set('lampac_unic_id', data.uid);

    var profileId = currentProfileId();
    if (fresh || !findProfile(profileId)) {
      if (fresh && data.profile_id && findProfile(data.profile_id)) profileId = String(data.profile_id);
      else if (!findProfile(profileId)) profileId = '';
    }

    if (profileId === '') {
      // Профиль, который стоял на устройстве, удалили в админке — пусть выберут заново
      Lampa.Storage.set('lampac_profile_id', '0');
      if (area(data.uid, '0') !== oldArea) { resetLocalData(); reload(); return true; }
      updateMenu();
      if (profiles().length > 1) setTimeout(function () { pickProfile(true); }, 500);
      return false;
    }

    Lampa.Storage.set('lampac_profile_id', profileId);
    updateMenu();

    if (area(data.uid, profileId) !== oldArea) {
      resetLocalData();
      reload();
      return true;
    }
    return false;
  }

  function refresh() {
    if (!token()) return;

    request('GET', '/hub/device/me', { hub_token: token() }, function (status, data) {
      if (status === 200 && data && data.status === 'ok') {
        var reloading = apply(data, false);
        loadSync();
        if (!reloading) { pullSettings(); loadLists(); }
      }
      else if (status === 401) {
        logout(true);
      }
      else {
        // Сервер недоступен — работаем с тем, что сохранено
        loadSync();
      }
    });
  }

  function logout(revoked) {
    Lampa.Storage.set(KEY_TOKEN, '');
    Lampa.Storage.set(KEY_PROFILES, []);
    Lampa.Storage.set(KEY_ACCOUNT, '');
    Lampa.Storage.set(KEY_PIN, false);
    Lampa.Storage.set('lampac_profile_id', '');
    resetLocalData();
    Lampa.Noty.show(revoked ? 'Устройство отключено от LampacHub в админке' : 'Вы вышли из LampacHub');
    reload();
  }

  function deviceName() {
    var name = '';
    try {
      if (Lampa.Platform.is('android')) name = 'Android';
      else if (Lampa.Platform.is('tizen')) name = 'Samsung TV';
      else if (Lampa.Platform.is('webos')) name = 'LG TV';
      else if (Lampa.Platform.is('orsay')) name = 'Samsung Orsay';
      else if (Lampa.Platform.is('apple_tv')) name = 'Apple TV';
      else if (Lampa.Platform.is('apple')) name = 'iOS';
      else if (Lampa.Platform.is('nw')) name = 'Lampa для ПК';
      else name = 'Браузер';
      if (Lampa.Platform.screen && Lampa.Platform.screen('tv') && name.indexOf('TV') < 0) name += ' (ТВ)';
    } catch (e) {
      name = 'Lampa';
    }
    return name;
  }

  function login() {
    request('POST', '/hub/device/code', { name: deviceName() }, function (status, data) {
      if (status !== 200 || !data || !data.code) {
        Lampa.Noty.show((data && data.error) || 'LampacHub недоступен');
        return;
      }

      var code = String(data.code);
      var html = $('<div style="text-align:center;padding:1em 0 .5em"></div>');
      html.append($('<div style="opacity:.75"></div>').text('Откройте на телефоне или компьютере'));
      html.append($('<div style="font-size:1.3em;margin:.3em 0 1em;word-break:break-all"></div>').text(data.url || (HOST + '/hub')));
      html.append($('<div style="opacity:.75"></div>').text('и введите код'));
      html.append($('<div style="font-size:3.5em;font-weight:700;letter-spacing:.15em;margin:.2em 0"></div>').text(code.slice(0, 3) + ' ' + code.slice(3)));
      var statusLine = $('<div style="opacity:.6"></div>').text('Код действует ' + Math.max(1, Math.round((data.ttl || 600) / 60)) + ' мин');
      html.append(statusLine);

      var previous = Lampa.Controller.enabled().name;
      var closed = false;
      var timer = null;

      function close() {
        if (closed) return;
        closed = true;
        clearTimeout(timer);
        Lampa.Modal.close();
        Lampa.Controller.toggle(previous);
      }

      function poll() {
        if (closed) return;
        request('GET', '/hub/device/poll', { key: data.key }, function (st, res) {
          if (closed) return;
          if (st === 200 && res && res.status === 'ok') {
            close();
            Lampa.Noty.show('Устройство подключено к LampacHub');
            var reloading = apply(res, true);
            loadSync();
            if (!reloading) pullSettings();
          }
          else if (st === 404) {
            statusLine.text('Код истёк. Закройте окно и запросите новый.');
          }
          else {
            timer = setTimeout(poll, 3000);
          }
        });
      }

      Lampa.Modal.open({ title: 'Вход в LampacHub', html: html, size: 'medium', onBack: close });
      timer = setTimeout(poll, 3000);
    });
  }

  // --- профили --------------------------------------------------------------
  function setProfile(id) {
    var uid = Lampa.Storage.get('lampac_unic_id', '');
    var changed = area(uid, id) !== area(uid, currentProfileId());

    Lampa.Storage.set('lampac_profile_id', String(id));
    request('POST', '/hub/device/profile', { hub_token: token(), profile_id: id }, function () { });

    var p = findProfile(id);
    Lampa.Noty.show('Профиль: ' + (p ? p.name : id));
    updateMenu();

    if (changed) {
      resetLocalData();
      reload();
    }
  }

  function pickProfile(forced) {
    var list = profiles();
    if (!list.length) { Lampa.Noty.show('Профили не загружены, проверьте связь с сервером'); return; }

    var current = currentProfileId() || '0';
    var previous = Lampa.Controller.enabled().name;

    Lampa.Select.show({
      title: forced ? 'Выберите профиль' : 'Профиль',
      items: list.map(function (p) {
        return { title: profileLabel(p), subtitle: p.child ? 'Детский' : '', selected: String(p.id) === current, profile: String(p.id) };
      }),
      onSelect: function (item) {
        Lampa.Controller.toggle(previous);
        if (item.profile === current && !forced) return;
        var target = findProfile(item.profile);
        // С детского профиля на взрослый — только по PIN; между детскими и на детский — свободно
        if (target && target.child) setProfile(item.profile);
        else guardChild(function () { setProfile(item.profile); });
      },
      onBack: function () {
        Lampa.Controller.toggle(previous);
      }
    });
  }

  // --- интерфейс ------------------------------------------------------------
  function menuText() {
    if (!token()) return 'Войти в LampacHub';
    var p = findProfile(currentProfileId() || '0');
    return p ? 'Профиль: ' + p.name : 'Профиль';
  }

  function updateMenu() {
    if (!menuItem) return;
    menuItem.find('.menu__text').text(menuText());
    var p = token() ? currentProfile() : null;
    var src = p ? avatarSrc(p.avatar) : null;
    var ico = menuItem.find('.menu__ico');
    if (src) ico.html('<img src="' + src.replace(/"/g, '&quot;') + '" style="width:100%;height:100%;border-radius:50%;object-fit:cover" alt="">');
    else ico.html(ICON);
  }

  function addMenu() {
    menuItem = $('<li class="menu__item selector" data-action="lampachub"><div class="menu__ico">' + ICON + '</div><div class="menu__text"></div></li>');
    menuItem.on('hover:enter', function () {
      if (token()) pickProfile(false);
      else login();
    });
    $('.menu .menu__list').eq(0).append(menuItem);
    updateMenu();
  }

  function addSettings() {
    if (!Lampa.SettingsApi) return;

    Lampa.SettingsApi.addComponent({ component: 'lampachub', name: 'LampacHub', icon: ICON });

    Lampa.SettingsApi.addParam({
      component: 'lampachub',
      param: { name: 'lampachub_login', type: 'button' },
      field: { name: 'Войти по коду', description: 'Устройство покажет код, подтвердите его в админке ' + HOST + '/hub' },
      onChange: login
    });

    Lampa.SettingsApi.addParam({
      component: 'lampachub',
      param: { name: 'lampachub_profile', type: 'button' },
      field: { name: 'Сменить профиль', description: 'У каждого профиля свои закладки, история и тайм-коды' },
      onChange: function () {
        if (token()) pickProfile(false);
        else Lampa.Noty.show('Сначала войдите по коду');
      }
    });

    Lampa.SettingsApi.addParam({
      component: 'lampachub',
      param: { name: 'lampachub_servers_menu', type: 'button' },
      field: { name: 'Серверы', description: 'Выбрать другой сервер Lampac или изменить список' },
      onChange: function () { serverMenu(false); }
    });

    Lampa.SettingsApi.addParam({
      component: 'lampachub',
      param: { name: KEY_SERVERS_ASK, type: 'trigger', default: true },
      field: { name: 'Выбор сервера при запуске', description: 'Показывать меню серверов при старте, если их больше одного' }
    });

    Lampa.SettingsApi.addParam({
      component: 'lampachub',
      param: { name: 'lampachub_logout', type: 'button' },
      field: { name: 'Выйти', description: 'Отключить это устройство от LampacHub' },
      onChange: function () {
        if (token()) guardChild(function () { logout(false); });
        else Lampa.Noty.show('Устройство не подключено');
      }
    });
  }

  // --- настройки профиля ----------------------------------------------------
  /**
   * Настройки Lampa общие для всех устройств профиля. Берём всё, что Lampa и плагины регистрируют
   * как параметры настроек (Lampa.Params.values), кроме того, что зависит от экрана и железа:
   * плеер, размер интерфейса, навигация, кеш. Плагины — только если это включено у профиля.
   * Значения передаются как есть, строками из localStorage.
   */
  var SETTINGS_EXTRA = ['language', 'cub_theme', 'menu_sort', 'menu_hide'];
  var SETTINGS_LOCAL = /^(player|lampac|lampachub|account|cloud_|internal_torrclient|infuse_|tmdb_proxy|proxy_tmdb)/;
  var SETTINGS_LOCAL_KEYS = ['interface_size', 'navigation_type', 'keyboard_type', 'scroll_type', 'light_version',
    'hide_outside_the_screen', 'animation', 'advanced_animation', 'request_caching', 'cache_images', 'pages_save_total',
    'protocol', 'device_name', 'cub_domain', 'plugins', 'favorite', 'file_view'];

  var settings = { ready: false, plugins: false, applying: false, dirty: {}, timer: null };

  function syncedSetting(key) {
    if (!key || typeof key !== 'string') return false;
    if (key === 'plugins') return settings.plugins;
    if (SETTINGS_LOCAL.test(key) || SETTINGS_LOCAL_KEYS.indexOf(key) >= 0) return false;
    if (SETTINGS_EXTRA.indexOf(key) >= 0) return true;
    var values = Lampa.Params && Lampa.Params.values;
    return !!(values && values.hasOwnProperty(key));
  }

  function settingKeys() {
    var keys = SETTINGS_EXTRA.slice();
    var values = (Lampa.Params && Lampa.Params.values) || {};
    for (var k in values) if (values.hasOwnProperty(k) && keys.indexOf(k) < 0) keys.push(k);
    if (settings.plugins) keys.push('plugins');
    return keys.filter(syncedSetting);
  }

  function settingsArea() { return area(Lampa.Storage.get('lampac_unic_id', ''), currentProfileId()); }

  function settingsMark() {
    var m = Lampa.Storage.get(KEY_SETTINGS, '{}');
    return m && typeof m === 'object' && m.area === settingsArea() ? (m.updated || 0) : 0;
  }

  function setSettingsMark(updated) {
    Lampa.Storage.set(KEY_SETTINGS, { area: settingsArea(), updated: updated });
  }

  function rawSetting(key) {
    try { return window.localStorage.getItem(key); } catch (e) { return null; }
  }

  function writeSetting(key, raw) {
    var value = raw;
    var c = raw.charAt(0);
    if (c === '{' || c === '[') {
      try { value = JSON.parse(raw); } catch (e) { value = raw; }
    }
    Lampa.Storage.set(key, value, true);
  }

  function pushSettings(all) {
    if (!token()) return;
    var keys = all ? settingKeys() : Object.keys(settings.dirty).filter(syncedSetting);
    settings.dirty = {};

    var data = {};
    keys.forEach(function (k) {
      var raw = rawSetting(k);
      if (raw !== null) data[k] = raw;
    });
    if (!Object.keys(data).length) return;

    request('POST', '/hub/device/settings', { hub_token: token(), profile_id: currentProfileId() || '0', data: JSON.stringify(data) }, function (status, res) {
      if (status === 200 && res && res.updated) setSettingsMark(res.updated);
    });
  }

  function pullSettings() {
    if (!token()) return;

    request('GET', '/hub/device/settings', { hub_token: token(), profile_id: currentProfileId() || '0' }, function (status, res) {
      if (status !== 200 || !res || res.status !== 'ok') return;

      settings.plugins = !!res.plugins;
      settings.ready = true;

      // Профиль ещё без настроек — сохраняем настройки этого устройства
      if (!res.data || !res.updated) { pushSettings(true); return; }
      if (res.updated <= settingsMark()) return;

      var changed = [];
      settings.applying = true;
      try {
        for (var k in res.data) {
          if (!res.data.hasOwnProperty(k) || !syncedSetting(k)) continue;
          var raw = res.data[k];
          if (typeof raw !== 'string') raw = JSON.stringify(raw);
          if (rawSetting(k) === raw) continue;
          writeSetting(k, raw);
          changed.push(k);
        }
      } finally {
        settings.applying = false;
      }

      setSettingsMark(res.updated);

      // Язык, тема и плагины применяются только при запуске Lampa
      if (changed.length) {
        Lampa.Noty.show('LampacHub: применены настройки профиля');
        reload();
      }
    });
  }

  function watchSettings() {
    Lampa.Storage.listener.follow('change', function (e) {
      if (!settings.ready || settings.applying || !token() || !e || !syncedSetting(e.name)) return;
      settings.dirty[e.name] = true;
      clearTimeout(settings.timer);
      settings.timer = setTimeout(function () { pushSettings(false); }, 2500);
    });
  }

  // --- мои списки -----------------------------------------------------------
  /**
   * Категории как в Yummy/Anihub. Пять статусов и Избранное — это категории Lampa (их синхронизирует
   * bookmark.js), «Пересматриваю» хранит LampacHub. Статус у карточки один: новый снимает прежний.
   */
  var LISTS = [
    { id: 'look', title: 'Смотрю', status: true },
    { id: 'scheduled', title: 'Планирую', status: true },
    { id: 'viewed', title: 'Просмотрено', status: true },
    { id: 'continued', title: 'Отложено', status: true },
    { id: 'thrown', title: 'Брошено', status: true },
    { id: 'rewatch', title: 'Пересматриваю', status: true, own: true },
    { id: 'like', title: 'Избранное' }
  ];
  var LAMPA_MARKS = ['look', 'viewed', 'scheduled', 'continued', 'thrown'];
  var LIST_ICON = '<svg viewBox="0 0 24 24" fill="currentColor" xmlns="http://www.w3.org/2000/svg"><path d="M4 5h2v2H4V5Zm4 0h12v2H8V5Zm-4 6h2v2H4v-2Zm4 0h12v2H8v-2Zm-4 6h2v2H4v-2Zm4 0h12v2H8v-2Z"/></svg>';

  var rewatch = [];

  function listTitle(id) {
    for (var i = 0; i < LISTS.length; i++) if (LISTS[i].id === id) return LISTS[i].title;
    return id;
  }

  function loadLists(done) {
    if (!token()) { rewatch = []; if (done) done(); return; }
    request('GET', '/hub/device/lists', { hub_token: token(), profile_id: currentProfileId() || '0' }, function (status, res) {
      if (status === 200 && res && res.lists) rewatch = res.lists.rewatch || [];
      if (done) done();
    });
  }

  function inRewatch(card) {
    for (var i = 0; i < rewatch.length; i++) if (rewatch[i] && String(rewatch[i].id) === String(card.id)) return true;
    return false;
  }

  function slimCard(card, source) {
    var c = {};
    try { c = Lampa.Utils.clearCard ? Lampa.Utils.clearCard(JSON.parse(JSON.stringify(card))) : card; } catch (e) { c = card; }
    var out = {};
    ['id', 'title', 'name', 'original_title', 'original_name', 'poster_path', 'backdrop_path', 'release_date', 'first_air_date',
      'vote_average', 'original_language', 'genre_ids', 'img', 'poster', 'background_image', 'number_of_seasons', 'overview'].forEach(function (k) {
      if (c[k] !== undefined && c[k] !== null) out[k] = c[k];
    });
    if (!out.genre_ids && card.genres) out.genre_ids = card.genres.map(function (g) { return g.id; });
    if (typeof out.overview === 'string' && out.overview.length > 500) out.overview = out.overview.slice(0, 500);
    out.source = card.source || source || 'tmdb';
    return out;
  }

  function setRewatch(card, on, done) {
    var slim = slimCard(card);
    rewatch = rewatch.filter(function (c) { return c && String(c.id) !== String(card.id); });
    if (on) rewatch.unshift(slim);
    request('POST', '/hub/device/lists/set', { hub_token: token(), profile_id: currentProfileId() || '0', category: 'rewatch', card: JSON.stringify(slim), on: on }, function (status) {
      if (status !== 200) Lampa.Noty.show('LampacHub: не удалось сохранить «Пересматриваю»');
      if (done) done();
    });
  }

  function cardState(card) {
    var st = {};
    try { st = Lampa.Favorite.check(card) || {}; } catch (e) { }
    var res = { like: !!st.like, rewatch: inRewatch(card), status: '' };
    for (var i = 0; i < LAMPA_MARKS.length; i++) if (st[LAMPA_MARKS[i]]) { res.status = LAMPA_MARKS[i]; break; }
    if (!res.status && res.rewatch) res.status = 'rewatch';
    return res;
  }

  function setStatus(card, target) {
    var st = cardState(card);
    if (st.status === target) target = '';

    // Снять прежний статус
    if (st.status && st.status !== 'rewatch') Lampa.Favorite.remove(st.status, card);
    if (st.rewatch && target !== 'rewatch') setRewatch(card, false);

    if (target === 'rewatch') setRewatch(card, true);
    else if (target) Lampa.Favorite.add(target, card);

    Lampa.Noty.show(target ? listTitle(target) : 'Статус снят');
  }

  function listMenu(card, button) {
    var previous = Lampa.Controller.enabled().name;
    var st = cardState(card);
    var items = LISTS.map(function (l) {
      var on = l.status ? st.status === l.id : st[l.id];
      return { title: (on ? '✓ ' : '') + l.title, selected: on, list: l.id };
    });
    if (st.status) items.push({ title: 'Убрать статус', list: '' });

    Lampa.Select.show({
      title: 'Мой список',
      items: items,
      onSelect: function (item) {
        Lampa.Controller.toggle(previous);
        if (item.list === 'like') {
          if (st.like) Lampa.Favorite.remove('like', card); else Lampa.Favorite.add('like', card);
          Lampa.Noty.show(st.like ? 'Убрано из избранного' : 'Добавлено в избранное');
        }
        else setStatus(card, item.list || st.status);
        setTimeout(function () { updateListButton(card, button); }, 300);
      },
      onBack: function () { Lampa.Controller.toggle(previous); }
    });
  }

  function updateListButton(card, button) {
    var st = cardState(card);
    button.find('span').text(st.status ? listTitle(st.status) : 'Мой список');
  }

  /** Кнопка «Мой список» в карточке тайтла */
  function addListButton() {
    Lampa.Listener.follow('full', function (e) {
      if (e.type !== 'complite' || !e.data || !e.data.movie || !token()) return;
      var root = e.object && e.object.activity && e.object.activity.render ? e.object.activity.render() : null;
      if (!root || root.find('.lampachub-list').length) return;

      var card = e.data.movie;
      if (!card.source && e.object.source) card.source = e.object.source;

      var button = $('<div class="full-start__button selector lampachub-list">' + LIST_ICON + '<span>Мой список</span></div>');
      button.on('hover:enter', function () { listMenu(card, button); });

      var book = root.find('.button--book');
      if (book.length) book.after(button);
      else {
        var box = root.find('.full-start-new__buttons');
        if (!box.length) box = root.find('.full-start__buttons');
        box.append(button);
      }
      updateListButton(card, button);
    });

    // Статус из стандартной закладки Lampa снимает «Пересматриваю»
    try {
      Lampa.Favorite.listener.follow('add', function (e) {
        if (e && e.card && LAMPA_MARKS.indexOf(e.where) >= 0 && inRewatch(e.card)) setRewatch(e.card, false);
      });
    } catch (err) { }
  }

  /** Источник Lampa для «Пересматриваю»: открывается тем же экраном, что и категории каталога */
  function addListSource() {
    if (!Lampa.Api || !Lampa.Api.sources || Lampa.Api.sources.lampachub) return;
    Lampa.Api.sources.lampachub = {
      list: function (params, oncomplite, onerror) {
        loadLists(function () {
          var page = params.page || 1;
          var all = rewatch.slice();
          var total = Math.max(1, Math.ceil(all.length / 20));
          var results = all.slice((page - 1) * 20, page * 20);
          if (results.length) oncomplite({ results: results, page: page, total_pages: total });
          else onerror();
        });
      }
    };
  }

  function openList(id) {
    var title = listTitle(id);
    if (id === 'rewatch') Lampa.Activity.push({ url: 'rewatch', title: title, component: 'category_full', source: 'lampachub', page: 1 });
    else Lampa.Activity.push({ url: '', title: title, component: 'favorite', type: id, page: 1 });
  }

  function myLists() {
    var previous = Lampa.Controller.enabled().name;
    loadLists(function () {
      var items = LISTS.map(function (l) {
        var n = 0;
        if (l.own) n = rewatch.length;
        else { try { n = (Lampa.Favorite.get({ type: l.id }) || []).length; } catch (e) { } }
        return { title: l.title, subtitle: String(n), list: l.id };
      });
      Lampa.Select.show({
        title: 'Мои списки',
        items: items,
        onSelect: function (item) { Lampa.Controller.toggle(previous); openList(item.list); },
        onBack: function () { Lampa.Controller.toggle(previous); }
      });
    });
  }

  function addListsMenu() {
    var item = $('<li class="menu__item selector" data-action="lampachub_lists"><div class="menu__ico">' + LIST_ICON + '</div><div class="menu__text">Мои списки</div></li>');
    item.on('hover:enter', myLists);
    $('.menu .menu__list').eq(0).append(item);
  }

  // --- PIN ------------------------------------------------------------------
  /** Выход из детского профиля: если в админке задан PIN, сначала спросить его и проверить на сервере */
  function guardChild(next) {
    if (childLimit() < 0 || !Lampa.Storage.get(KEY_PIN, false)) { next(); return; }
    askPin(next);
  }

  var PIN_MAX = 8;

  function pinStyles() {
    if (document.getElementById('lampachub-pin-style')) return;
    var css = '.lhub-pin{text-align:center;padding:.5em 0}' +
      '.lhub-pin__dots{font-size:2.4em;letter-spacing:.35em;min-height:1.4em;margin:.2em 0 .1em}' +
      '.lhub-pin__msg{min-height:1.4em;opacity:.7;margin-bottom:.6em}' +
      '.lhub-pin__msg.error{color:#ff6b6b;opacity:1}' +
      '.lhub-pin__grid{display:inline-grid;grid-template-columns:repeat(3,4.2em);gap:.6em}' +
      '.lhub-pin__key{height:3em;line-height:3em;border-radius:.5em;background:rgba(255,255,255,.1);font-size:1.4em;cursor:pointer}' +
      '.lhub-pin__key.focus,.lhub-pin__key:hover{background:#fff;color:#000}';
    $('<style id="lampachub-pin-style"></style>').text(css).appendTo('head');
  }

  /** Ввод PIN цифровой панелью: цифры скрыты точками, работают и кнопки пульта 0–9 */
  function askPin(next) {
    pinStyles();
    var previous = Lampa.Controller.enabled().name;
    var value = '';
    var busy = false;
    var closed = false;

    var html = $('<div class="lhub-pin"></div>');
    var dots = $('<div class="lhub-pin__dots"></div>');
    var msg = $('<div class="lhub-pin__msg"></div>').text('Введите PIN');
    var grid = $('<div class="lhub-pin__grid"></div>');
    html.append(dots, msg, grid);

    function render() {
      var d = '';
      for (var i = 0; i < value.length; i++) d += '\u25CF';
      dots.text(d || '\u00A0');
    }

    function message(text, error) { msg.text(text).toggleClass('error', !!error); }

    function close() {
      if (closed) return;
      closed = true;
      window.removeEventListener('keydown', onKey, true);
      Lampa.Modal.close();
      Lampa.Controller.toggle(previous);
    }

    function press(key) {
      if (busy || closed) return;
      if (key === 'del') value = value.slice(0, -1);
      else if (key === 'ok') { submit(); return; }
      else if (value.length < PIN_MAX) value += key;
      message('Введите PIN');
      render();
    }

    function submit() {
      if (value.length < 4) { message('PIN — от 4 до 8 цифр', true); return; }
      busy = true;
      message('Проверка…');
      request('POST', '/hub/device/pin', { hub_token: token(), pin: value }, function (status, res) {
        busy = false;
        if (closed) return;
        if (status === 200 && res && res.ok) { close(); next(); return; }
        value = '';
        render();
        message((res && res.error) || 'Не удалось проверить PIN', true);
      });
    }

    function onKey(e) {
      var code = e.keyCode || e.which;
      var digit = code >= 48 && code <= 57 ? code - 48 : (code >= 96 && code <= 105 ? code - 96 : -1);
      if (digit < 0) return;
      e.preventDefault();
      e.stopPropagation();
      press(String(digit));
    }

    ['1', '2', '3', '4', '5', '6', '7', '8', '9', 'del', '0', 'ok'].forEach(function (key) {
      var label = key === 'del' ? '\u232B' : (key === 'ok' ? 'OK' : key);
      var btn = $('<div class="lhub-pin__key selector"></div>').text(label);
      btn.on('hover:enter', function () { press(key); });
      grid.append(btn);
    });

    render();
    Lampa.Modal.open({ title: 'PIN для выхода из детского профиля', html: html, size: 'small', onBack: close });
    window.addEventListener('keydown', onKey, true);
  }

  // --- детский профиль ------------------------------------------------------
  var HORROR = 27, THRILLER = 53;

  function blockedGenres(limit) { return limit < 16 ? [HORROR, THRILLER] : []; }

  function allowedItem(item, limit) {
    if (!item || typeof item !== 'object') return true;
    if (item.adult) return false;
    var bad = blockedGenres(limit);
    var genres = item.genre_ids || (item.genres || []).map(function (g) { return g && g.id; });
    for (var i = 0; i < genres.length; i++) if (bad.indexOf(genres[i]) >= 0) return false;
    return true;
  }

  function isTmdbUrl(url) {
    return typeof url === 'string' && (url.indexOf('api.themoviedb.org') >= 0 || url.indexOf('/tmdb/api') >= 0 || url.indexOf('/tmdb/api.themoviedb.org') >= 0);
  }

  /** Сертификат TMDB → возраст. RU даёт «16+», США — PG-13, TV-14 и т.п. */
  function certAge(cert) {
    var c = String(cert || '').toUpperCase().replace(/\s+/g, '');
    if (!c) return null;
    var m = /^(\d{1,2})\+?$/.exec(c);
    if (m) return parseInt(m[1], 10);
    var map = { 'G': 0, 'TV-Y': 0, 'TV-G': 0, 'TV-Y7': 7, 'PG': 8, 'TV-PG': 10, 'PG-13': 13, 'TV-14': 14, 'R': 17, 'TV-MA': 17, 'NC-17': 18 };
    return map.hasOwnProperty(c) ? map[c] : null;
  }

  function ratingAge(data, tv) {
    var list = (data && data.results) || [];
    var order = ['RU', 'US', 'DE', 'GB', 'FR'];
    for (var o = 0; o < order.length; o++) {
      for (var i = 0; i < list.length; i++) {
        var r = list[i];
        if (r.iso_3166_1 !== order[o]) continue;
        if (tv) { var a = certAge(r.rating); if (a !== null) return a; }
        else {
          var best = null;
          (r.release_dates || []).forEach(function (d) { var x = certAge(d.certification); if (x !== null && (best === null || x > best)) best = x; });
          if (best !== null) return best;
        }
      }
    }
    return null;
  }

  var MPAA = { 0: 'G', 6: 'PG', 12: 'PG-13', 16: 'R' };

  /** discover у TMDB умеет фильтровать по рейтингу сам; остальным запросам — только include_adult=false */
  function childTmdbUrl(url) {
    var limit = childLimit();
    if (limit < 0 || typeof url !== 'string') return url;
    if (url.indexOf('include_adult=') < 0) url = Lampa.Utils.addUrlComponent(url, 'include_adult=false');
    if (/discover\/movie/.test(url) && url.indexOf('certification') < 0)
      url = Lampa.Utils.addUrlComponent(url, 'certification_country=US&certification.lte=' + encodeURIComponent(MPAA[limit] || 'PG-13'));
    return url;
  }

  function wrapTmdb() {
    if (!Lampa.TMDB || typeof Lampa.TMDB.api !== 'function' || Lampa.TMDB.api.lampachub) return;
    var original = Lampa.TMDB.api;
    var wrapped = function () { return childTmdbUrl(original.apply(this, arguments)); };
    wrapped.lampachub = true;
    Lampa.TMDB.api = wrapped;
  }

  /** Ответы TMDB со списками: убираем 18+ и жанры, закрытые лимитом. Сетевые запросы Lampa идут через $.ajax. */
  function wrapAjax() {
    if (!window.$ || !$.ajax || $.ajax.lampachub) return;
    var original = $.ajax;
    var wrapped = function (options) {
      try {
        var limit = childLimit();
        var opts = typeof options === 'object' ? options : arguments[1];
        if (limit >= 0 && opts && isTmdbUrl(opts.url || options) && typeof opts.success === 'function') {
          var success = opts.success;
          opts.success = function (data) {
            try {
              if (data && Array.isArray(data.results)) data.results = data.results.filter(function (i) { return allowedItem(i, limit); });
            } catch (e) { }
            return success.apply(this, arguments);
          };
        }
      } catch (e) { }
      return original.apply(this, arguments);
    };
    for (var k in original) if (original.hasOwnProperty(k)) wrapped[k] = original[k];
    wrapped.lampachub = true;
    $.ajax = wrapped;
  }

  function blockCard(reason) {
    Lampa.Noty.show('Недоступно в детском профиле' + (reason ? ' (' + reason + ')' : ''));
    try { Lampa.Activity.backward(); } catch (e) { }
  }

  /** Открытая карточка: 18+ и рейтинг выше лимита закрываются сразу */
  function guardCards() {
    Lampa.Listener.follow('full', function (e) {
      var limit = childLimit();
      if (limit < 0 || e.type !== 'complite' || !e.data || !e.data.movie) return;

      var movie = e.data.movie;
      if (!allowedItem(movie, limit)) { blockCard(movie.adult ? '18+' : 'жанр'); return; }

      var tv = !!(movie.name || movie.first_air_date || (e.object && e.object.method === 'tv'));
      var embedded = tv ? movie.content_ratings : movie.release_dates;
      var check = function (data) {
        var age = ratingAge(data, tv);
        if (age !== null && age > limit) blockCard(age + '+');
      };

      if (embedded) { check(embedded); return; }
      if (!movie.id || !Lampa.TMDB || !Lampa.TMDB.key) return;

      var url = Lampa.TMDB.api((tv ? 'tv/' : 'movie/') + movie.id + '/' + (tv ? 'content_ratings' : 'release_dates') + '?api_key=' + Lampa.TMDB.key());
      try {
        var net = new Lampa.Reguest();
        net.silent(url, check, function () { });
      } catch (err) { }
    });
  }

  function hideAdultMenu() {
    if (childLimit() < 0) return;
    $('.menu [data-action="sisi"]').remove();
  }

  function startChildMode() {
    wrapAjax();
    wrapTmdb();
    guardCards();
    hideAdultMenu();
    // Плагины грузятся параллельно: TMDB Proxy может переопределить TMDB.api, а sisi.js — добавить пункт меню позже
    setInterval(function () { wrapTmdb(); hideAdultMenu(); }, 1500);
  }

  /** Закладки и тайм-коды идут через стандартные плагины Lampac; оба сами защищены от двойной загрузки */
  function loadSync() {
    if (!LOAD_SYNC || !token()) return;

    var urls = [];
    if (!window.lampacBookmarkSyncInitialized) urls.push(HOST + '/bookmark.js');
    if (!window.lampac_timecode_plugin) urls.push(HOST + '/timecode.js');
    if (urls.length) Lampa.Utils.putScriptAsync(urls, function () { });
  }

  // --- серверы ---------------------------------------------------------------
  /**
   * Меню выбора сервера при запуске. Список хранится на устройстве (у каждого адреса свой
   * localStorage, поэтому при переходе он передаётся в адресе ?hub_servers=) и дополняется
   * серверами из админки (/hub/servers). Этот сервер в списке есть всегда.
   */
  var KEY_SERVERS = 'lampachub_servers';
  var KEY_SERVERS_ASK = 'lampachub_servers_ask';
  var serverMenuShown = false;

  function normUrl(url) {
    url = String(url || '').trim().replace(/\/+$/, '');
    if (url && !/^https?:\/\//i.test(url)) url = 'http://' + url;
    return url;
  }

  // Адрес этого Lampac: сервер подставляет его в HOST, иначе берём адрес страницы
  function hereUrl() { return normUrl(/^https?:\/\//i.test(HOST) ? HOST : window.location.protocol + '//' + window.location.host); }

  function sameServer(a, b) { return normUrl(a).toLowerCase() === normUrl(b).toLowerCase(); }

  function servers() {
    var list = Lampa.Storage.get(KEY_SERVERS, '[]');
    return Array.isArray(list) ? list.filter(function (s) { return s && s.url; }) : [];
  }

  function saveServers(list) {
    var seen = {}, out = [];
    list.forEach(function (s) {
      var url = normUrl(s.url), key = url.toLowerCase();
      if (!url || seen[key]) return;
      seen[key] = true;
      out.push({ name: String(s.name || '').trim() || url.replace(/^https?:\/\//, ''), url: url });
    });
    Lampa.Storage.set(KEY_SERVERS, out);
    return out;
  }

  function mergeServers(extra) {
    var list = servers();
    (extra || []).forEach(function (s) {
      if (s && s.url && !list.some(function (x) { return sameServer(x.url, s.url); })) list.push(s);
    });
    if (!list.some(function (x) { return sameServer(x.url, hereUrl()); }))
      list.unshift({ name: 'Этот сервер (' + hereUrl().replace(/^https?:\/\//, '') + ')', url: hereUrl() });
    return saveServers(list);
  }

  function askServers() { return Lampa.Storage.field ? Lampa.Storage.field(KEY_SERVERS_ASK) !== false : Lampa.Storage.get(KEY_SERVERS_ASK, true) !== false; }

  function sessionGet(k) { try { return window.sessionStorage.getItem(k); } catch (e) { return null; } }
  function sessionSet(k, v) { try { window.sessionStorage.setItem(k, v); } catch (e) { } }

  function goServer(url) {
    if (sameServer(url, hereUrl())) return;
    Lampa.Noty.show('Переход на ' + url);
    var payload = encodeURIComponent(JSON.stringify(servers()));
    setTimeout(function () { window.location.href = normUrl(url) + '/?hub_chosen=1&hub_servers=' + payload; }, 300);
  }

  function serverMenu(atStart) {
    var list = servers();
    var previous = Lampa.Controller.enabled().name;
    var items = list.map(function (s) {
      var here = sameServer(s.url, hereUrl());
      return { title: s.name, subtitle: s.url + (here ? ' · сейчас' : ''), selected: here, url: s.url };
    });
    items.push({ title: 'Изменить список…', action: 'edit' });

    Lampa.Select.show({
      title: atStart ? 'Выберите сервер' : 'Серверы',
      items: items,
      onSelect: function (item) {
        Lampa.Controller.toggle(previous);
        if (item.action === 'edit') editServers();
        else goServer(item.url);
      },
      onBack: function () { Lampa.Controller.toggle(previous); }
    });
  }

  function input(title, value, done) {
    Lampa.Input.edit({ title: title, value: value || '', free: true, nosave: true }, function (v) { done(String(v || '').trim()); });
  }

  function editServers() {
    var list = servers();
    var previous = Lampa.Controller.enabled().name;
    var items = list.map(function (s, n) { return { title: s.name, subtitle: s.url, index: n }; });
    items.unshift({ title: '+ Добавить сервер', action: 'add' });

    Lampa.Select.show({
      title: 'Изменить список серверов',
      items: items,
      onSelect: function (item) {
        Lampa.Controller.toggle(previous);
        if (item.action === 'add') {
          input('Адрес сервера, например 192.168.1.20:9118', '', function (url) {
            if (!url) return;
            input('Название', '', function (name) {
              saveServers(servers().concat([{ name: name, url: url }]));
              Lampa.Noty.show('Сервер добавлен');
              editServers();
            });
          });
        }
        else editServer(item.index);
      },
      onBack: function () { Lampa.Controller.toggle(previous); }
    });
  }

  function editServer(index) {
    var s = servers()[index];
    if (!s) return;
    var previous = Lampa.Controller.enabled().name;
    Lampa.Select.show({
      title: s.name,
      items: [
        { title: 'Открыть', action: 'open' },
        { title: 'Переименовать', action: 'name' },
        { title: 'Изменить адрес', action: 'url' },
        { title: 'Поднять в начало', action: 'top' },
        { title: 'Удалить', action: 'del' }
      ],
      onSelect: function (item) {
        Lampa.Controller.toggle(previous);
        var list = servers();
        if (item.action === 'open') goServer(s.url);
        else if (item.action === 'name') input('Название', s.name, function (v) { if (v) { list[index].name = v; saveServers(list); } editServers(); });
        else if (item.action === 'url') input('Адрес сервера', s.url, function (v) { if (v) { list[index].url = v; saveServers(list); } editServers(); });
        else if (item.action === 'top') { list.unshift(list.splice(index, 1)[0]); saveServers(list); editServers(); }
        else if (item.action === 'del') {
          if (sameServer(s.url, hereUrl())) { Lampa.Noty.show('Текущий сервер удалить нельзя'); editServers(); return; }
          list.splice(index, 1); saveServers(list); editServers();
        }
      },
      onBack: function () { Lampa.Controller.toggle(previous); editServers(); }
    });
  }

  function startServers() {
    var chosen = false;
    try {
      // Без URLSearchParams: на старых ТВ его нет
      var params = {}, rest = [];
      window.location.search.replace(/^\?/, '').split('&').forEach(function (pair) {
        if (!pair) return;
        var i = pair.indexOf('='), k = decodeURIComponent(i < 0 ? pair : pair.slice(0, i));
        if (k === 'hub_servers' || k === 'hub_chosen') params[k] = decodeURIComponent(i < 0 ? '' : pair.slice(i + 1));
        else rest.push(pair);
      });
      if (params.hub_servers) mergeServers(JSON.parse(params.hub_servers));
      chosen = params.hub_chosen === '1';
      if ((params.hub_servers || chosen) && window.history && history.replaceState)
        history.replaceState(null, '', window.location.pathname + (rest.length ? '?' + rest.join('&') : '') + window.location.hash);
    } catch (e) { }

    mergeServers([]);
    if (chosen) sessionSet('lampachub_server_chosen', '1');

    request('GET', '/hub/servers', {}, function (status, data) {
      if (status === 200 && data && Array.isArray(data.servers)) mergeServers(data.servers);

      // Один раз за запуск приложения; перезагрузка при смене профиля меню не показывает
      if (sessionGet('lampachub_server_chosen') || !askServers() || servers().length < 2) return;
      sessionSet('lampachub_server_chosen', '1');
      serverMenuShown = true;
      setTimeout(function () { serverMenu(true); }, 1200);
    });
  }

  function start() {
    addSettings();
    startServers();
    addMenu();
    startChildMode();
    watchSettings();
    addListSource();
    addListButton();
    if (token()) addListsMenu();

    // Sync предпочитает account_email (вход в CUB) идентичности uid — профили LampacHub тогда не работают
    if (token() && Lampa.Storage.get('account_email', ''))
      setTimeout(function () { Lampa.Noty.show('LampacHub: выйдите из аккаунта CUB, иначе закладки синхронизируются через CUB, а не по профилям'); }, 4000);

    if (token()) {
      // Даём стандартным плагинам из lampainit загрузиться первыми
      setTimeout(refresh, 1500);
    }
    else if (!Lampa.Storage.get(KEY_PROMPTED, false)) {
      Lampa.Storage.set(KEY_PROMPTED, true);
      setTimeout(function () {
        // Сначала выбор сервера: предложение входа покажем при следующем запуске
        if (serverMenuShown) { Lampa.Storage.set(KEY_PROMPTED, false); return; }
        var previous = Lampa.Controller.enabled().name;
        Lampa.Select.show({
          title: 'LampacHub: подключить устройство?',
          items: [
            { title: 'Войти по коду', action: 'login' },
            { title: 'Позже (Настройки → LampacHub)', action: 'later' }
          ],
          onSelect: function (item) {
            Lampa.Controller.toggle(previous);
            if (item.action === 'login') login();
          },
          onBack: function () { Lampa.Controller.toggle(previous); }
        });
      }, 2500);
    }
  }

  whenReady(function () {
    if (window.appready) start();
    else Lampa.Listener.follow('app', function (e) { if (e.type === 'ready') start(); });
  });
})();
