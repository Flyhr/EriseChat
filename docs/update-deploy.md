# EiseChat 更新与自行构建部署指南

> 适用版本：1.1.0+；部署方式：Docker Compose 单容器（见 [docs/deploy.md](deploy.md)）。
> 本文件解决一件事：**你在开发机改了代码，如何自己打源码包 → 上传 → 服务器重新构建部署 → 验证与回滚**。
> 端口约定：应用只用 6500–6600 区间，默认 **6580**（宿主机仅绑定 `127.0.0.1:6580`，可用 `EISECHAT_PORT` 覆盖）。

---

## 0. 一句话流程

```text
开发机（Git 干净）→ git archive 出源码 zip → scp 上传服务器
      → 解压替换 /opt/eisechat/src → sudo bash infra/docker/deploy.sh
      → 健康检查 /download / 登录验证；出问题按 §7 回滚
```

镜像构建会**重新编译 Web 前端与服务端并把 Web 产物打进镜像**，因此改任何前端/服务端代码都走同一流程；服务器上的 `/opt/eisechat/data`（SQLite + 文件 + 安装包）不会被重建过程删除。

---

## 1. 前提

- 服务器已按 [docs/deploy.md](deploy.md) §3 完成首次部署：`/opt/eisechat/{src,data,.env}` 存在，容器 `eisechat-server` 运行中；
- 开发机工作区干净（未提交的改动不会进入源码包）：`git status --short` 无输出；
- 服务器环境（2026-10-29 已核查）：**Ubuntu Server 26.04.1 LTS**、**Docker Engine 29.1.3**、**Docker Compose 2.40.3**、Tailscale 1.104.4、UFW 已启用、DaoCloud 镜像加速生效；`curl` / `python3` 已装，**未装 `unzip`**（§3 用 python3 解压）；
- 权限：若 `docker ps` 报 `permission denied`，先 `sudo usermod -aG docker ggbond` 并重新登录；`sudo` 需要密码，命令前加 `sudo`；
- 配置项（JWT/SMTP/端口）在 `/opt/eisechat/.env`（权限 600），重新部署会**复用**该文件，不会被覆盖；
- 数据升级前先备份（§5）；协议/库表只允许向后兼容增量（AGENTS.md §2.7）。

---

## 2. 开发机出源码包

在仓库根目录（本机 `D:\Code\EriseChat`）：

```bash
git status --short                      # 必须干净
git archive --format=zip -o EiseChat-src-$(git rev-parse --short HEAD).zip HEAD
```

- 包内含：`server/`、`apps/web/`、`apps/desktop/`、`apps/mobile/`、`infra/`、`docker-compose.yml`、`protocol/`、说明文档；
- 不包含：`node_modules/`、`dist/`、`data/`、`secrets.json`、`.env`、`docs/test/` 等（均已 gitignore）；
- 可选：`git archive --format=tar.gz` 同理，服务器用 `tar -xzf` 解压。

---

## 3. 上传并重新构建部署（Windows 开发机，`ssh chat` 免密）

```powershell
# 1) 上传源码包（替换为实际文件名）
scp EiseChat-src-xxxxxxx.zip chat:/home/ggbond/eisechat-src.zip

# 2) 解压到临时目录（服务器无 unzip 时用 python3；有 unzip 可改成 unzip -q -o）
ssh chat "python3 -c \"import zipfile,shutil,os; src='/home/ggbond/eisechat-src.zip'; dst='/opt/eisechat/src.new'; shutil.rmtree(dst, ignore_errors=True); os.makedirs(dst); zipfile.ZipFile(src).extractall(dst); print('extracted ->', dst)\""

# 3) 原子替换 src（旧目录保留一份 src.old 便于排查/回滚）
ssh chat "sudo bash -lc 'set -e; cd /opt/eisechat; rm -rf src.old; mv src src.old; mv src.new src; chown -R root:root src; echo ok'"

# 4) 重新构建并启动（首次/大改可能拉取基础镜像；日常有缓存约 1~3 分钟）
ssh chat "sudo bash /opt/eisechat/src/infra/docker/deploy.sh"
```

`deploy.sh` 会：准备/复用目录与 `.env` → `docker compose up -d --build` → 轮询 `http://127.0.0.1:6580/api/health` 直到就绪 → 打印容器状态与后续步骤。

验证：

```powershell
ssh chat "curl -fsS http://127.0.0.1:6580/api/health"
ssh chat "sudo bash -lc 'cd /opt/eisechat/src && docker compose ps && docker compose logs --tail=50 eisechat'"
```

> 说明：登录态/消息数据在 `/opt/eisechat/data`，重建容器不会丢失；容器内以非 root 用户 `app`(UID 1654) 运行，`deploy.sh` 会确保数据目录可写。

---

## 4. 只改某一端时的差异

| 改动类型 | 做法 |
|---|---|
| Web 前端（`apps/web`） | 走 §2~§3 重新构建（Dockerfile 内会 `npm run build` 并打进镜像） |
| 服务端（`server/`） | 走 §2~§3 重新构建 |
| 协议 / 库表（`protocol/`、EF 迁移） | 走 §2~§3；容器启动自动执行迁移；**升级前先 §5 备份**，只做兼容增量 |
| 桌面 exe / Android APK | 不进 Docker：本地打包后 scp 到 `/opt/eisechat/data/downloads/`，`GET /download` 按文件名规则自动展示最新包 |
| 仅配置（SMTP/JWT/端口） | 编辑 `/opt/eisechat/.env` 后 `docker compose restart eisechat`（无需重建） |

