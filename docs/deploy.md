# EriseChat 服务端部署说明（Docker Compose 一键部署）

> 适用版本：1.1.0+。目标环境：**Ubuntu 22.04 / 24.04 / 26.04**（云服务器 2C4G 轻量即可），宿主机只需 **Docker + Docker Compose**。
> **已核查的服务器环境（2026-10-29，可直接照做）**：Ubuntu Server **26.04.1 LTS**（kernel 7.0.0-38-generic）、Docker Engine **29.1.3**、Docker Compose **2.40.3**（compose v2 插件）、Tailscale **1.104.4**、UFW 已启用、DaoCloud 镜像加速已配置并通过 `hello-world` 拉取验证；22.04 / 24.04 同样适用。
> 服务端为**单进程**（ASP.NET Core，Kestrel 在容器内监听 6580，宿主机只绑定 `127.0.0.1:6580`）；
> 数据落容器挂载卷（SQLite + 文件目录），镜像内置 Web 前端与 `GET /download` 下载页。
> 部署方式见 AGENTS.md §2.4：**Docker Compose 单容器一键部署**（不引入多容器编排）。

---

## 1. 部署架构

```
客户端（Web / Windows exe / Android APK）
   │  HTTPS（Tailscale Serve 内测）或 宿主 Caddy（公网域名）
   ▼
宿主机 127.0.0.1:6580  ──>  docker 容器 erisechat-server（ASP.NET Core 单进程）
                              ├─ /data/chat.db      SQLite（WAL，容器外 /opt/erisechat/data/chat.db）
                              ├─ /data/files/       聊天图片/文件
                              └─ /data/downloads/   安装包（GET /download 分发）
/opt/erisechat/              部署根目录
  ├── src/                  源码（git 仓库快照，供 docker compose 构建镜像）
  ├── data/                 持久化数据（chat.db / files / downloads）
  └── .env                  配置与密钥（JWT、SMTP，权限 600，不入库）
```

- 公网只开放 22（SSH，建议限来源 IP）与 HTTPS 端口；**6580 不对公网开放**。
- 容器镜像内包含：Web 前端（Vite 构建）、服务端、`wwwroot`、`/download` 页。
- 备份、恢复、邮件通道等运维操作见 §5~§7。

### 1.1 端口分配（项目约定 6500–6600）

| 用途 | 端口 | 绑定/说明 |
|---|---|---|
| 容器内应用（Kestrel） | **6580** | 容器网络内监听 `0.0.0.0:6580`，不直接暴露 |
| 宿主回环映射（唯一服务端口） | `127.0.0.1:6580` | 由 Tailscale Serve / Caddy 反代；可用 `ERISECHAT_PORT` 覆盖 |
| HTTPS 入口 | 443 | Tailscale Serve（内测）或宿主 Caddy（公网） |
| SSH | 22 | 服务器管理，建议限制来源 IP |
| Web 开发服务器 | 5173 | Vite，仅开发机 |
| Vite 预览 | 4173 | `vite preview`，仅开发机 |

- 约定：本项目自有服务端口统一放在 **6500–6600** 区间，默认 **6580**；同机多实例依次用 6581/6582…（改 `ERISECHAT_PORT`）。
- 容器内部与宿主映射用**同一个端口号**，避免“宿主在区间内、容器里又是另一个端口”的混淆。
- 6500–6600 之外只使用标准端口（22/80/443）与开发工具端口（5173/4173）。

---

## 2. 服务器准备

> **已核查的服务器环境（2026-10-29，可直接照做）**：Ubuntu Server **26.04.1 LTS**（kernel 7.0.0-38-generic）、Docker Engine **29.1.3**、Docker Compose **2.40.3**（compose v2 插件）、Tailscale **1.104.4**、UFW 已启用、DaoCloud 镜像加速已配置并通过 `hello-world` 拉取验证；22.04 / 24.04 同样适用。

