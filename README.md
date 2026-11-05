# Lampac — Home Assistant OS Add-on

Неофициальный аддон [Home Assistant OS](https://www.home-assistant.io/installation/) для
[Lampac Next Generation](https://github.com/lampac-nextgen/lampac) — backend-сервера
для приложения [Lampa](https://github.com/immisterio/lampa), который собирает ссылки на
фильмы/сериалы/аниме с десятков источников, отдаёт их в виде JSON API и дополнительно
предоставляет TorrServer, синхронизацию закладок, тайм-коды воспроизведения и многое другое.

Этот репозиторий — не часть проекта Lampac NextGen, а обёртка, которая позволяет
запускать его как обычный аддон Supervisor на Home Assistant OS / Supervised,
с настройкой через веб-интерфейс HA вместо ручной правки конфигов.

## Возможности аддона

- Официальный образ `ghcr.io/lampac-nextgen/lampac`, multi-arch (amd64/arm64)
- Персистентный конфиг (не теряется при обновлении аддона)
- Настройка порта, часового пояса и пароля администратора через опции аддона
- **Всё, что работает без настройки, включено сразу**: базовые модули, все
  бесплатные онлайн- и аниме-провайдеры, встроенная админка `/adminpanel/`
- Собственные модули: **Senku**, **AniStar**, **AniMaunt**, **Hentasis** (18+) и
  монитор доступности источников (`/animemon`) — папка [`lampac/mods/`](lampac/mods/)
- Переключатели для 17 базовых модулей Lampac (TorrServer, JacRed, Sync, TimeCode,
  DLNA, Catalog, Tracks, Transcoding, WebLog, CacheMedia, ProxyLimiter,
  ForkPlayerXML, MsxNative, Potok, TelegramAuth, TelegramAuthBot, SeriesNotify,
  QRAuth) — без правки `init.conf`
- Встроенная веб-админка Lampac (`/adminpanel/`) — галочки для всех 70+
  источников контента и модулей
- Версии и автообновление через GitHub Actions: каждая сборка проверяется
  (Lampac поднимается вместе с модулями), версия ставится автоматически в
  формате `ГГГГ.ММ.ДД`; в HA работает переключатель Auto update

## Установка

### Вариант A — быстрый старт (без своего git-репозитория)

1. Скопируйте папку `lampac/` из этого репозитория в `/addons/lampac/` на вашем
   HAOS-хосте (через аддон **Samba share** или по SSH через **Terminal & SSH**).
2. **Settings → Add-ons → Add-on Store → ⋮ (меню) → Check for updates**.
3. Внизу списка появится **Local add-ons → Lampac** — установите его.
4. На вкладке **Configuration** настройте порт/пароль/модули под себя →
   **Start**.
5. Проверьте `http://<IP хоста>:9118/version`.

С этим вариантом обновление образа Lampac — вручную кнопкой **Rebuild**
на странице аддона (тег `:latest`, так что подтянет самую свежую сборку).

### Вариант B — через репозиторий (с автообновлением)

1. **Settings → Add-ons → Add-on Store → ⋮ → Repositories** → добавьте:
   ```
   https://github.com/konyshinip-creator/Lampac-haos-addon
   ```
   (зеркало с тем же содержимым: `https://github.com/infomirlabtest-alt/Lampac-haos-addon-`)
2. Аддон **Lampac** появится в списке доступных для установки — установите
   как обычный аддон.
3. На странице аддона включите переключатель **Auto update**, если хотите,
   чтобы новые сборки Lampac подтягивались сами (подробности в
   [`lampac/DOCS.md`](lampac/DOCS.md#автообновление)).

## Подключение в Lampa

Все провайдеры и модули работают на сервере, в Lampa нужно только подключить
плагины Lampac: **Настройки → Расширения → Добавить плагин**:

| Плагин | Адрес |
|---|---|
| Онлайн (все онлайн- и аниме-провайдеры, Senku, AniStar, AniMaunt) | `http://<IP хоста>:9118/online.js` |
| 18+ (в том числе Hentasis) | `http://<IP хоста>:9118/sisi.js` |

Перезапустите Lampa, откройте карточку и нажмите **Онлайн** — включённые
источники будут в списке. Либо просто откройте `http://<IP хоста>:9118/` —
Lampac отдаёт сборку Lampa, где эти плагины уже подключены.

Если аддон уже был установлен раньше, HA сохранил ваши старые значения
опций. Чтобы включить всё: **Configuration → ⋮ → Reset to defaults** →
**Save** → перезапуск аддона. Подробности — в
[`lampac/DOCS.md`](lampac/DOCS.md#подключение-в-lampa).

## Модули для обычного Lampac (без Home Assistant)

Модули из [`lampac/mods/`](lampac/mods/) работают в любом Lampac NextGen.
Добавьте в `mods/repository.yaml` папки Lampac:

```yaml
- repository: https://github.com/konyshinip-creator/Lampac-haos-addon
  branch: main
  modules:
    - lampac/mods/Senku
    - lampac/mods/Anistar
    - lampac/mods/Animaunt
    - lampac/mods/Hentasis
    - lampac/mods/AnimeMonitor
```

и включите их в `init.conf`:

```json
{
  "Senku": { "enable": true },
  "Anistar": { "enable": true },
  "Animaunt": { "enable": true },
  "Hentasis": { "enable": true },
  "AnimeMonitor": { "enable": true }
}
```

Настройки, зеркала и примеры для Home Assistant — в
[`lampac/mods/README.md`](lampac/mods/README.md).

## Настройка

Полный список опций, что каждая делает и какие у неё дефолты — на вкладке
**Configuration** аддона (подсказки на русском и английском) и подробно в
[`lampac/DOCS.md`](lampac/DOCS.md).

Кратко: базовые модули включаются/выключаются переключателями в опциях
аддона, провайдеры — опциями `anime_providers` и `online_providers`. Полный
список источников контента (их 70+) управляется через встроенную админку
Lampac: `http://<IP хоста>:9118/adminpanel/` (пароль — `root_password`).

## Отказ от ответственности

Этот аддон не аффилирован с командой Lampac NextGen и не является официальным
способом установки. Используйте на свой страх и риск. Всё, что касается
легальности источников контента, которые предоставляет сам Lampac, —
ответственность пользователя в соответствии с законодательством его страны.

## Лицензия

Код обёртки (`Dockerfile`, `run.sh`, `config.yaml` и остальные файлы этого
репозитория) распространяется по лицензии MIT — см. [`LICENSE`](LICENSE).
Сам Lampac NextGen — отдельный проект со своей лицензией, см. его репозиторий.
