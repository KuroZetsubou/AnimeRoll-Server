# Build context = repository root (AnimeRoll-Home), see docker-compose.yml
# ---------- Web client ----------
FROM --platform=$BUILDPLATFORM node:22-alpine AS web
WORKDIR /web
COPY AnimeRoll.Client/Web/package*.json ./
RUN npm ci
COPY AnimeRoll.Client/Web/ ./
RUN npm run build

# ---------- Server ----------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY AnimeRoll.Server/src/AnimeRoll.Server.Api/AnimeRoll.Server.Api.csproj AnimeRoll.Server.Api/
RUN dotnet restore AnimeRoll.Server.Api/AnimeRoll.Server.Api.csproj -a $TARGETARCH
COPY AnimeRoll.Server/src/AnimeRoll.Server.Api/ AnimeRoll.Server.Api/
RUN dotnet publish AnimeRoll.Server.Api/AnimeRoll.Server.Api.csproj -c Release -a $TARGETARCH --no-restore -o /out

# ---------- Runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg ca-certificates curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out ./
COPY --from=web /web/dist ./wwwroot
ENV ASPNETCORE_ENVIRONMENT=Production \
    AnimeRoll__DataPath=/data \
    AnimeRoll__MediaPath=/media
VOLUME ["/data", "/media", "/downloads", "/tools"]
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s CMD curl -fs http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "AnimeRoll.Server.Api.dll"]