| 组件 | 实际状态 | 说明 |
|---|---|---|
| 操作系统 | Ubuntu Server **26.04.1 LTS**（kernel 7.0.0-38-generic） | 22.04 / 24.04 同样适用 |
| Docker Engine | **29.1.3**（Ubuntu 包 `29.1.3-0ubuntu4.1`） | 已安装，`hello-world` 拉取/运行通过 |
| Docker Compose | **2.40.3**（`2.40.3+ds1-0ubuntu1`，compose v2 插件） | 已安装，无需另装 |
| 镜像加速 | DaoCloud（Docker Hub 加速已生效） | 只加速 Docker Hub；MCR 基础镜像见 §10.4 |
| Tailscale | **1.104.4**，节点 `chat-server`（100.115.90.9） | 内测 HTTPS 入口（§3.4） |
| UFW | 已启用 | 仅放行 22；部署后由 Tailscale Serve / Caddy 回源本机 6580 |
| 硬件 | 4 核 / 14G 内存 / 468G 磁盘（已用 7.8G） | 2C4G 轻量规格即可 |
| 端口占用 | 仅 22 监听；80 / 443 / 6580 未占用 | 部署后只绑 `127.0.0.1:6580` |

> 若 `docker ps` 报 `permission denied`，说明当前用户不在 docker 组：`sudo usermod -aG docker ggbond`（执行后**重新登录**生效）；本机 `sudo` 需要密码，命令前加 `sudo`。
> 服务器**未安装 `unzip`**：解压源码包用 `python3`（§3.1）或 `tar`。

以下为从零开始的完整步骤，已具备的可跳过。

1. 云服务器安装 **Ubuntu 22.04 / 24.04 / 26.04**，2C4G 起（峰值 ≤100 在线）。
2. 安全组入站：`22`（建议限制来源 IP）、`80`、`443`；其余拒绝。
3. 安装 Docker Engine 与 Compose 插件（二选一）：

```bash
# 方式 A：官方脚本（最快）
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker "$USER"        # 之后可免 sudo 管理 docker（需重新登录生效）

# 方式 B：发行版仓库
sudo apt-get update && sudo apt-get install -y docker.io docker-compose-v2
sudo usermod -aG docker "$USER"
```

4. 校验：

```bash
docker version
docker compose version
docker run --rm hello-world          # 能拉取并运行即网络正常（国内可配置镜像加速）
```

5. 基础工具（备份/提权/核查用，建议安装）：`sudo apt-get install -y sqlite3 rsync`（`curl` 已自带；`sqlite3` 用于 §3.5 管理员提权与 §6.1 备份）。

> 国内网络若拉取 Docker Hub / MCR 镜像慢，可通过 `/etc/docker/daemon.json` 配置镜像加速（如 DaoCloud），
> 或用构建参数换基础镜像源（见 §10.4）。

---

## 3. 一键部署

### 3.1 更新服务器代码：出包 → 上传 → 解压（三步缺一不可）

> **`deploy.sh` 不会解压 `~/erisechat-src.zip`**，它只构建 `/opt/erisechat/src` 中已有的源码。只上传 ZIP 就直接执行部署脚本，会继续使用旧代码。

Docker 构建需要完整源码（含 `server/`、`apps/web/`）。**源码包名固定**，后续更新命令不再随提交号变化。

#### 3.1.1 出源码包（Windows 用 Git Bash）

**打包前提：工作区必须完全干净**（`git status --short` 无任何输出，包含未跟踪文件、测试改动与临时文件）；脚本检测到脏工作区会直接拒绝打包。测试文件统一放根级 `tests/`（已 gitignore），不要留在 `apps/`、`server/` 源码目录里。

```powershell
cd D:\Code\EriseChat
& 'D:\Git\bin\bash.exe' infra/package-src.sh
Get-FileHash .\dist\erisechat-src.zip -Algorithm SHA256
```

- 必须用 Git Bash（或完整路径调用 `D:\Git\bin\bash.exe`）；cmd 里的 `bash` 是 WSL 转接器，会报 `execvpe(/bin/bash) failed: No such file or directory`。
- 产物固定为 `dist/erisechat-src.zip`（`dist/` 已 gitignore）；Linux/macOS 可直接 `bash infra/package-src.sh`。
- `infra/package-src.sh` = `git archive HEAD` + LF 行尾自检（`.sh`/`Dockerfile`/compose 混入 CRLF 会直接失败）；需要其他输出路径时：`bash infra/package-src.sh /tmp/erisechat-src.zip`。

#### 3.1.2 上传（Windows 用 PowerShell，不要用 Git Bash）

Git Bash 默认不会复用 Windows 的 ssh-agent；私钥带口令时会连续提示 `Enter passphrase`，最终 `Permission denied (publickey)`。上传与 SSH 统一用 **Windows PowerShell**：

```powershell
cd D:\Code\EriseChat
scp .\dist\erisechat-src.zip chat:~/erisechat-src.zip
```

`chat` 为 `~/.ssh/config` 中的服务器别名（也可直接用 Tailscale IP，如 `ggbond@100.115.90.9`）。

