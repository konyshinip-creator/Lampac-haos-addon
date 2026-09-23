#!/bin/bash
set -e

OPTIONS_FILE="/data/options.json"
CONF_DIR="/config"
LAMPAC_HOME="/lampac"

mkdir -p "$CONF_DIR"

json_get() {
  jq -r "$1" "$OPTIONS_FILE" 2>/dev/null | sed 's/^null$//'
}

ROOT_PASSWORD=$(json_get '.root_password // ""')
PORT=$(json_get '.port // 9118')
TIMEZONE=$(json_get '.timezone // "Europe/Kiev"')
ANIME_PROVIDERS=$(json_get '.anime_providers // ""')
EXTRA_JSON=$(json_get '.extra_init_json // ""')
ENABLE_ADMIN_PANEL=$(json_get '.enable_admin_panel // false')

[ -z "$PORT" ] && PORT=9118
[ -z "$TIMEZONE" ] && TIMEZONE="Europe/Kiev"
[ -z "$ANIME_PROVIDERS" ] && ANIME_PROVIDERS="AniLiberty,AniLibria,Animevost,AnimeON,AniMedia,MoonAnime,Mikai,AnimeLib"

ln -snf "/usr/share/zoneinfo/$TIMEZONE" /etc/localtime 2>/dev/null || true
export TZ="$TIMEZONE"

# ---------------------------------------------------------------------------
# init.conf — создаётся один раз в /config (persistent), дальше только
# патчится по опциям аддона; ручные правки пользователя не теряются.
# ---------------------------------------------------------------------------
if [ ! -f "$CONF_DIR/init.conf" ]; then
  if [ -f "$LAMPAC_HOME/init.conf.default" ]; then
    cp "$LAMPAC_HOME/init.conf.default" "$CONF_DIR/init.conf"
  else
    echo '{}' > "$CONF_DIR/init.conf"
  fi
fi

tmp="$(mktemp)"
jq --argjson port "$PORT" '.listen.port = $port' "$CONF_DIR/init.conf" > "$tmp" && mv "$tmp" "$CONF_DIR/init.conf"

# --- Пароль root (WebLog/AdminPanel/служебные функции) ----------------------
if [ -n "$ROOT_PASSWORD" ]; then
  printf '%s' "$ROOT_PASSWORD" > "$CONF_DIR/passwd"
elif [ ! -f "$CONF_DIR/passwd" ]; then
  if [ -f "$LAMPAC_HOME/passwd.default" ]; then
    cp "$LAMPAC_HOME/passwd.default" "$CONF_DIR/passwd"
  else
    GENERATED="$(tr -dc 'A-Za-z0-9' </dev/urandom | head -c 20)"
    printf '%s' "$GENERATED" > "$CONF_DIR/passwd"
    echo "[lampac-addon] Пароль root не задан в опциях — сгенерирован автоматически: $GENERATED"
    echo "[lampac-addon] Он сохранён в $CONF_DIR/passwd. Задайте свой через опцию root_password, если хотите заменить."
  fi
fi

# --- Базовые модули: управляются через BaseModule.SkipModules в init.conf --
# Список и дефолты (true = уже включён в апстриме, false = уже выключен
# в апстриме) взяты из официального README проекта. Опция false у модуля,
# включённого по умолчанию, ДОБАВЛЯЕТ его в SkipModules; опция true у
# модуля, выключенного по умолчанию, УБИРАЕТ его из SkipModules.
declare -A MODULE_OPTION=(
  [TorrServer]="enable_torrserver"
  [JacRed]="enable_jacred"
  [Sync]="enable_sync"
  [TimeCode]="enable_timecode"
  [DLNA]="enable_dlna"
  [Catalog]="enable_catalog"
  [Tracks]="enable_tracks"
  [Transcoding]="enable_transcoding"
  [WebLog]="enable_weblog"
  [CacheMedia]="enable_cachemedia"
  [ProxyLimiter]="enable_proxylimiter"
  [ForkPlayerXML]="enable_forkplayerxml"
  [MsxNative]="enable_msxnative"
  [TelegramAuth]="enable_telegramauth"
  [TelegramAuthBot]="enable_telegramauthbot"
)

