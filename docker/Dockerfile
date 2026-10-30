FROM node:26-bookworm-slim@sha256:bc0a2afd3b74ce12222fc8bab99271e66257f021078f5f4a5d687a77d280c1d9 AS web
WORKDIR /source/frontend
COPY frontend/package*.json ./
RUN npm ci
COPY frontend/ ./
COPY shared/api/src/generated.ts /source/shared/api/src/generated.ts
RUN npm run build
FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /source
ARG VERSION=0.2.0-alpha.1
COPY Directory.Build.props global.json ./
COPY backend/ backend/
RUN dotnet publish backend/src/Api/Api.csproj -c Release -r linux-x64 --no-self-contained -o /app /p:UseAppHost=false /p:Version=$VERSION /p:RestoreLockedMode=true
COPY --from=web /source/frontend/dist /app/wwwroot
FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee AS runtime-base
USER root
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg curl && rm -rf /var/lib/apt/lists/* && mkdir -p /data && chown app:app /data
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
RUN sh /collect-sources.sh
FROM scratch AS corresponding-source
COPY --from=source-bundle /Container-Corresponding-Source.tar.gz /
FROM runtime-base AS runtime
