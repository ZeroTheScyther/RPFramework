# Multi-stage build for the RPFramework relay server.
# Build context is the repository ROOT (the server references RPFramework.Contracts).
# The Dalamud client (RPFramework/) is intentionally NOT built here.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against just the project files first for better layer caching.
COPY RPFramework.Contracts/RPFramework.Contracts.csproj RPFramework.Contracts/
COPY RPFrameworkServer/RPFrameworkServer.csproj RPFrameworkServer/
RUN dotnet restore RPFrameworkServer/RPFrameworkServer.csproj

# Copy sources and publish.
COPY RPFramework.Contracts/ RPFramework.Contracts/
COPY RPFrameworkServer/ RPFrameworkServer/
RUN dotnet publish RPFrameworkServer/RPFrameworkServer.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Server-side BGM audio pipeline: ffmpeg (transcode to WAV) + yt-dlp standalone binary (no Python needed).
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg ca-certificates curl \
    && curl -L https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux -o /usr/local/bin/yt-dlp \
    && chmod a+rx /usr/local/bin/yt-dlp \
    && apt-get purge -y curl \
    && apt-get autoremove -y \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
# Transcoded WAV cache; mount a volume here so it survives container restarts.
ENV BGM_CACHE_DIR=/data/bgm_cache
EXPOSE 8080
ENTRYPOINT ["dotnet", "RPFrameworkServer.dll"]
