FROM node:26-bookworm-slim@sha256:5df79989ed685e09f0a98019db4cebfb22fb9df1a53c0161664a011f7ff6b0f1 AS web
WORKDIR /source/frontend
COPY vendor/picomatch /source/vendor/picomatch
COPY frontend/package*.json ./
RUN npm ci
COPY frontend/ ./
COPY shared/api/src/generated.ts /source/shared/api/src/generated.ts
RUN npm run build
FROM mcr.microsoft.com/dotnet/sdk:10.0.404-noble@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /source
ARG VERSION=0.2.0-alpha.1
COPY Directory.Build.props global.json ./
COPY backend/ backend/
RUN dotnet publish backend/src/Api/Api.csproj -c Release -r linux-x64 --no-self-contained -o /app /p:UseAppHost=false /p:Version=$VERSION /p:RestoreLockedMode=true
COPY --from=web /source/frontend/dist /app/wwwroot
FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-noble@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e AS runtime-base
USER root
RUN apt-get update && apt-get upgrade -y --no-install-recommends && apt-get install -y --no-install-recommends ffmpeg curl && rm -rf /var/lib/apt/lists/* && mkdir -p /data && chown app:app /data
WORKDIR /app
COPY --from=build --chown=app:app /app ./
COPY --chown=app:app LICENSE NOTICE /app/licenses/
COPY --from=web /source/frontend/node_modules/react/LICENSE /app/licenses/react-LICENSE
COPY --from=web /source/frontend/node_modules/react-dom/LICENSE /app/licenses/react-dom-LICENSE
COPY --from=web /source/frontend/node_modules/scheduler/LICENSE /app/licenses/scheduler-LICENSE
COPY --from=web /source/frontend/node_modules/hls.js/LICENSE /app/licenses/hls.js-LICENSE
USER app
ENV ASPNETCORE_URLS=http://+:5050 MediaServer__DataDirectory=/data MediaServer__AllowedRoots__0=/media
EXPOSE 5050
HEALTHCHECK --interval=30s --timeout=5s CMD curl -fsS http://127.0.0.1:5050/health || exit 1
ENTRYPOINT ["dotnet", "Api.dll"]

FROM runtime-base AS source-bundle
USER root
COPY docker/collect-sources.sh /collect-sources.sh
COPY docker/download-sources.sh /download-sources.sh
COPY docker/installed-packages.sh /installed-packages.sh
RUN sh /collect-sources.sh
FROM scratch AS corresponding-source
COPY --from=source-bundle /Container-Corresponding-Source.tar.gz /
FROM runtime-base AS runtime
