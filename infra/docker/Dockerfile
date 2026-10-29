# syntax=docker/dockerfile:1
# EiseChat 服务端镜像：Web 前端 + ASP.NET Core 单进程（REST + WebSocket + 静态前端）
# 构建上下文 = 仓库根目录（见根目录 docker-compose.yml）。
# 网络受限时可用 build-arg 换基础镜像源，例如：
#   docker compose build --build-arg DOTNET_REGISTRY=mcr.m.daocloud.io --build-arg NODE_IMAGE=docker.m.daocloud.io/library/node:24-alpine
ARG DOTNET_REGISTRY=mcr.microsoft.com
ARG NODE_IMAGE=node:24-alpine

# 1) Web 前端构建（Vite 产物输出到 apps/web/dist）
FROM ${NODE_IMAGE} AS web
WORKDIR /src/apps/web
COPY apps/web/package.json apps/web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY apps/web/ ./
RUN npm run build

# 2) 服务端发布：前端产物并入 wwwroot，随程序集一起发布
FROM ${DOTNET_REGISTRY}/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY server/ ./server/
COPY --from=web /src/apps/web/dist ./server/Chat.Server/wwwroot/
RUN dotnet publish server/Chat.Server/Chat.Server.csproj -c Release -o /out -p:UseAppHost=false

# 3) 运行时镜像：官方 ASP.NET Core 运行时，非 root（镜像自带 app 用户，UID 1654）
FROM ${DOTNET_REGISTRY}/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    App__DataDirectory=/data \
    App__FileDirectory=/data/files \
    App__DownloadDirectory=/data/downloads
RUN mkdir -p /data/files /data/downloads && chown -R app:app /data /app
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Chat.Server.dll"]
