# Lampac — документация аддона

Backend-сервер [Lampac Next Generation](https://github.com/lampac-nextgen/lampac)
для клиента Lampa. Собран поверх официального образа
**`ghcr.io/lampac-nextgen/lampac`** (GitHub Container Registry, multi-arch:
amd64/arm64) с добавленными модулями из папки `mods/` этого репозитория.

## Содержание

- [Что включено по умолчанию](#что-включено-по-умолчанию)
- [Подключение в Lampa](#подключение-в-lampa)
- [Опции аддона](#опции-аддона)
- [Управление модулями](#управление-модулями)
- [Онлайн- и аниме-провайдеры](#онлайн--и-аниме-провайдеры)
- [Собственные модули аддона](#собственные-модули-аддона)
- [Управление источниками контента (/adminpanel/)](#управление-источниками-контента-adminpanel)
- [Где лежат конфиги](#где-лежат-конфиги-persistent)
- [Сеть](#сеть)
- [Версии и автообновление](#версии-и-автообновление)
- [Технические детали сборки](#технические-детали-сборки-образа)

## Что включено по умолчанию

Включено всё, что работает без аккаунтов, токенов и ручной настройки:

- **Базовые модули**: TorrServer, JacRed, Sync, TimeCode, DLNA, Catalog,
  Tracks, WebLog, ForkPlayerXML, MsxNative, Potok
- **Админка** Lampac на `/adminpanel/` (пароль — `root_password`)
- **Аниме-провайдеры**: AniLiberty, AniLibria, Animevost, AnimeON, AniMedia,
  MoonAnime, Mikai, AnimeLib, AnimeGo, Kodik, AniDub, AiLiberty, Animebesst,
  Dreamerscast + свои Senku, Anistar, Animaunt, AnimeFox, AniTokyo
- **Онлайн-провайдеры**, выключенные в апстриме: FanCDN, FlixCDN, Videoseed,
  Mirage, Spectre, Kinoukr, Autoembed, Playembed, Rgshows, Twoembed
  (остальные бесплатные источники апстрима и так включены)
- **Hentasis** и **HentaiUkr** (18+, раздел SISI), зеркало `ua.xhamster2.com` для
  встроенного Xhamster и **монитор доступности** источников
- Порт **9118**, часовой пояс **Europe/Kiev**
- Пароль администратора генерируется случайно при первом старте и один раз
  выводится в лог аддона, если не задан свой через `root_password`

Выключено (нужна отдельная настройка или ресурсы): Transcoding, CacheMedia,
ProxyLimiter, TelegramAuth, TelegramAuthBot, SeriesNotify, QRAuth и платные
источники (Rezka, Filmix, KinoPub, Alloha, VoKino, iRemux и др. — им нужен
свой аккаунт или токен, включаются через `/adminpanel/`).

> ⚠️ **Если аддон уже был установлен**, Home Assistant сохраняет ваши старые
> значения опций — новые дефолты применятся только к новым опциям
> (`online_providers`, `enable_seriesnotify`, `enable_qrauth`). Чтобы
> получить полный набор: на вкладке **Configuration** нажмите **⋮ → Reset to
> defaults** (или выставьте переключатели вручную) и замените
> `anime_providers` на список из таблицы ниже. Затем перезапустите аддон.

## Подключение в Lampa

Все модули и провайдеры работают на сервере — в самой Lampa достаточно
подключить плагины Lampac один раз.

1. В Lampa: **Настройки → Расширения → Добавить плагин**.
2. Добавьте нужные адреса (вместо `IP` — адрес хоста Home Assistant):

   | Плагин | Адрес | Что даёт |
   |---|---|---|
   | Онлайн | `http://IP:9118/online.js` | Кнопка «Онлайн» в карточке: все онлайн- и аниме-провайдеры, включая Senku, AniStar, AniMaunt |
   | 18+ | `http://IP:9118/sisi.js` | Раздел 18+ (в том числе Hentasis) |
   | Potok | `http://IP:9118/naked-gun` и `http://IP:9118/blue-oyster` | Плагины модуля Potok (онлайн и 18+) |

3. Перезапустите Lampa. В карточке фильма или аниме нажмите **Онлайн** —
   в списке источников появятся включённые провайдеры. Аниме-источники
   обычно показываются только в карточках аниме.

Проще всего — открыть `http://IP:9118/` в браузере или в Lampa на ТВ: Lampac
отдаёт свою сборку Lampa, в которой плагины онлайн, 18+, TorrServer, JacRed,
TimeCode и синхронизация уже подключены.

Мониторинг источников: `http://IP:9118/animemon` (страница) и
`http://IP:9118/animemon/status` (JSON для REST-сенсора).

## Опции аддона

| Опция | По умолчанию | Описание |
|---|---|---|
| `root_password` | *(пусто → авто-генерация)* | Пароль для `/adminpanel/` и `/weblog` |
| `port` | `9118` | Порт Lampac |
| `timezone` | `Europe/Kiev` | Часовой пояс контейнера |
| `enable_torrserver` | `true` | Модуль TorrServer |
| `enable_jacred` | `true` | Модуль JacRed |
| `enable_sync` | `true` | Модуль Sync |
| `enable_timecode` | `true` | Модуль TimeCode |
| `enable_dlna` | `true` | Модуль DLNA |
| `enable_catalog` | `true` | Модуль Catalog |
| `enable_tracks` | `true` | Модуль Tracks (аудиодорожки через ffprobe) |
| `enable_weblog` | `true` | Живой просмотр логов запросов на `/weblog` |
| `enable_forkplayerxml` | `true` | Модуль ForkPlayerXML |
| `enable_msxnative` | `true` | Модуль MsxNative |
| `enable_potok` | `true` | Модуль Potok |
| `enable_transcoding` | `false` | Транскодирование (нагружает CPU) |
| `enable_cachemedia` | `false` | Кэш видео на диске (занимает место) |
| `enable_proxylimiter` | `false` | Лимиты прокси (для публичных серверов) |
| `enable_telegramauth` | `false` | Авторизация через Telegram (нужен бот) |
| `enable_telegramauthbot` | `false` | Telegram-бот авторизации (нужен токен) |
| `enable_seriesnotify` | `false` | Уведомления о новых сериях в Telegram (нужен токен) |
| `enable_qrauth` | `false` | Вход по QR через Telegram (нужна настройка) |
| `enable_admin_panel` | `true` | Встроенная веб-админка Lampac на `/adminpanel/` |
| `anime_providers` | см. ниже | Аниме-провайдеры через запятую |
| `online_providers` | см. ниже | Онлайн-провайдеры, выключенные в апстриме; пусто — не включать |
| `enable_hentasis` | `true` | Источник Hentasis (18+) |
| `enable_hentaiukr` | `true` | Источник HentaiUkr (18+) |
| `xhamster_host` | `https://ua.xhamster2.com` | Зеркало встроенного Xhamster; пусто — не менять `init.conf` |
| `enable_monitor` | `true` | Мониторинг доступности источников (`/animemon`) |
| `monitor_interval` | `10` | Интервал проверок, минуты |
| `monitor_webhook` | *(пусто)* | URL для POST при смене статуса источника |
| `enable_hub` | `false` | LampacHub: профили, вход устройств по коду, админка `/hub`; плагин `hub.js` подключается в Lampa сам |
| `extra_init_json` | *(пусто)* | Произвольный JSON, подмешиваемый в `init.conf` при каждом запуске |

## Управление модулями

Базовые модули Lampac включаются и выключаются переключателями на вкладке
**Configuration** аддона. При старте `run.sh` добавляет или убирает имя модуля
из `BaseModule.SkipModules` в `init.conf`. Изменения применяются после
перезапуска аддона.

> ⚠️ Модули **DLNA, Tracks, Transcoding, Catalog** не рассчитаны на
> публичный доступ (нет своей аутентификации и лимитов). Если порт 9118
> открыт в интернет — выключите их или закройте порт firewall'ом/reverse
> proxy с аутентификацией.

## Онлайн- и аниме-провайдеры

Провайдеры — это источники контента внутри модуля `Online`. Каждый
включается своим ключом в `init.conf`:

```json
{ "AniLibria": { "enable": true } }
```

Аддон только **включает** провайдеры из опций `anime_providers` и
`online_providers` и не выключает остальные. Значения по умолчанию:

- `anime_providers`:
  `AniLiberty,AniLibria,Animevost,AnimeON,AniMedia,MoonAnime,Mikai,AnimeLib,AnimeGo,Kodik,AniDub,AiLiberty,Animebesst,Dreamerscast,Senku,Anistar,Animaunt,AnimeFox,AniTokyo`
- `online_providers`:
  `FanCDN,FlixCDN,Videoseed,Mirage,Spectre,Kinoukr,Autoembed,Playembed,Rgshows,Twoembed`

Mirage, Spectre, Videoseed и ENG-источники (Autoembed, Playembed, Twoembed)
используют встроенный в Lampac браузер. Если он недоступен, источник просто не
появится в списке, ошибок это не вызывает.

Выключить конкретный провайдер: уберите его из опции, а если он уже был
включён — снимите галочку в `/adminpanel/` или передайте
`{"FanCDN": {"enable": false}}` через `extra_init_json`.

## Собственные модули аддона

В образ копируется папка `mods/` репозитория. Lampac компилирует модули из
неё при старте:

| Модуль | Что даёт |
|---|---|
| `Senku` | senku.one: озвучки Kodik и собственные mp4 до 1080p и выше |
| `Anistar` | anistar.org: плеер AniStar (озвучка, многоголосая, субтитры), поток через прокси Lampac |
| `Animaunt` | animaunt.org: плеер OpenNI, mp4 до 1080p |
| `AnimeFox` | animefox.org: озвучки и серии плеера Kodik, поток через встроенный модуль Kodik |
| `AniTokyo` | anitokyo.tv: озвучки и серии плеера Kodik, поток через встроенный модуль Kodik |
| `Hentasis` | hentasis1.top в разделе 18+; опция `enable_hentasis` |
| `HentaiUkr` | hentaiukr.com в разделе 18+; опция `enable_hentaiukr` |
| `AnimeMonitor` | мониторинг доступности источников |
| `LampacHub` | профили со своими закладками и историей, вход устройств по коду; опция `enable_hub` |

**Мониторинг.** Страница `http://<IP>:9118/animemon`, JSON для
REST-сенсора — `http://<IP>:9118/animemon/status`. Если задать
`monitor_webhook` (например, `http://<IP HA>:8123/api/webhook/anime-sources`),
при каждом падении или восстановлении источника туда уйдёт POST с JSON.
Примеры сенсора и автоматизации, смена зеркал и установка этих модулей в
обычный (не HA) Lampac — в `mods/README.md` репозитория.

**LampacHub.** Включите опцию `enable_hub` и перезапустите аддон. Плагин
`hub.js` добавится в Lampa, которую раздаёт Lampac (`http://<IP>:9118/`),
автоматически. Админка — `http://<IP>:9118/hub`, пароль = `root_password`.
На устройстве: **Настройки → LampacHub → Войти по коду**, код подтверждается
в админке. У каждого профиля свои закладки, история, тайм-коды и настройки
Lampa, а в админке есть раздел «Списки» с категориями Смотрю / Планирую /
Просмотрено / Отложено / Брошено / Пересматриваю / Избранное, переносом между
профилями, экспортом в JSON и сведениями TMDB о сезонах, сериях и выходе новых
серий. Раздел «Импорт» переносит списки Yummy Anime (по нику или файлом
экспорта), Shikimori, MyAnimeList (по нику или файлом экспорта) и AniLiberty
(статусы 1:1, сопоставление с TMDB, очередь ручного сопоставления) и закладки
из резервной копии Lampa / CUB. Anihub не поддерживается: у него нет
открытого API. Подробности — в `mods/README.md`.

## Управление источниками контента (/adminpanel/)

Источников в апстриме больше 70, и их список меняется. Полное управление
ими — через **собственную веб-админку Lampac**:

1. Откройте `http://<IP хоста>:9118/adminpanel/`
2. Войдите паролем из `root_password` (или из лога первого запуска)
3. Включайте и выключайте источники и модули галочками

Изменения из `/adminpanel/` сохраняются в постоянный `init.conf` и не
теряются при перезапуске или обновлении аддона. Аддон при старте только
включает провайдеры из своих опций, поэтому выключенный в админке провайдер,
который есть в `anime_providers`/`online_providers`, снова включится —
уберите его и из опции.

## Где лежат конфиги (persistent)

Постоянная папка конфигурации аддона на хосте — `/addon_configs/local_lampac/`
или `/app_configs/local_lampac/` в зависимости от версии Supervisor (через
Samba share или Studio Code Server):

- `init.conf` — основной конфиг Lampac
- `passwd` — пароль root
- `module/AdminPanel/manifest.json` — состояние встроенной админки
- `database/` — данные пользователей Lampac: закладки (`Sync.sql`),
  позиции просмотра (`TimeCode.sql`), `storage/` и др. Переживают
  обновления аддона и попадают в бэкап Home Assistant

Файлы создаются один раз при первом запуске и дальше только патчатся
аддоном по опциям — ручные правки (в т.ч. из `/adminpanel/`) не теряются.

## Сеть

Порт `9118` проброшен как обычный порт Docker (не через Ingress), потому
что клиенты Lampa (Android TV, браузер и т.д.) обращаются к серверу напрямую
по IP хоста.

## Версии и автообновление

Версию аддона (`version:` в `config.yaml`) руками не меняем — её ставит
GitHub Action `.github/workflows/check-lampac-update.yml` в формате
`ГГГГ.ММ.ДД` (при нескольких сборках за день — `ГГГГ.ММ.ДД.ЧЧММ`).
Он запускается:

- каждый день в 05:00 UTC, если на GHCR вышел новый образ Lampac;
- при каждом push в `main` с изменениями в папке `lampac/`;
- вручную: вкладка **Actions** → **Build and version Lampac add-on** →
  **Run workflow**.

Перед бампом Action собирает образ аддона и запускает его: проверяет, что
Lampac поднялся и модули из `mods/` скомпилировались. Если проверка не
прошла, версия не меняется.

Установка через репозиторий с включённым **Auto update** обновляется сама:
Supervisor видит новую версию и пересобирает аддон. Локальный аддон
(`/addons/lampac/`) — только кнопкой **Rebuild**.

## Технические детали сборки образа

Базовый образ Lampac работает от непривилегированного пользователя, поэтому
`Dockerfile` переключается на `USER root` — это нужно для установки `jq`
через `apt-get` и для `run.sh`, который при каждом старте пишет и симлинкует
файлы внутри `/lampac`.

`jq` ставится через `apt-get` (glibc-сборка). Не копируйте статический `jq`
из Alpine (musl): он не запустится в этом образе
(`cannot execute: required file not found`).
