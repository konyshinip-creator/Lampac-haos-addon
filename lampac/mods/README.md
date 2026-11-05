# Дополнительные модули Lampac

Модули из этой папки работают в **любом** Lampac NextGen, не только в аддоне
Home Assistant. Каждый модуль — обычная папка с `manifest.json` и `.cs`-файлами,
Lampac компилирует их Roslyn-ом при старте (и пересобирает на лету при
изменении файлов, `"dynamic": true`).

| Модуль | Раздел | Что даёт |
|---|---|---|
| `Senku` | Онлайн (аниме) | senku.one: поиск, озвучки Kodik (до 720p) и собственные mp4 до 1080p/2160p. Ключ тайтла — Shikimori ID |
| `Anistar` | Онлайн (аниме) | anistar.org: свой плеер AniStar (озвучка, многоголосая, субтитры). Поток идёт через прокси Lampac, потому что CDN требует Referer |
| `Animaunt` | Онлайн (аниме) | animaunt.org: плеер OpenNI, прямые mp4 до 1080p. Тайтлы с плеером Kodik пропускаются — их отдаёт балансер Kodik |
| `AnimeFox` | Онлайн (аниме) | animefox.org: поиск, озвучки и серии плеера Kodik. Поток отдаёт встроенный модуль Kodik, он должен быть включён. Раздел сайта /hentai/ не показывается |
| `AniTokyo` | Онлайн (аниме) | anitokyo.tv: поиск, озвучки и серии плеера Kodik. Поток отдаёт встроенный модуль Kodik, он должен быть включён. Серии Sibnet и VK не показываются |
| `Hentasis` | 18+ (SISI) | hentasis1.top: каталог, поиск, серии с озвучкой и субтитрами. **Выключен по умолчанию** |
| `HentaiUkr` | 18+ (SISI) | hentaiukr.com: каталог, поиск, прямые mp4 до 1080p. Тайтлы с тегами «Школа» и «Школярки» и тайтлы без тегов не показываются, это не настройка. **Выключен по умолчанию** |
| `AnimeMonitor` | Служебный | Мониторинг доступности источников: страница `/animemon`, JSON `/animemon/status`, webhook при смене статуса |

## Установка в обычный Lampac

### Вариант 1: автоматически из GitHub (рекомендуется)

Создайте файл `mods/repository.yaml` в папке Lampac (или допишите в него,
если он уже есть):

```yaml
- repository: https://github.com/konyshinip-creator/Lampac-haos-addon
  branch: main
  modules:
    - lampac/mods/Senku
    - lampac/mods/Anistar
    - lampac/mods/Animaunt
    - lampac/mods/AnimeFox
    - lampac/mods/AniTokyo
    - lampac/mods/Hentasis
    - lampac/mods/HentaiUkr
    - lampac/mods/AnimeMonitor
```

При каждом старте Lampac проверяет последний коммит ветки и, если он
изменился, скачивает модули в `module/<имя репозитория>/<Модуль>/`.
Лишние строки из списка можно убрать. Вместо `konyshinip-creator/Lampac-haos-addon`
можно указать зеркало `https://github.com/infomirlabtest-alt/Lampac-haos-addon-`,
содержимое одинаковое.

### Вариант 2: вручную

Скопируйте нужные папки в `mods/` Lampac (`mods/Senku`, `mods/Anistar`, …)
и перезапустите сервер. В Docker — примонтируйте их томом:

```yaml
services:
  lampac:
    image: ghcr.io/lampac-nextgen/lampac
    volumes:
      - ./mods/Senku:/lampac/mods/Senku
      - ./mods/Anistar:/lampac/mods/Anistar
```

### Подключение в Lampa

Модули работают на сервере, отдельных плагинов у них нет. В Lampa
(**Настройки → Расширения → Добавить плагин**) достаточно стандартных плагинов
Lampac:

- `http://<IP Lampac>:9118/online.js` — Senku, AniStar, AniMaunt, AnimeFox и AniTokyo появятся в
  списке источников кнопки **Онлайн** в карточках аниме;
- `http://<IP Lampac>:9118/sisi.js` — Hentasis и HentaiUkr появятся в разделе 18+.

