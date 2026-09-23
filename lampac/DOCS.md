# Lampac — документация аддона

Backend-сервер [Lampac Next Generation](https://github.com/lampac-nextgen/lampac)
для клиента Lampa. Собран поверх официального образа
**`ghcr.io/lampac-nextgen/lampac`** (GitHub Container Registry, multi-arch:
amd64/arm64).

## Содержание

- [Что уже включено по умолчанию](#что-уже-включено-по-умолчанию)
- [Опции аддона](#опции-аддона)
- [Управление модулями](#управление-модулями)
- [Управление источниками контента (/adminpanel/)](#управление-источниками-контента-adminpanel)
- [Где лежат конфиги](#где-лежат-конфиги-persistent)
- [Сеть](#сеть)
- [Автообновление](#автообновление)
- [Технические детали сборки](#технические-детали-сборки-образа)

## Что уже включено по умолчанию

При первом запуске (без изменения опций) сразу работают:

- **TorrServer, JacRed, Sync, TimeCode** — модули, включённые в апстриме
  "из коробки"
- **Аниме-провайдеры**: AniLiberty, AniLibria, Animevost, AnimeON, AniMedia,
  MoonAnime, Mikai, AnimeLib
- Порт **9118**, часовой пояс **Europe/Kiev**
- Пароль администратора генерируется случайно при первом старте и один раз
  выводится в лог аддона (Log-вкладка) — если не задали свой через
  `root_password`

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
| `enable_dlna` | `true` | Модуль DLNA (включён в апстриме по умолчанию) |
| `enable_catalog` | `false` | Модуль Catalog |
| `enable_tracks` | `false` | Модуль Tracks |
| `enable_transcoding` | `false` | Модуль Transcoding (нужны ресурсы CPU) |
| `enable_weblog` | `false` | Живой просмотр логов запросов на `/weblog` |
| `enable_cachemedia` | `false` | Модуль CacheMedia |
| `enable_proxylimiter` | `false` | Модуль ProxyLimiter |
| `enable_forkplayerxml` | `false` | Модуль ForkPlayerXML |
| `enable_msxnative` | `false` | Модуль MsxNative |
| `enable_telegramauth` | `false` | Модуль TelegramAuth |
| `enable_telegramauthbot` | `false` | Модуль TelegramAuthBot |
| `enable_potok` | `false` | Модуль Potok |
| `enable_admin_panel` | `false` | Встроенная веб-админка Lampac на `/adminpanel/` |
| `anime_providers` | см. выше | Список аниме-источников через запятую |
| `extra_init_json` | *(пусто)* | Произвольный JSON, подмешиваемый в `init.conf` при каждом запуске |

## Управление модулями

15 базовых модулей Lampac включаются/выключаются чекбоксами прямо на
вкладке **Configuration** аддона — никаких конфигов трогать не нужно.
Список и дефолты (включён/выключен) взяты из официального `SkipModules`
апстрима:

| Включены по умолчанию | Выключены по умолчанию |
|---|---|
| TorrServer, JacRed, Sync, TimeCode, DLNA | Catalog, Tracks, Transcoding, WebLog, CacheMedia, ProxyLimiter, ForkPlayerXML, MsxNative, TelegramAuth, TelegramAuthBot, Potok |

Механика: при старте контейнера `run.sh` добавляет/убирает имя модуля из
`BaseModule.SkipModules` в `init.conf`, в соответствии с положением
переключателя. Изменения применяются при следующем перезапуске аддона.

> ⚠️ Модули **DLNA, Tracks, Transcoding, GStreamer, Catalog** не
> экранируют входящие запросы как публичный API (нет встроенной
> аутентификации/лимитов). Если порт 9118 доступен из интернета — держите
> их выключенными или закрывайте firewall'ом/reverse proxy с аутентификацией.

## Управление источниками контента (/adminpanel/)

Источников контента (VOD/аниме/18+) в апстриме больше 70, и их список
регулярно меняется — источники добавляются, переименовываются, отключаются
при блокировках. Вместо того чтобы хардкодить весь список чекбоксами в
этом аддоне (что быстро устареет), для этого используется **собственная
веб-админка Lampac**.

1. Включите опцию `enable_admin_panel` → перезапустите аддон
2. Откройте `http://<IP хоста>:9118/adminpanel/`
3. Войдите паролем из `root_password`
4. Управляйте всеми источниками и модулями чекбоксами — точно так же, как
   при обычной (не-Docker) установке Lampac

Изменения, сделанные через `/adminpanel/`, сохраняются в персистентный конфиг и
не теряются при перезапуске или обновлении аддона.

Восемь аниме-провайдеров, с которых аддон стартовал изначально
(`anime_providers`), — это независимый и более ранний механизм, оставлен
для тех, кто предпочитает включать их одной опцией, не заходя в `/adminpanel/`.

## Где лежат конфиги (persistent)

Файлы монтируются в постоянную папку конфигурации аддона на хосте — видна
как `/addon_configs/local_lampac/` или `/app_configs/local_lampac/` в
зависимости от версии Supervisor (смотрите через Samba share или Studio
Code Server):

- `init.conf` — основной конфиг Lampac
- `passwd` — пароль root
- `module/AdminPanel/manifest.json` — состояние встроенной админки

Эти файлы создаются один раз при первом запуске и дальше только патчатся
аддоном по опциям — ручные правки (в т.ч. сделанные через `/adminpanel/`) не
теряются при перезапуске.

## Сеть

Порт `9118` проброшен как обычный порт Docker (не через Ingress), потому
что клиенты Lampa (Android TV / браузер / MX Player и т.д.) должны
обращаться к серверу напрямую по IP хоста, а не через прокси Home
Assistant.

## Автообновление

Аддон собирается из тега `ghcr.io/lampac-nextgen/lampac:latest` — то есть
любой **Rebuild** уже подтягивает самую свежую сборку Lampac. Как эти
Rebuild-ы запускаются, зависит от способа установки:

- **Локальный аддон** (`/addons/lampac/`) — только вручную, кнопкой
  Rebuild на странице аддона.
- **Git-репозиторий с включённым Auto update** — автоматически.
  `.github/workflows/check-lampac-update.yml` в репозитории каждый день
  в 05:00 UTC проверяет digest образа на GHCR; если он изменился —
  бампает `version:` в `config.yaml` и коммитит. Supervisor видит новую
  версию при обычной проверке репозиториев и с включённым Auto update
  сам пересобирает и перезапускает аддон.

Проверить вручную, что автообновление работает: GitHub → вкладка
**Actions** репозитория → **Check for new Lampac image** → **Run
workflow**.

## Технические детали сборки образа

Базовый образ Lampac по умолчанию работает от непривилегированного
пользователя (в нём даже `apt-get` падает с Permission denied). Поэтому
`Dockerfile` явно переключается на `USER root` в начале — это нужно и для
установки `jq` через `apt-get`, и для рантайма: `run.sh` при каждом
старте контейнера должен иметь право писать/симлинковать файлы внутри
`/lampac`.

`jq` ставится через `apt-get` (glibc-сборка, совместимая с этим
Debian/Ubuntu образом) — не копируйте статический бинарник `jq` из
другого базового образа с другим libc (например Alpine/musl): такой файл
физически не запускается в Debian/Ubuntu-окружении
(`cannot execute: required file not found`, поскольку в системе нет
пути `/lib/ld-musl-x86_64.so.1`, на который он рассчитан).