#### 3.1.3 登录服务器并校验压缩包

```powershell
ssh chat
```

进入 Ubuntu 后（提示符应为 `ggbond@chat-server:~$`）：

```bash
ls -lh ~/erisechat-src.zip
sha256sum ~/erisechat-src.zip    # 与 3.1.1 本地 Get-FileHash 对比
```

#### 3.1.4 解压并原子替换源码（必做）

服务器无 `unzip` 时用 `python3`；旧源码保留为 `src.old` 便于排查/回滚。**以下命令固定不变**：

```bash
sudo bash -c 'set -e
  cd /opt/erisechat
  rm -rf src.new && mkdir -p src.new
  python3 -c "import zipfile; zipfile.ZipFile(\"/home/ggbond/erisechat-src.zip\").extractall(\"src.new\")"
  rm -rf src.old
  if [ -d src ]; then mv src src.old; fi
  mv src.new src
  chown -R root:root src
  echo "src 已更新"'
```

解压后核对版本，确认不是旧源码：

```bash
grep -m1 '版本' /opt/erisechat/src/AGENTS.md
ls /opt/erisechat/src/server/Chat.Server | head
```

> 服务器已安装 `unzip` 时可用：`sudo rm -rf /opt/erisechat/src.new && sudo mkdir -p /opt/erisechat/src.new && sudo unzip -o ~/erisechat-src.zip -d /opt/erisechat/src.new`，再按上面的 `mv`/`chown` 步骤替换。
>
> `tar.gz` 替代方案（不推荐，包名/命令需另改）：`git archive --format=tar.gz -o /tmp/erisechat-src.tar.gz HEAD` → `sudo tar -xzf /tmp/erisechat-src.tar.gz -C /opt/erisechat/src`。

#### 3.1.5 执行部署

必须在 **Ubuntu SSH 终端**里执行，不要在 Windows 的 `PS D:\Code\EriseChat>` 下执行（Windows 会提示「已在此计算机上禁用 Sudo」）：

```bash
sudo bash /opt/erisechat/src/infra/docker/deploy.sh
```

已登录过服务器时，也可从 PowerShell 一步远程执行（前提：3.1.4 已完成）：

```powershell
ssh -t chat "sudo bash /opt/erisechat/src/infra/docker/deploy.sh"
```

### 3.2 执行一键部署脚本

先确认 §3.1.4 已完成（ZIP 已解压替换源码）；否则脚本只会用旧代码重新构建镜像。

```bash
sudo bash /opt/erisechat/src/infra/docker/deploy.sh
```

脚本会：
1. 创建 `/opt/erisechat/{data,data/files,data/downloads}` 并按容器内非 root 用户（默认 UID/GID 1000，可用 `ERISECHAT_UID` 覆盖）授权；
2. 首次部署自动生成 `/opt/erisechat/.env`（随机 JWT 密钥，权限 600；SMTP 项待填）；
3. `docker compose up -d --build` 构建镜像（Node 构建 Web → .NET 发布 → ASP.NET Core 运行时镜像）并启动；
4. 等待 `http://127.0.0.1:6580/api/health` 通过后打印容器状态。

首次构建需拉取 `node:24-alpine`、`dotnet/sdk:10.0`、`dotnet/aspnet:10.0`，耗时取决于网络。
镜像已做瘦身：`linux-x64` 单平台发布、不发布调试符号与开发配置、只保留英文卫星资源；运行用户固定为 UID/GID 1000（`ERISECHAT_UID` 可覆盖），数据卷由脚本按同一 UID 授权。

可选环境变量：

```bash
sudo APP_VERSION=1.1.0 bash /opt/erisechat/src/infra/docker/deploy.sh          # 镜像 tag/版本
sudo DOTNET_REGISTRY=mcr.m.daocloud.io bash /opt/erisechat/src/infra/docker/deploy.sh   # 基础镜像镜像源
sudo SKIP_BUILD=1 bash /opt/erisechat/src/infra/docker/deploy.sh              # 只重启，不重建
```

### 3.3 验证

