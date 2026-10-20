# Build context is the solution folder (the one holding MoneySite.sln):
#   docker compose build      or      docker build -t moneysite .

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
# Restore first so the package layer is cached until the .csproj changes.
COPY MoneySite/MoneySite.csproj MoneySite/
RUN dotnet restore MoneySite/MoneySite.csproj
COPY MoneySite/ MoneySite/
RUN dotnet publish MoneySite/MoneySite.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
COPY --chmod=755 docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
# The database lives on a volume, not inside the image. The entrypoint creates the
# folder, hands it to PUID:PGID (default 99:100, Unraid's nobody:users) and drops
# root before starting the app, so no manual chown is needed.
ENV MONEYSITE_DB_PATH=/data/money.db \
    PUID=99 \
    PGID=100
VOLUME ["/data"]
# Unraid shows this icon and adds a WebUI link even when its template doesn't set them.
LABEL net.unraid.docker.icon="https://raw.githubusercontent.com/Hintab/moneysite/main/MoneySite/wwwroot/icons/icon-512.png" \
      net.unraid.docker.webui="http://[IP]:[PORT:8080]"
# The .NET 8 base image already listens on port 8080 (ASPNETCORE_HTTP_PORTS=8080).
EXPOSE 8080
ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
