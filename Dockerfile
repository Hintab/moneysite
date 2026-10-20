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
# The database lives on a volume, not inside the image. Create /data owned by the
# image's non-root "app" user (UID $APP_UID = 1654) so a new named volume starts out writable.
RUN mkdir /data && chown "$APP_UID:$APP_UID" /data
ENV MONEYSITE_DB_PATH=/data/money.db
VOLUME ["/data"]
USER $APP_UID
# The .NET 8 base image already listens on port 8080 (ASPNETCORE_HTTP_PORTS=8080).
EXPOSE 8080
ENTRYPOINT ["dotnet", "MoneySite.dll"]
