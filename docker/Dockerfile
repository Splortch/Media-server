FROM node:24-bookworm-slim AS web
WORKDIR /web
COPY frontend/package*.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build
FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble AS build
WORKDIR /source
COPY Directory.Build.props global.json ./
COPY backend/ backend/
RUN dotnet publish backend/src/Api/Api.csproj -c Release -o /app /p:UseAppHost=false
COPY --from=web /web/dist /app/wwwroot
FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble AS runtime
USER root
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg curl && rm -rf /var/lib/apt/lists/* && mkdir -p /data && chown app:app /data
WORKDIR /app
COPY --from=build --chown=app:app /app ./
USER app
ENV ASPNETCORE_URLS=http://+:5050 MediaServer__DataDirectory=/data MediaServer__AllowedRoots__0=/media
EXPOSE 5050
HEALTHCHECK --interval=30s --timeout=5s CMD curl -fsS http://127.0.0.1:5050/health || exit 1
ENTRYPOINT ["dotnet", "Api.dll"]