```bash
# 1) 健康检查（数据库探活失败返回 503，curl -f 会直接判定失败）
curl -fsS http://127.0.0.1:6580/api/health
# {"status":"ok","serverTime":...,"version":"1.2.0","protocolVersion":1,"db":"ok"}

# 2) 版本/构建核对：新代码应返回 200；404 说明仍是旧镜像（2026-11-02 之前的构建）
curl -s http://127.0.0.1:6580/api/version

# 3) 容器与镜像时间，确认是本次重建而不是复用旧镜像
docker compose -f /opt/erisechat/src/docker-compose.yml ps      # STATUS 应为 healthy
docker inspect erisechat-server --format '{{.Created}}'
docker compose -f /opt/erisechat/src/docker-compose.yml images

# 4) 端口与日志
sudo ss -ltnp | grep 6580                                      # 必须只有 127.0.0.1:6580
sudo docker logs --tail=30 erisechat-server
```

> 若 `docker inspect` 的创建时间远早于本次部署，说明用了 `SKIP_BUILD=1` 或源码未替换（回到 §3.1.4 / §3.2）；`/api/version` 返回 404 是“旧镜像”的明确信号。
> `/api/health` 会执行 SQLite `SELECT 1`：数据库可用返回 200、`db: ok`；探活失败返回 **503**、`status: degraded` / `db: error`，诊断字段保持不变。`docker-compose.yml` 的 healthcheck、`infra/docker/deploy.sh`、`infra/restore.sh` 都用 `curl -f` 判定，因此数据库故障不会再被当作健康实例上线或恢复成功。
> 2026-10-29 首次一键部署实测通过：`docker compose ps` 中 `erisechat-server` 为 `healthy`，`/api/health` 返回 `{"status":"ok","version":"1.1.0","protocolVersion":1,"db":"ok"}`。

### 3.4 配置 HTTPS：Tailscale Serve（内测推荐，无需域名）

服务器已加入 Tailscale 时（内测阶段推荐）：

```bash
# 一次性：允许当前普通用户管理 serve（之后 tailscale serve 不用 sudo）
sudo tailscale set --operator="$USER"

# 把 tailnet 的 https://<host>.<tailnet>.ts.net/ 反代到本机 6580（后台常驻）
tailscale serve --bg --https=443 http://127.0.0.1:6580
tailscale serve status
```

- 访问地址形如 `https://chat-server.tail8c07f5.ts.net/`（以 `tailscale status --json` 的 `Self.DNSName` 为准）。
- 客户端（Windows exe / Android APK）需加入**同一个 tailnet** 才能访问。
- 首次使用需在 Tailscale 管理台开启 **HTTPS Certificates**，否则无法签发证书。
- 之后若换公网域名，可在宿主机装 Caddy 反代 `127.0.0.1:6580`（见 §3.6）。

### 3.5 管理员提权（v1 无后台提权界面）

```bash
sudo sqlite3 /opt/erisechat/data/chat.db "UPDATE users SET role='admin' WHERE email='<你的邮箱>';"
```

登录 Web 端后可在 `/admin` 查看用户与审计日志。

### 3.6 可选：宿主 Caddy 提供公网 HTTPS

```bash
# 安装 Caddy 后，/etc/caddy/Caddyfile 示例（域名指向本机公网 IP）
chat.example.com {
    encode gzip
    reverse_proxy 127.0.0.1:6580
}
sudo systemctl reload caddy
```

---

## 4. 配置项（/opt/erisechat/.env）

配置读取顺序：**secrets.json > 环境变量 > appsettings.{Environment}.json > appsettings.json**。
Docker 部署统一用 `/opt/erisechat/.env` 注入环境变量（该文件权限 600、不入库）：

| 变量 | 说明 |
|---|---|
| `Jwt__Key` | JWT 签名密钥（HS256，≥32 字节随机值；deploy.sh 已自动生成） |
| `Jwt__ExpireDays` | JWT 有效期（天），默认 3；到期后需重新登录，可按需覆盖（未设置时读 appsettings 默认值） |
| `Email__SmtpHost` / `Email__SmtpPort` / `Email__SmtpUseSsl` | SMTP 服务器（推荐 `smtp.qq.com:587` + STARTTLS；465 隐式 TLS 不被 `System.Net.Mail` 支持，会超时） |
| `Email__SmtpUser` / `Email__SmtpPassword` | SMTP 账号与授权码（密码只放 .env） |
| `Email__FromAddress` / `Email__FromName` | 发件人地址/显示名 |
| `App__UploadMaxBytes` | 附件上限（默认 52428800 = 50MB） |
| `App__RegistrationEnabled` / `App__AllowedEmailDomains` | 是否开放注册 / 允许邮箱域名白名单（空=不限） |
| `App__ProtocolVersion` | 协议版本（保持 1） |
| `App__MaxConnectionsPerUser` | 每账号最大同时在线连接数（默认 3，限制 1~10；超限踢最早连接，F-22） |

