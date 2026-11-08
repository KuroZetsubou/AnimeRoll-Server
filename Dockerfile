# Build context = this repository. The web client comes from the "web-src" named context
# (AnimeRoll-Web), see docker-compose.yml.
# ---------- Web client ----------
FROM --platform=$BUILDPLATFORM node:22-alpine AS web
WORKDIR /web
COPY --from=web-src package*.json ./
RUN npm ci
COPY --from=web-src . ./
RUN npm run build

# ---------- Server ----------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY src/AnimeRoll.Server.Api/AnimeRoll.Server.Api.csproj AnimeRoll.Server.Api/
COPY src/AnimeRoll.Plugins.Abstractions/AnimeRoll.Plugins.Abstractions.csproj AnimeRoll.Plugins.Abstractions/
RUN dotnet restore AnimeRoll.Server.Api/AnimeRoll.Server.Api.csproj -a $TARGETARCH
COPY src/AnimeRoll.Plugins.Abstractions/ AnimeRoll.Plugins.Abstractions/
COPY src/AnimeRoll.Server.Api/ AnimeRoll.Server.Api/
RUN dotnet publish AnimeRoll.Server.Api/AnimeRoll.Server.Api.csproj -c Release -a $TARGETARCH --no-restore -o /out

# ---------- Runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg ca-certificates curl python3 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out ./
COPY --from=web /web/dist ./wwwroot
ENV ASPNETCORE_ENVIRONMENT=Production \
    AnimeRoll__DataPath=/data \
    AnimeRoll__MediaPath=/media
VOLUME ["/data", "/media", "/downloads", "/plugins"]
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s CMD curl -fs http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "AnimeRoll.Server.Api.dll"]
