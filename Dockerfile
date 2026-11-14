FROM node:22-bookworm-slim AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/FluxGet.Engine/ src/FluxGet.Engine/
COPY src/FluxGet.Host/ src/FluxGet.Host/
RUN dotnet publish src/FluxGet.Host -c Release -o /app --no-self-contained
COPY --from=web /src/web/dist /app/wwwroot

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG YTDLP_VERSION=2026.10.26
ARG DENO_VERSION=2.9.8
RUN apt-get update \
 && apt-get install -y --no-install-recommends ffmpeg ca-certificates curl unzip \
 && curl -fsSL -o /usr/local/bin/yt-dlp "https://github.com/yt-dlp/yt-dlp/releases/download/${YTDLP_VERSION}/yt-dlp_linux" \
 && chmod 755 /usr/local/bin/yt-dlp \
 && curl -fsSL -o /tmp/deno.zip "https://github.com/denoland/deno/releases/download/v${DENO_VERSION}/deno-x86_64-unknown-linux-gnu.zip" \
 && unzip /tmp/deno.zip -d /usr/local/bin && rm /tmp/deno.zip \
 && apt-get purge -y curl unzip && apt-get autoremove -y && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
RUN mkdir -p /data /downloads && chown -R app:app /data /downloads
USER app
ENV FluxGet__Mode=Hosted \
    FluxGet__Port=8080 \
    FluxGet__DataDirectory=/data \
    FluxGet__DownloadDirectory=/downloads
VOLUME ["/data", "/downloads"]
EXPOSE 8080
ENTRYPOINT ["dotnet", "FluxGet.Host.dll"]