修改后重新执行部署脚本生效（**不要用 `docker compose restart`**，它不会重读 `.env`）：

```bash
sudo SKIP_BUILD=1 bash /opt/erisechat/src/infra/docker/deploy.sh
```

> 容器内数据目录已由 compose 固定为 `/data`（`App__DataDirectory=/data`），无需改动；
> 若确要改路径，同步改 compose 的 `volumes` 与 `environment` 后重建。

---

## 5. 邮件通道（**上线前必须配置**）

生产环境（`Production`）使用真实 SMTP 发信；未配置时验证码发送会返回 500 并提示「SMTP 邮件通道未配置」。

1. 准备邮箱（QQ 邮箱/企业邮箱均可，QQ 邮箱需开启 SMTP 并使用**授权码**）；宿主安装 `sqlite3`（备份脚本需要）：`sudo apt-get install -y sqlite3`。
2. 编辑 `/opt/erisechat/.env`，取消注释并填入：

```bash
sudo nano /opt/erisechat/.env
```

```ini
Email__SmtpHost=smtp.qq.com
Email__SmtpPort=587
Email__SmtpUseSsl=true
Email__SmtpUser=你的邮箱@qq.com
Email__SmtpPassword=你的授权码
Email__FromAddress=你的邮箱@qq.com
Email__FromName=EriseChat
```

> 端口必须用 **587**（STARTTLS）。465 是隐式 TLS（连上即握手），.NET 的 `System.Net.Mail` 不支持，会报 `The operation has timed out`。

3. 重新执行部署脚本并验证：

```bash
sudo SKIP_BUILD=1 bash /opt/erisechat/src/infra/docker/deploy.sh
curl -s -X POST http://127.0.0.1:6580/api/auth/email/send \
  -H 'Content-Type: application/json' -d '{"email":"你的邮箱@qq.com","purpose":"Register"}'
# 返回 200 且邮箱收到 6 位验证码；失败时看 docker logs --tail=100 erisechat-server
```

> 开发/本地调试用 `Development` 环境的控制台输出与固定测试码，不影响生产。

---

## 6. 备份与恢复（**必须演练一次**）

数据目录是宿主机的 `/opt/erisechat/data`，`infra/backup.sh` 在宿主机执行即可（容器运行中安全）：

### 6.1 每日备份

```bash
sudo install -m 755 /opt/erisechat/src/infra/backup.sh /usr/local/bin/erisechat-backup.sh
# 立即跑一次
sudo ERISECHAT_DATA_DIR=/opt/erisechat/data ERISECHAT_BACKUP_DIR=/var/backups/erisechat /usr/local/bin/erisechat-backup.sh
sudo ls -lh /var/backups/erisechat

# 每天 02:00 备份（可选 ERISECHAT_REMOTE=user@host:/path 同步到异机）
sudo tee /etc/cron.d/erisechat-backup >/dev/null <<'EOF'
0 2 * * * root ERISECHAT_DATA_DIR=/opt/erisechat/data ERISECHAT_BACKUP_DIR=/var/backups/erisechat /usr/local/bin/erisechat-backup.sh
EOF
```

备份内容是 SQLite 一致性快照（`VACUUM INTO`）+ `files/`、`downloads/`，默认保留最近 30 份。

**失败必须可见**：缺少 `sqlite3`、`VACUUM INTO` 失败或快照为空时脚本以非 0 退出，不再降级为直接复制运行中的 `chat.db`（那会产生不一致、可能无法恢复的备份，因此宿主必须装 `sqlite3`，见 §2）；附件归档不再用 `2>/dev/null` 的三级回退吞掉磁盘满 / 权限 / 坏文件等真实 `tar` 错误。快照与附件全部成功后，先写同目录临时归档再原子 `mv` 成最终文件，中途失败只留下临时文件（由 trap 清理），不会留下半截的正式备份。`ERISECHAT_REMOTE` 同步与保留策略仍按原逻辑执行。

### 6.2 恢复演练（停服窗口执行；建议先在测试机演练）

