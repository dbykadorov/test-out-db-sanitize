# Образ прогона: воркер, трансформер и Greenmask в одном контейнере.
#
# Они не разнесены по контейнерам намеренно. Greenmask запускает трансформер
# как подпроцесс через `Cmd` и общается с ним по stdin и stdout - между
# контейнерами такого канала нет. Воркер запускает Greenmask, поэтому живёт
# там же. Это одна единица развёртывания «прогон», а не три сервиса.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/ src/

RUN dotnet publish src/Sanitize.Worker/Sanitize.Worker.csproj -c Release -o /out/worker \
 && dotnet publish src/Sanitize.Transformer/Sanitize.Transformer.csproj -c Release -o /out/transformer

FROM mcr.microsoft.com/dotnet/runtime:8.0

# Greenmask - статический бинарник на Go, поэтому переносится копированием,
# а не установкой. Клиент PostgreSQL нужен отдельно: pg_dump и pg_restore
# Greenmask вызывает как внешние программы, и версия у них обязана быть
# не ниже версии сервера, иначе дамп откажется читать новые конструкции схемы.
COPY --from=greenmask/greenmask:latest /usr/bin/greenmask /usr/bin/greenmask

RUN apt-get update \
 && apt-get install -y --no-install-recommends curl ca-certificates gnupg \
 && install -d /usr/share/postgresql-common/pgdg \
 && curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc \
      -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc \
 && echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt bookworm-pgdg main" \
      > /etc/apt/sources.list.d/pgdg.list \
 && apt-get update \
 && apt-get install -y --no-install-recommends postgresql-client-18 \
 && rm -rf /var/lib/apt/lists/*

ENV PATH="/usr/lib/postgresql/18/bin:${PATH}"

COPY --from=build /out/worker      /opt/sanitize/worker/
COPY --from=build /out/transformer /opt/sanitize/transformer/

# Трансформер запускается Greenmask как внешняя программа, поэтому у него
# должен быть простой исполняемый путь без пробелов и версий.
RUN printf '#!/bin/sh\nexec dotnet /opt/sanitize/transformer/Sanitize.Transformer.dll "$@"\n' \
      > /usr/local/bin/sanitize-transformer \
 && chmod +x /usr/local/bin/sanitize-transformer \
 && useradd --create-home --uid 10001 sanitize \
 && mkdir -p /var/lib/sanitize/runs /var/lib/sanitize/published \
 && chown -R sanitize:sanitize /var/lib/sanitize

# Каталоги создаются в образе с нужным владельцем намеренно: Docker переносит
# владельца из образа в новый именованный том при первом монтировании. Иначе
# том создаётся с владельцем root, а прогон идёт под непривилегированным
# пользователем и не может в него писать.
ENV SANITIZE_TRANSFORMER=/usr/local/bin/sanitize-transformer \
    SANITIZE_GREENMASK=/usr/bin/greenmask \
    SANITIZE_WORK_DIR=/var/lib/sanitize/runs \
    SANITIZE_PUBLISH_DIR=/var/lib/sanitize/published

USER sanitize
WORKDIR /var/lib/sanitize

ENTRYPOINT ["dotnet", "/opt/sanitize/worker/Sanitize.Worker.dll"]
CMD ["serve"]
