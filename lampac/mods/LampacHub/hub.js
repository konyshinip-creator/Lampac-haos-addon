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

  function profileLabel(p) {
    if (!p) return '';
    var avatar = p.avatar && !/^https?:\/\//i.test(p.avatar) ? p.avatar + ' ' : '';
    return avatar + p.name;
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
      if (area(data.uid, '0') !== oldArea) { resetLocalData(); reload(); return; }
      updateMenu();
      if (profiles().length > 1) setTimeout(function () { pickProfile(true); }, 500);
      return;
    }

    Lampa.Storage.set('lampac_profile_id', profileId);
    updateMenu();

    if (area(data.uid, profileId) !== oldArea) {
      resetLocalData();
      reload();
    }
  }

  function refresh() {
    if (!token()) return;

    request('GET', '/hub/device/me', { hub_token: token() }, function (status, data) {
      if (status === 200 && data && data.status === 'ok') {
        apply(data, false);
        loadSync();
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
            apply(res, true);
            loadSync();
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
        if (item.profile !== current || forced) setProfile(item.profile);
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
    if (menuItem) menuItem.find('.menu__text').text(menuText());
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
      param: { name: 'lampachub_logout', type: 'button' },
      field: { name: 'Выйти', description: 'Отключить это устройство от LampacHub' },
      onChange: function () {
        if (token()) logout(false);
        else Lampa.Noty.show('Устройство не подключено');
      }
    });
  }

  /** Закладки и тайм-коды идут через стандартные плагины Lampac; оба сами защищены от двойной загрузки */
  function loadSync() {
    if (!LOAD_SYNC || !token()) return;

    var urls = [];
    if (!window.lampacBookmarkSyncInitialized) urls.push(HOST + '/bookmark.js');
    if (!window.lampac_timecode_plugin) urls.push(HOST + '/timecode.js');
    if (urls.length) Lampa.Utils.putScriptAsync(urls, function () { });
  }

  function start() {
    addSettings();
    addMenu();

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
