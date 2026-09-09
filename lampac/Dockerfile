# Официальный образ: ghcr.io/lampac-nextgen/lampac (см. README проекта,
# раздел Docker). Это НЕ docker.io/immisterio/lampac — того места
# распространения образа не существует.
#
# Базовый образ Lampac работает НЕ от root (apt-get внутри него падает
# с Permission denied на /var/lib/apt/lists). Поэтому jq не ставим через
# apt в этом образе, а берём готовый статический бинарник из отдельного
# стейджа на Alpine и просто копируем файл — это не требует прав root
# в целевом образе и не трогает его пользователя/окружение.
FROM alpine:3.20 AS jqbuilder
RUN apk add --no-cache jq

FROM ghcr.io/lampac-nextgen/lampac:latest

# Базовый образ по умолчанию работает от непривилегированного пользователя
# (отсюда Permission denied и на apt, и потенциально на COPY в /). Переходим
# на root на все оставшиеся шаги сборки и на рантайм — run.sh всё равно
# должен иметь право писать/симлинкать файлы внутри /lampac при каждом
# старте, так что root здесь и корректен, и необходим.
USER root

COPY --from=jqbuilder /usr/bin/jq /usr/local/bin/jq

WORKDIR /lampac

# Сохраняем "чистые" init.conf/passwd из образа как дефолты (если они там
# есть), чтобы run.sh мог один раз сгенерировать /config при первом старте.
COPY run.sh /run.sh

RUN chmod +x /run.sh /usr/local/bin/jq \
    && (cp init.conf init.conf.default 2>/dev/null || true) \
    && (cp passwd passwd.default 2>/dev/null || true)

ENTRYPOINT ["/run.sh"]
CMD ["dotnet", "Core.dll"]
