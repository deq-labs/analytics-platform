# The whole app in one image: the C# API, which also serves tracker.js and the
# built React dashboard (one origin, so the session cookie just works).
#
#   docker compose --profile app up -d --build      (see docker-compose.yml)
#
# Three stages; only the last one ships. Earlier stages have the build tools
# (Node, the .NET SDK) and are thrown away.

# ---- 1. Build the dashboard (static files) ---------------------------------------
FROM node:26-alpine AS dashboard
WORKDIR /src/dashboard
# Dependencies first: this layer is cached until package*.json change.
COPY dashboard/package.json dashboard/package-lock.json ./
RUN npm ci
COPY dashboard/ ./
RUN npm run build

# ---- 2. Build the API --------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY global.json ./
COPY src/AnalyticsPlatform.Api/AnalyticsPlatform.Api.csproj src/AnalyticsPlatform.Api/
RUN dotnet restore src/AnalyticsPlatform.Api
COPY src/ src/
RUN dotnet publish src/AnalyticsPlatform.Api -c Release -o /app --no-restore

# ---- 3. Runtime: just the ASP.NET Core runtime + our files -----------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=api /app ./
COPY --from=dashboard /src/dashboard/dist ./wwwroot/

# Cookie-encryption keys live on a volume so restarts don't sign everyone out.
# Created here (owned by the non-root app user) so a new volume inherits that.
RUN mkdir -p /data/keys && chown "$APP_UID" /data/keys

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DataProtection__KeysPath=/data/keys \
    GeoIp__DatabasePath=/geoip/GeoLite2-City.mmdb

# The .NET images include a non-root user; never run the app as root.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "AnalyticsPlatform.Api.dll"]
