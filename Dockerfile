# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore in its own layer so dependencies are cached between code changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/BookWorm.Contracts/BookWorm.Contracts.csproj src/BookWorm.Contracts/
COPY src/BookWorm.UI/BookWorm.UI.csproj src/BookWorm.UI/
COPY src/BookWorm.Client/BookWorm.Client.csproj src/BookWorm.Client/
COPY src/BookWorm.Server/BookWorm.Server.csproj src/BookWorm.Server/
RUN dotnet restore src/BookWorm.Server/BookWorm.Server.csproj

COPY src/ src/
RUN dotnet publish src/BookWorm.Server/BookWorm.Server.csproj --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# PostgreSQL 18's client tools (pg_dump, pg_restore, psql) make and restore backups. They come from
# PostgreSQL's own package repository, so they always match the database's major version.
RUN set -eux; \
    . /etc/os-release; \
    apt-get update; \
    apt-get install -y --no-install-recommends ca-certificates curl; \
    install -d /usr/share/postgresql-common/pgdg; \
    curl -fsSL -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc https://www.postgresql.org/media/keys/ACCC4CF8.asc; \
    echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt ${VERSION_CODENAME}-pgdg main" \
        > /etc/apt/sources.list.d/pgdg.list; \
    apt-get update; \
    apt-get install -y --no-install-recommends postgresql-client-18; \
    apt-get purge -y --auto-remove curl; \
    rm -rf /var/lib/apt/lists/*; \
    # Book files, covers and notes; and backups. Owned by the app user, so fresh volumes are writable.
    mkdir -p /data /backups; \
    chown "$APP_UID" /data /backups

WORKDIR /app
COPY --from=build /app .

ENV Storage__DataPath=/data \
    Backups__Path=/backups

# Run as the image's unprivileged "app" user.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "BookWorm.Server.dll"]