上传新安装包示例：

```powershell
scp EiseChat-Setup-1.1.1.exe chat:/tmp/
ssh chat "sudo bash -lc 'mv /tmp/EiseChat-Setup-1.1.1.exe /opt/eisechat/data/downloads/ && chown 1654:1654 /opt/eisechat/data/downloads/* && ls -l /opt/eisechat/data/downloads/'"
```

---

## 5. 升级前备份（重要）

```powershell
# 1) 每天/升级前跑一次备份脚本（含 SQLite 在线备份 + files 目录，保留策略见脚本）
ssh chat "sudo bash /opt/eisechat/src/infra/backup.sh"

# 2) 备份产物确认
ssh chat "sudo ls -lt /opt/eisechat/data/backups/ | head"
```

- 备份目录：`/opt/eisechat/data/backups/`（或脚本配置的异机/对象存储位置）；
- 恢复步骤见 [docs/deploy.md](deploy.md) §6（恢复演练属于 M6 事项，内测前至少手动演练一次）。

---

## 6. 版本号、端口与配置

| 项 | 怎么改 | 说明 |
|---|---|---|
| 镜像/发布版本 | `APP_VERSION=1.1.1 sudo -E bash infra/docker/deploy.sh` | compose 镜像 tag 与 `.env`/脚本默认值；同时记得同步源码里的版本号（F-27 流程） |
| 服务端口 | `/opt/eisechat/.env` 里 `EISECHAT_PORT=6581`，或部署时 `EISECHAT_PORT=6581 sudo -E bash infra/docker/deploy.sh` | 仅 6500–6600 区间；改完需同步更新 Tailscale Serve/Caddy 反代目标 |
| Tailscale Serve HTTPS | `sudo tailscale serve --bg --https=443 http://127.0.0.1:<端口>` | 查看：`tailscale serve status`；关闭：`tailscale serve reset` |
| SMTP / JWT | 编辑 `/opt/eisechat/.env`（600）后 `docker compose restart eisechat` | 密钥/口令不写入仓库、不打印到日志 |

---

## 7. 回滚

1. **代码回滚**：本地 `git checkout <旧提交>` → 按 §2 出包 → §3 重新部署（镜像重建）；
2. **数据回滚**：`sudo bash -lc 'cd /opt/eisechat/src && docker compose down'` → 用备份覆盖 `/opt/eisechat/data/chat.db` 与 `files/` → `docker compose up -d`；
3. **镜像回滚**：`docker images` 里保留的旧 `eisechat-server:<旧版本>` 可 `docker tag` 回目标版本后 `docker compose up -d`（不 `--build`）；旧源码目录 `src.old` 也可直接换回。

---

## 8. 每次更新后的验证清单

- [ ] `curl -fsS http://127.0.0.1:6580/api/health` 返回 200 且版本正确；
- [ ] `GET /download` 可打开，安装包链接 200（新包已上传时文件名为最新版本）；
- [ ] Web/桌面/手机登录、发消息、收图片/文件正常（至少两个客户端互发一轮）；
- [ ] `docker compose logs --tail=100 eisechat` 无异常堆栈；
- [ ] `ss -lntp | grep 6580` 只绑定在 `127.0.0.1`，公网未暴露；
- [ ] `/opt/eisechat/data` 未丢失（chat.db 存在、文件数量正常）。

---

## 9. 常见问题

| 现象 | 处理 |
|---|---|
| `port is already allocated` | 宿主端口被占：`sudo ss -lntp \| grep 6580` 找到进程，或换 `EISECHAT_PORT` |
| 拉取基础镜像超时 | 用镜像加速：`DOTNET_REGISTRY=mcr.m.daocloud.io NODE_IMAGE=docker.m.daocloud.io/library/node:24-alpine sudo -E bash infra/docker/deploy.sh` |
| 构建后页面仍是旧版 | 清缓存重建：`cd /opt/eisechat/src && sudo docker compose build --no-cache && sudo docker compose up -d` |
| 容器反复重启 | `docker compose logs -f eisechat` 看堆栈；常见为 `.env` 缺 JWT 密钥或数据卷权限 |
| 改了端口但 HTTPS 打不开 | 同步更新 `tailscale serve` 目标后 `tailscale serve status` 复核 |
| 迁移报错/数据异常 | 立即停容器，按 §7 用备份恢复，再排查代码/迁移 |

---

## 10. 服务器目录速查

```text
/opt/eisechat/
├── src/                源码快照（deploy.sh 构建上下文；保留 src.old 供回滚）
├── data/
│   ├── chat.db         SQLite（WAL）
│   ├── files/          聊天图片与文件
│   ├── downloads/      安装包（GET /download 分发）
│   └── backups/        备份产物
└── .env                配置与密钥（600，不入库）
```

> 首次部署、HTTPS 入口、SMTP 邮件通道、管理员提权、备份与恢复的完整步骤见 [docs/deploy.md](deploy.md)。
