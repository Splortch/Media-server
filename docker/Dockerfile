FROM node:26-bookworm-slim AS web
WORKDIR /web
COPY frontend/package*.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build
FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble AS build
WORKDIR /source
ARG VERSION=0.2.0-alpha.1
COPY Directory.Build.props global.json ./
COPY backend/ backend/
RUN dotnet publish backend/src/Api/Api.csproj -c Release -r linux-x64 --no-self-contained -o /app /p:UseAppHost=false /p:Version=$VERSION /p:RestoreLockedMode=true
COPY --from=web /web/dist /app/wwwroot
FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble AS runtime-base
USER root
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg curl && rm -rf /var/lib/apt/lists/* && mkdir -p /data && chown app:app /data
WORKDIR /app
COPY --from=build --chown=app:app /app ./
COPY --chown=app:app LICENSE NOTICE /app/licenses/
COPY --from=web /web/node_modules/react/LICENSE /app/licenses/react-LICENSE
COPY --from=web /web/node_modules/react-dom/LICENSE /app/licenses/react-dom-LICENSE
COPY --from=web /web/node_modules/scheduler/LICENSE /app/licenses/scheduler-LICENSE
USER app
ENV ASPNETCORE_URLS=http://+:5050 MediaServer__DataDirectory=/data MediaServer__AllowedRoots__0=/media
EXPOSE 5050
HEALTHCHECK --interval=30s --timeout=5s CMD curl -fsS http://127.0.0.1:5050/health || exit 1
ENTRYPOINT ["dotnet", "Api.dll"]

FROM runtime-base AS source-bundle
USER root
COPY docker/collect-sources.sh /collect-sources.sh
RUN sh /collect-sources.sh
FROM scratch AS corresponding-source
COPY --from=source-bundle /Container-Corresponding-Source.tar.gz /
FROM runtime-base AS runtime
