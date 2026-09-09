# Lampac add-on для Home Assistant OS

Готовый локальный аддон в папке `lampac/`.

## Установка — вариант A: локальный аддон по SSH/Samba (без git, проще всего)

1. Установите аддон **Samba share** или **SSH & Terminal** / **Studio Code Server**
   из официального Add-on Store, если ещё не установлены.
2. Скопируйте папку `lampac/` (её содержимое: `config.yaml`, `Dockerfile`,
   `run.sh`, `translations/`, `DOCS.md`) в `/addons/lampac/` на HAOS-хосте:
   - через Samba: сетевой путь `\\<IP HAOS>\addons\lampac\`
   - через SSH: `scp -r lampac root@<IP HAOS>:/addons/lampac`
3. В интерфейсе HA: **Settings → Add-ons → Add-on Store → ⋮ → Check for
   updates / Reload**. Внизу списка появится **Local add-ons → Lampac**.
4. Откройте аддон → **Install** → настройте опции на вкладке **Configuration**
   → **Start**.
5. Проверьте `http://<IP HAOS>:9118/version`.

С этим вариантом обновление образа — только вручную: **Settings → Add-ons →
Lampac → Rebuild** (тег `:latest`, так что рескачает свежую сборку Lampac).

## Установка — вариант B: как git-репозиторий + автообновление

Этот вариант даёт настоящую кнопку/переключатель **Auto update** в HA и
избавляет от ручных Rebuild — рекомендуется, если вы хотите "поставил и
забыл".

1. Загрузите **всё содержимое этой папки целиком** (включая `repository.yaml`
   и `.github/workflows/check-lampac-update.yml`) в свой git-репозиторий на
   GitHub.
2. Поправьте `url` в `repository.yaml` на адрес вашего репозитория.
3. Во вкладке репозитория на GitHub включите **Actions**, если они выключены
   по умолчанию (Settings → Actions → General → Allow all actions).
4. В HA: **Settings → Add-ons → Add-on Store → ⋮ → Repositories** → вставьте
   URL репозитория → **Add**. Аддон **Lampac** появится в списке доступных.
5. Установите его как обычный аддон из репозитория, затем на странице аддона
   включите переключатель **Auto update** (значок на карточке аддона или в
   верхней части страницы аддона).

Как это работает дальше: `.github/workflows/check-lampac-update.yml` каждый
день в 05:00 UTC проверяет, не обновился ли образ
`ghcr.io/lampac-nextgen/lampac:latest`. Если да — бампает `version:` в
`lampac/config.yaml` и коммитит это в ваш репозиторий. Supervisor при
следующей регулярной проверке репозиториев увидит новую версию и, благодаря
включённому Auto update, сам пересоберёт и перезапустит аддон — без вашего
участия.

Проверить вручную, что Action работает: на GitHub → вкладка **Actions** →
**Check for new Lampac image** → **Run workflow** (кнопка справа).

## Дальнейшая настройка

Все детали — какие модули включены, где лежат конфиги, что проверить после
первого запуска — в `lampac/DOCS.md` (он же отображается как вкладка
**Documentation** аддона в интерфейсе HA после установки).