```bash
# 0) 先备份当前数据（便于回滚），并解包目标备份
sudo ERISECHAT_DATA_DIR=/opt/erisechat/data ERISECHAT_BACKUP_DIR=/var/backups/erisechat   bash /opt/erisechat/src/infra/backup.sh
sudo rm -rf /tmp/erisechat-restore && sudo mkdir -p /tmp/erisechat-restore
sudo tar -xzf /var/backups/erisechat/erisechat-YYYYmmdd-HHMM.tar.gz -C /tmp/erisechat-restore

# 1) 停止容器
sudo docker compose -f /opt/erisechat/src/docker-compose.yml stop

# 2) 覆盖 chat.db 与 files/（downloads 不动）
sudo cp /tmp/erisechat-restore/chat.db /opt/erisechat/data/chat.db
sudo rm -rf /opt/erisechat/data/files && sudo mkdir -p /opt/erisechat/data/files
sudo tar -xzf /tmp/erisechat-restore/files.tar.gz -C /opt/erisechat/data
sudo chown -R 1000:1000 /opt/erisechat/data   # 默认 UID 1000；用 ERISECHAT_UID 覆盖时同步修改

# 3) 启动并验证
sudo docker compose -f /opt/erisechat/src/docker-compose.yml start
curl -s http://127.0.0.1:6580/api/health
```

> 恢复前先确认目标归档、先备份当前数据；恢复后抽查登录、历史消息、附件下载。
> **注意**：`infra/restore.sh` 是旧 systemd 方案的恢复脚本（`/var/lib/erisechat` + `systemctl`），
> Docker 部署请用上面的手工步骤（见本文附录）。

---

## 7. 升级与回滚

> 日常“改了代码 → 重新构建 → 上线”的完整步骤（含自检、打包、上传、回滚）见 **[docs/update-deploy.md](update-deploy.md)**。

### 7.1 升级

1. 先备份（§6）；
2. 按 §3.1 用 `bash infra/package-src.sh` 出包（固定名 `dist/erisechat-src.zip`）并上传，解压替换 `/opt/erisechat/src`；
3. 执行 `sudo APP_VERSION=<新版本> bash /opt/erisechat/src/infra/docker/deploy.sh`；
4. 验证 `/api/health` 的 `version`、`/download` 页面与客户端连通性。

- 数据库迁移在服务启动时自动执行（EF Core Migrations），迁移前务必有备份；
- 版本升级保持协议向后兼容（`protocolVersion` 不变即可平滑升级）；
- 安装包（exe/apk）上传到 `/opt/erisechat/data/downloads/` 后自动出现在 `/download` 页（按文件名查找最新）。

### 7.2 回滚

```bash
# 保留上一版本镜像 tag 时可直接切回
sudo APP_VERSION=<旧版本> SKIP_BUILD=1 bash /opt/erisechat/src/infra/docker/deploy.sh
# 或恢复旧版源码目录后重新 deploy
```

### 7.3 从旧命名升级到 1.2.0（一次性迁移）

1.2.0 统一了部署根目录、容器/镜像名、compose 项目名、源码包名与环境变量前缀（旧前缀 `EISE...` 全部改为 `ERISECHAT_*`）。1.1.0 及更早的部署需按下列顺序迁移一次；**`/data` 数据卷内容保持不变**（`chat.db`、`files/`、`downloads/` 原地沿用，不复制、不改名）。

| 步骤 | 操作 |
|---|---|
| 1. 备份 | 先跑一次 `sudo bash <旧部署根目录>/src/infra/backup.sh`；再打包旧源码目录、`.env` 与备份目录留存，作为回滚基线。 |
| 2. 停旧容器 | 用旧 compose 文件执行 `sudo docker compose -f <旧部署根目录>/src/docker-compose.yml down`，确认旧容器与旧项目已停止。 |
| 3. 迁移目录 | 把旧部署根目录改名为 `/opt/erisechat`；如服务器上另有旧命名的备用部署目录、`/var/lib/` 与 `/var/backups/` 下的旧命名目录，一并改为对应的 `erisechat` 名称。数据目录只随根目录改名，不做内容迁移。 |
| 4. 迁移变量 | 编辑 `/opt/erisechat/.env` 与 cron / systemd 中的旧环境变量前缀，统一改为 `ERISECHAT_*`（含 `HOME`、`DATA_DIR`、`ENV_FILE`、`PORT`、`UID`、`BACKUP_DIR`、`BACKUP_KEEP`、`REMOTE`、`PYTHON` 等后缀）；systemd 单元文件与 env 模板已分别改名为 `infra/erisechat.service`、`infra/docker/erisechat.env.example`，引用处同步更新。 |
| 5. 重建 | 用 1.2.0 的 `dist/erisechat-src.zip` 替换 `/opt/erisechat/src`（流程见 `docs/update-deploy.md` §3），执行 `sudo APP_VERSION=1.2.0 bash /opt/erisechat/src/infra/docker/deploy.sh`；新项目名为 `erisechat`，容器与镜像名为 `erisechat-server`。 |
| 6. 健康检查 | `curl -fsS http://127.0.0.1:6580/api/health` 核对 `version=1.2.0`；`docker compose -f /opt/erisechat/src/docker-compose.yml ps` 确认 healthy；再核对 `/download` 页与客户端登录。 |
| 7. 回滚 | 失败时先 `down` 新容器，把目录改回旧名、恢复旧 `.env` 变量名与旧 systemd/cron 引用，用旧源码包与上一版本镜像 tag 按旧 compose 重建；数据卷全程未改动，必要时用第 1 步备份恢复。 |