for name in "${!MODULE_OPTION[@]}"; do
  opt_key="${MODULE_OPTION[$name]}"
  val=$(json_get ".${opt_key} // empty")
  [ -z "$val" ] && continue   # опция отсутствует в options.json -> не трогаем
  tmp="$(mktemp)"
  if [ "$val" = "true" ]; then
    jq --arg n "$name" \
       '.BaseModule = ((.BaseModule // {})) | .BaseModule.SkipModules = ((.BaseModule.SkipModules // []) - [$n])' \
       "$CONF_DIR/init.conf" > "$tmp" && mv "$tmp" "$CONF_DIR/init.conf"
  else
    jq --arg n "$name" \
       '.BaseModule = ((.BaseModule // {})) | .BaseModule.SkipModules = (((.BaseModule.SkipModules // []) + [$n]) | unique)' \
       "$CONF_DIR/init.conf" > "$tmp" && mv "$tmp" "$CONF_DIR/init.conf"
  fi
done

# --- Аниме-провайдеры: каждый включается своим ключом верхнего уровня
#     в init.conf, например {"AniLibria": {"enable": true}} — формат из
#     README ("Конфигурация провайдеров"). Мы только ВКЛЮЧАЕМ перечисленные
#     в опции, остальные провайдеры не трогаем. -----------------------------
IFS=',' read -ra WANTED <<< "$ANIME_PROVIDERS"
for w in "${WANTED[@]}"; do
  name="$(echo "$w" | xargs)"
  [ -z "$name" ] && continue
  tmp="$(mktemp)"
  jq --arg name "$name" \
     '.[$name] = ((.[$name] // {}) + {enable: true})' \
     "$CONF_DIR/init.conf" > "$tmp" && mv "$tmp" "$CONF_DIR/init.conf"
done

# --- Произвольные overrides (глубокое слияние, приоритет у EXTRA_JSON) ------
if [ -n "$EXTRA_JSON" ]; then
  if echo "$EXTRA_JSON" | jq empty 2>/dev/null; then
    tmp="$(mktemp)"
    jq -s '.[0] * .[1]' "$CONF_DIR/init.conf" <(echo "$EXTRA_JSON") > "$tmp" && mv "$tmp" "$CONF_DIR/init.conf"
  else
    echo "[lampac-addon] ВНИМАНИЕ: extra_init_json содержит невалидный JSON, пропускаю"
  fi
fi

ln -snf "$CONF_DIR/init.conf" "$LAMPAC_HOME/init.conf"
ln -snf "$CONF_DIR/passwd" "$LAMPAC_HOME/passwd"

# ---------------------------------------------------------------------------
# AdminPanel — отдельный механизм (manifest.json в каталоге модуля, а не
# SkipModules). Включает встроенную веб-админку Lampac на /admin, где
# доступно управление ПОЛНЫМ списком источников (70+) и модулей — то, что
# в рамках этого аддона мы намеренно не дублируем чекбоксами, чтобы не
# зависеть от устаревающего списка имён провайдеров.
# ---------------------------------------------------------------------------
mkdir -p "$CONF_DIR/module/AdminPanel" "$LAMPAC_HOME/module/AdminPanel"
if [ ! -f "$CONF_DIR/module/AdminPanel/manifest.json" ]; then
  echo '{"enable": false}' > "$CONF_DIR/module/AdminPanel/manifest.json"
fi
tmp="$(mktemp)"
jq --argjson en "$ENABLE_ADMIN_PANEL" '.enable = $en' \
   "$CONF_DIR/module/AdminPanel/manifest.json" > "$tmp" && mv "$tmp" "$CONF_DIR/module/AdminPanel/manifest.json"
ln -snf "$CONF_DIR/module/AdminPanel/manifest.json" "$LAMPAC_HOME/module/AdminPanel/manifest.json"

echo "[lampac-addon] init.conf -> $CONF_DIR/init.conf"
if [ "$ENABLE_ADMIN_PANEL" = "true" ]; then
  echo "[lampac-addon] AdminPanel включён -> http://<IP>:${PORT}/admin (пароль = root_password)"
fi
echo "[lampac-addon] Запуск Lampac на порту ${PORT} (TZ=${TIMEZONE}) ..."

cd "$LAMPAC_HOME"
exec "$@"
