# syntax=docker/dockerfile:1
# EriseChat 服务端镜像：Web 前端 + ASP.NET Core 单进程（REST + WebSocket + 静态前端）
# 构建上下文 = 仓库根目录（见根目录 docker-compose.yml）。
# 网络受限时可用 build-arg 换基础镜像源，例如：
#   docker compose build --build-arg DOTNET_REGISTRY=mcr.m.daocloud.io --build-arg NODE_IMAGE=docker.m.daocloud.io/library/node:24-alpine
#
# 镜像瘦身要点：
#   - dotnet publish 指定 linux-x64 RID，只带本平台 native 资产（否则 runtimes/ 会带所有平台，约 +30MB）；
#   - 关闭调试符号、不发布 appsettings.Development.json、只保留英文卫星资源（见 Chat.Server.csproj）；
#   - 运行期以固定 UID/GID（默认 1000，ERISECHAT_UID 可覆盖）非 root 运行，数据卷由 deploy.sh 按同一 UID chown。
ARG DOTNET_REGISTRY=mcr.microsoft.com
ARG NODE_IMAGE=node:24-alpine

# 1) Web 前端构建：服务端同域托管必须用 Web 产物（base '/'，输出到 apps/web/dist/web）
FROM ${NODE_IMAGE} AS web
WORKDIR /src/apps/web
COPY apps/web/package.json apps/web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY apps/web/ ./
RUN npm run build:web

# 2) 服务端发布：前端产物并入 wwwroot，随程序集一起发布
FROM ${DOTNET_REGISTRY}/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY server/ ./server/
COPY --from=web /src/apps/web/dist/web ./server/Chat.Server/wwwroot/
RUN dotnet publish server/Chat.Server/Chat.Server.csproj -c Release -o /out \
      -r linux-x64 --self-contained false \
      -p:UseAppHost=false -p:DebugType=None -p:DebugSymbols=false \
    && rm -f /out/appsettings.Development.json /out/secrets.json

# 3) 运行时镜像：官方 ASP.NET Core 运行时 + 固定 UID 非 root 用户
FROM ${DOTNET_REGISTRY}/dotnet/aspnet:10.0 AS runtime
ARG APP_UID=1000
ARG APP_GID=1000
# 基础镜像（Ubuntu 24.04）自带的 ubuntu 用户已占用 1000:1000；
# 已存在时复用该数字 UID/GID（USER 使用数字，不依赖 erisechat 账号名）。
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && (getent group "${APP_GID}" >/dev/null || groupadd --gid "${APP_GID}" erisechat) \
    && (getent passwd "${APP_UID}" >/dev/null || useradd --uid "${APP_UID}" --gid "${APP_GID}" --no-create-home --shell /usr/sbin/nologin erisechat)
WORKDIR /app
COPY --from=build /out ./
# 数据目录由数据卷挂载覆盖；这里先建好并授权，保证容器在无卷/首次启动时也可写
RUN mkdir -p /data/files /data/downloads && chown -R "${APP_UID}:${APP_GID}" /data /app
ENV ASPNETCORE_URLS=http://0.0.0.0:6580 \
    Kestrel__Endpoints__Http__Url=http://0.0.0.0:6580 \
    ASPNETCORE_ENVIRONMENT=Production \
    App__DataDirectory=/data \
    App__FileDirectory=/data/files \
    App__DownloadDirectory=/data/downloads \
    DOTNET_EnableDiagnostics=0
USER ${APP_UID}:${APP_GID}
EXPOSE 6580
ENTRYPOINT ["dotnet", "Chat.Server.dll"]
