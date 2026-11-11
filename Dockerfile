# syntax=docker/dockerfile:1

# SDK 10.0.103 is the newest published 10.0.1xx tag. Bump this and the
# runtime pin together, deliberately.
FROM mcr.microsoft.com/dotnet/sdk:10.0.103 AS build
WORKDIR /src
COPY src/StreamKeepr/StreamKeepr.csproj src/StreamKeepr/
RUN dotnet restore src/StreamKeepr/StreamKeepr.csproj
COPY src/ src/
RUN dotnet publish src/StreamKeepr/StreamKeepr.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14 AS runtime

# Pin Streamlink, but rebuild regularly: Twitch-side changes are usually fixed
# by a Streamlink release. Override with --build-arg when a bump is needed.
ARG STREAMLINK_VERSION=8.6.3

# python3-venv brings its own pip, so python3-pip is not installed. curl is kept
# for the image healthcheck.
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 python3-venv curl \
    && python3 -m venv /opt/streamlink \
    && /opt/streamlink/bin/pip install --no-cache-dir "streamlink==${STREAMLINK_VERSION}" \
    && rm -rf /var/lib/apt/lists/*

# Static FFmpeg is far smaller than apt's ffmpeg and its dependency tree.
# ffprobe is needed by the MP4 export.
COPY --from=mwader/static-ffmpeg:9.0.2-1 /ffmpeg /ffprobe /usr/local/bin/

ENV PATH="/opt/streamlink/bin:${PATH}" \
    Storage__RecordingsRoot=/recordings

# APP_UID (1654) is defined by the .NET base image. A fresh named volume
# inherits this ownership; a bind mount must be chowned to 1654:1654.
RUN mkdir -p /recordings && chown "$APP_UID:$APP_UID" /recordings

WORKDIR /app
COPY --from=build /app/publish .

USER $APP_UID
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD curl -fsS http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "StreamKeepr.dll"]