相关影响与限制：

- JWT `Issuer` / `Audience` 同步改为 `erisechat`，**1.2.0 上线后所有已签发令牌失效，全部用户需重新登录**；不提供旧令牌兼容窗口。
- 客户端 `appId` / `applicationId` 也已变更，Windows 与 Android 均需卸载重装，见 `docs/desktop.md` §2.1、`docs/mobile.md` §2.1。
- `data/downloads/` 中 1.1.0 及更早的历史产物保留发布时的原文件名，服务端白名单只识别 `EriseChat-` 前缀，这些文件**已废弃、不再出现在 `/download`，也不作为回滚路径**；回滚请使用上一版本镜像 tag 与备份。

---

## 8. 卸载

```bash
# 停止并删除容器（数据卷在宿主机目录，不会被删除）
sudo docker compose -f /opt/erisechat/src/docker-compose.yml down
# 删除镜像（可选）
sudo docker image rm erisechat-server:1.1.0
# 确认不再需要数据后手工删除（不可恢复！）
# sudo rm -rf /opt/erisechat /var/backups/erisechat
```

---

## 9. Windows 安装包与 Android APK 分发

- 两个安装包放到 `/opt/erisechat/data/downloads/`，`GET /download`（及其 `/download/files/*` 直链）即可下载；
- 客户端安装后登录，在「设置 → 连接与关于」配置服务器地址（内测用 `https://<host>.<tailnet>.ts.net`，公网用你的域名）；登录页不再提供地址编辑入口，桌面端首次连接地址见 `docs/desktop.md`；
- 客户端细节见 [docs/web.md](docs/web.md)、[docs/mobile.md](docs/mobile.md)。

---

## 10. 故障排查

### 10.1 容器起不来 / 健康检查失败

```bash
docker compose -f /opt/erisechat/src/docker-compose.yml ps
sudo docker logs --tail=100 erisechat-server
```

常见原因：`/opt/erisechat/data` 权限不是容器 UID（默认 1000，重新执行 deploy.sh 会自动 chown）；端口 6580 被占用；
`.env` 缺失或权限不对（应 600 且属主 root）。

### 10.2 只能本机访问 / 外网打不开

- 确认 `ss -ltnp | grep 6580` 是 `127.0.0.1:6580`（正常，公网入口应经 Tailscale Serve / Caddy）；
- Tailscale：`tailscale serve status` 是否 RUNNING、客户端是否在同一 tailnet、管理台 HTTPS 证书是否开启；
- Caddy：`systemctl status caddy`、`journalctl -u caddy -n 100`。

### 10.3 页面 404 / 刷新深链接 404

镜像已内置 SPA 回退（`MapFallbackToFile("index.html")`）；若仍 404，确认镜像构建时 Web 产物已进入 `wwwroot`
（重新 deploy.sh 构建），并检查 `docker logs` 中静态文件请求路径。

### 10.4 构建时镜像拉取失败

```bash
sudo DOTNET_REGISTRY=mcr.m.daocloud.io \
     NODE_IMAGE=docker.m.daocloud.io/library/node:24-alpine \
     bash /opt/erisechat/src/infra/docker/deploy.sh
```

或配置 `/etc/docker/daemon.json` 的 `registry-mirrors` 后 `sudo systemctl restart docker`。

> **注意：** DaoCloud / `registry-mirrors` 只加速 **Docker Hub**（如 `node:24-alpine`）；本项目的 `mcr.microsoft.com/dotnet/sdk|aspnet` 基础镜像不经 Docker Hub，需用上面的 `DOTNET_REGISTRY=mcr.m.daocloud.io`（或直连 MCR）。

