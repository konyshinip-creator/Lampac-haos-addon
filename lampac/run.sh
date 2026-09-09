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
ENABLE_TORRSERVER=$(json_get '.enable_torrserver // true')
ENABLE_JACRED=$(json_get '.enable_jacred // true')
ENABLE_SYNC=$(json_get '.enable_sync // true')
ENABLE_TIMECODE=$(json_get '.enable_timecode // true')
ANIME_PROVIDERS=$(json_get '.anime_providers // ""')
EXTRA_JSON=$(json_get '.extra_init_json // ""')

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

# --- Пароль root (WebLog/служебные функции админки) -------------------------
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

# --- Базовые модули (TorrServer/JacRed/Sync/TimeCode включены по умолчанию
#     в самом Lampac — они НЕ в дефолтном SkipModules; здесь мы только
#     добавляем их в SkipModules, если пользователь явно выключил опцию) ----
tmp="$(mktemp)"
jq '(.BaseModule.SkipModules // []) as $skip | $skip' "$CONF_DIR/init.conf" > /dev/null 2>&1 || true

declare -A DISABLE_IF_FALSE=(
  [TorrServer]="$ENABLE_TORRSERVER"
  [JacRed]="$ENABLE_JACRED"
  [Sync]="$ENABLE_SYNC"
  [TimeCode]="$ENABLE_TIMECODE"
)

for name in "${!DISABLE_IF_FALSE[@]}"; do
  val="${DISABLE_IF_FALSE[$name]}"
  tmp="$(mktemp)"
  if [ "$val" = "false" ]; then
    # добавить в SkipModules, если ещё не там
    jq --arg n "$name" \
       '.BaseModule = ((.BaseModule // {}) ) | .BaseModule.SkipModules = (((.BaseModule.SkipModules // []) + [$n]) | unique)' \
       "$CONF_DIR/init.conf" > "$tmp" && mv "$tmp" "$CONF_DIR/init.conf"
  else
    # убрать из SkipModules, если пользователь ранее выключал, а теперь включил обратно
    jq --arg n "$name" \
       '.BaseModule = ((.BaseModule // {})) | .BaseModule.SkipModules = ((.BaseModule.SkipModules // []) - [$n])' \
       "$CONF_DIR/init.conf" > "$tmp" && mv "$tmp" "$CONF_DIR/init.conf"
  fi
done

# --- Аниме-провайдеры: каждый включается своим ключом верхнего уровня
#     в init.conf, например {"AniLibria": {"enable": true}} — формат,
#     задокументированный в README ("Конфигурация провайдеров"). Мы только
#     ВКЛЮЧАЕМ перечисленные в опции, остальные провайдеры не трогаем. ------
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

echo "[lampac-addon] init.conf -> $CONF_DIR/init.conf"
echo "[lampac-addon] Запуск Lampac на порту ${PORT} (TZ=${TIMEZONE}) ..."

cd "$LAMPAC_HOME"
exec "$@"
