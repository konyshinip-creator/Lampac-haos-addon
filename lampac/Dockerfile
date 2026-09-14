# Официальный образ: ghcr.io/lampac-nextgen/lampac (см. README проекта,
# раздел Docker). Это НЕ docker.io/immisterio/lampac — того места
# распространения образа не существует.
FROM ghcr.io/lampac-nextgen/lampac:latest

# Базовый образ по умолчанию работает от непривилегированного пользователя
# (apt-get и запись в /lampac от него падают с Permission denied).
# Переходим на root на всё оставшееся: и на сборку (нужно ставить пакет),
# и на рантайм (run.sh должен иметь право писать/симлинковать файлы внутри
# /lampac при каждом старте).
USER root

# ВАЖНО: jq ставим отсюда, через apt — то есть glibc-сборкой, совместимой
# с этим (Debian/Ubuntu) образом. Более ранняя версия копировала статический
# jq из alpine (musl libc) — он физически не может запуститься в
# glibc-окружении ("cannot execute: required file not found", т.к. нет
# /lib/ld-musl-x86_64.so.1). Никогда не смешивайте musl- и glibc-бинарники
# между базовыми образами.
RUN apt-get update \
    && apt-get install -y --no-install-recommends jq \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /lampac

# Сохраняем "чистые" init.conf/passwd из образа как дефолты (если они там
# есть), чтобы run.sh мог один раз сгенерировать /config при первом старте.
COPY run.sh /run.sh
RUN chmod +x /run.sh \
    && (cp init.conf init.conf.default 2>/dev/null || true) \
    && (cp passwd passwd.default 2>/dev/null || true)

ENTRYPOINT ["/run.sh"]
CMD ["dotnet", "Core.dll"]