### 10.5 数据库被锁定 / WAL 文件增大

SQLite 已启用 WAL；备份用 `VACUUM INTO`。确认没有第二个进程直接打开 `chat.db`（例如本机开发时又启动了服务端连到同一数据目录）。

### 10.6 SSH/SCP 提示 `Enter passphrase` 或 `Permission denied (publickey)`

- Git Bash 默认不共享 Windows 的 ssh-agent，私钥带口令时无法自动解锁；服务器与公钥本身没有问题。
- 上传与远程执行优先用 **Windows PowerShell**（`scp`/`ssh` 均正常）。
- 必须在 Git Bash 用时：启动 Windows OpenSSH Authentication Agent 服务并在 PowerShell 执行 `ssh-add $env:USERPROFILE\.ssh\id_ed25519`（无需口令，后续复用）；或在 Git Bash 内 `eval "$(ssh-agent -s)" && ssh-add ~/.ssh/id_ed25519` 后重试。
- 不要为了这个错误去重新生成或重配服务器公钥。

### 10.7 Windows 提示「已在此计算机上禁用 Sudo」

- 说明命令在 Windows 本机执行了；`sudo` 和 `/opt/erisechat/...` 都属于 Ubuntu 服务器。
- 先 `ssh chat`（提示符变为 `ggbond@chat-server:~$`）再执行；或从 PowerShell 用 `ssh -t chat "sudo ..."`。

### 10.8 部署命令成功但页面/功能没更新

按顺序核对：

1. ZIP 是否真的解压替换（§3.1.4）——`grep -m1 '版本' /opt/erisechat/src/AGENTS.md` 应是本次版本；
2. 是否误用了 `SKIP_BUILD=1`（只重启旧镜像）；
3. `docker inspect erisechat-server --format '{{.Created}}'` 是否为本次构建时间；
4. `curl http://127.0.0.1:6580/api/version`：404 表示仍是旧镜像，200 且带 artifacts 表示已是新代码；
5. 浏览器硬刷新（Ctrl+F5）；Electron/Android 壳内嵌静态资源，需要重打对应 exe/APK 才能看到新界面。

### 10.9 Docker 构建报 `groupadd: GID '1000' already exists`

`mcr.microsoft.com/dotnet/aspnet:10.0` 基于 Ubuntu 24.04，自带 `ubuntu` 用户已占用 `1000:1000`。当前 `infra/docker/Dockerfile` 已改为“已存在则复用该数字 UID/GID”，正常情况下不会再出现。

若仍报错，通常是 `src/` 仍是旧源码（见 §3.1.4）：确认解压后 `grep -n 'getent group' /opt/erisechat/src/infra/docker/Dockerfile` 能看到按存在性建用户的逻辑，再执行部署。

---

## 11. 上线检查清单

- [ ] `docker compose ps` 显示 `healthy`；`/api/health` 返回 200、`version` 与 `db: ok`；隔离实例上模拟数据库不可用时应返回 503，且 healthcheck 转为 `unhealthy`（批次 A 静态改动，隔离实例验证待执行）。
- [ ] `ss -ltnp` 确认 6580 仅绑定 `127.0.0.1`。
- [ ] `/opt/erisechat/.env` 权限 600、无软链接；JWT 密钥为随机值；SMTP 密码只在 .env。
- [ ] HTTPS 入口可用（Tailscale Serve 或 Caddy），Web 端可完成注册/登录/收发消息。
- [ ] SMTP 已配置并实测收到验证码（§5）。
- [ ] 管理员账号已提权（§3.5）。
- [ ] 备份已跑通一次并在 `/var/backups/erisechat` 生成产物；恢复演练已完成（§6.2）。另需抽查：缺 `sqlite3` 时脚本非 0 退出、不留半成品归档；归档内 `chat.db` 通过 `integrity_check`，附件与权限可恢复（批次 A 静态改动，隔离实例演练待执行）。
- [ ] `/download` 页可下载 1.1.0 安装包；覆盖安装不影响服务端数据。
- [ ] 日志无 token/密码/验证码明文；`docker logs` 无异常堆栈。

---

## 附：旧方案说明（备选，不再作为默认路径）

仓库保留了直接部署（`infra/install.sh` + systemd + 宿主 Caddy）的脚本，适合不允许 Docker 的环境；
默认部署以本文的 Docker Compose 方案为准。旧脚本与本文的数据目录不同（`/var/lib/erisechat`），迁移时注意备份与路径。