Если источника нет в списке, проверьте, что он включён в `init.conf`
(`"enable": true`, см. ниже), и посмотрите `/animemon` — сайт может быть
недоступен.

## Настройка (`init.conf`)

Все модули читают свою секцию из `init.conf` и подхватывают изменения без
перезапуска. Онлайн-модули поддерживают стандартные поля Lampac
(`enable`, `host`, `displayindex`, `useproxy`, `streamproxy`, …).

```json
{
  "Senku":    { "enable": true },
  "Anistar":  { "enable": true, "host": "https://anistar.org" },
  "Animaunt": { "enable": true },
  "AnimeFox": { "enable": true },
  "AniTokyo": { "enable": true },
  "Hentasis": { "enable": true },
  "HentaiUkr": { "enable": true },
  "Xhamster": { "host": "https://ua.xhamster2.com" },
  "AnimeMonitor": {
    "enable": true,
    "interval": 10,
    "timeout": 15,
    "webhook": "http://homeassistant.local:8123/api/webhook/anime-sources"
  }
}
```

Если сайт переехал на новое зеркало, достаточно поменять `host` в его секции.
Так же настроен встроенный Xhamster: новый модуль ему не нужен, адрес зеркала
задаётся полем `host`, в аддоне Home Assistant — опцией `xhamster_host`.

### Монитор доступности

По умолчанию проверяются senku.one (JSON-поиск), anistar.org, animaunt.org,
animefox.org, anitokyo.tv, hentasis1.top, hentaiukr.com (по `robots.txt`), плеер
OpenNI и Kodik. Состояния:

- `up` — сайт отвечает как ожидается;
- `challenge` — ответила защита (Cloudflare/DDoS-Guard), 403/429 или пришла
  HTML-заглушка вместо ожидаемого ответа;
- `down` — ошибка сети, таймаут или другой HTTP-код.

Свой список целей задаётся полем `targets`; он целиком заменяет список по
умолчанию:

```json
{
  "AnimeMonitor": {
    "targets": [
      { "name": "Anistar", "url": "https://anistar.org/robots.txt", "marker": "User-agent" },
      { "name": "Senku", "url": "https://senku.one/senku-api/api/v2/search?q=naruto", "marker": "\"ok\":true" }
    ]
  }
}
```

`/animemon?refresh=true` и `/animemon/status?refresh=true` запускают
проверку сразу.

Webhook получает `POST` с JSON при каждой смене состояния:

```json
{ "source": "Anistar", "url": "https://anistar.org/robots.txt", "previous": "up", "state": "down", "code": 0, "error": "timeout", "checked": "2026-11-04T18:00:00Z" }
```

### Home Assistant

REST-сенсор (`configuration.yaml`):

```yaml
rest:
  - resource: http://<IP Lampac>:9118/animemon/status
    scan_interval: 300
    sensor:
      - name: "Аниме-источники доступны"
        value_template: "{{ value_json.up }}/{{ value_json.total }}"
        json_attributes:
          - down
          - sources
```

Уведомление при падении источника через webhook (укажите
`http://<IP HA>:8123/api/webhook/anime-sources` в опции `monitor_webhook`
аддона или в `AnimeMonitor.webhook`):

```yaml
automation:
  - alias: "Аниме-источник изменил статус"
    trigger:
      - platform: webhook
        webhook_id: anime-sources
        local_only: true
    action:
      - service: notify.notify
        data:
          message: "{{ trigger.json.source }}: {{ trigger.json.previous }} → {{ trigger.json.state }} {{ trigger.json.error or '' }}"
```

## Ограничения

- Форматы сайтов разобраны на нескольких тайтлах; при изменении вёрстки
  модуль просто перестанет показывать источник (смотрите монитор и лог).
- API senku.one закрытое и может поменяться без предупреждения.
- hentai-hub.net модуля не имеет: все его страницы закрыты капчей с проверкой
  возраста.
- Тайтлы anistar с внешним плеером DEEP (`deep-ent.ru`) не поддерживаются —
  этот плеер сознательно закрыт для сторонних сайтов. Их озвучка есть в Kodik.
- С IP дата-центров Cloudflare/DDoS-Guard могут вести себя строже, чем с
  домашнего IP. Монитор покажет такой случай как `challenge`.
