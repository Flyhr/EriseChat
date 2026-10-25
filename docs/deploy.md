# EiseChat 服务端部署说明（Ubuntu + Caddy）

> 适用版本：M4（安装包 1.0.0）。目标环境：**Ubuntu 22.04 / 24.04**（云服务器 2C4G 轻量即可），
> 服务端为**单进程**（ASP.NET Core，Kestrel 只监听 `127.0.0.1:8080`），数据落本地 SQLite + 文件目录。
> **明确不使用 Docker / K8s / Nginx**，也不引入 Redis/MongoDB 等外部组件（AGENTS.md §2.4）。

---

## 1. 部署架构

```
Internet ──(443/80, HTTPS)──> Caddy ──(127.0.0.1:8080)──> EiseChat 服务端（systemd 单进程）
                                                             ├─ /var/lib/eisechat/chat.db      SQLite（WAL）
                                                             ├─ /var/lib/eisechat/files/       聊天图片/文件
                                                             ├─ /var/lib/eisechat/downloads/   安装包（GET /download 分发）
                                                             └─ /var/lib/eisechat/secrets.json JWT 密钥（首次启动自动生成，权限 600）
/opt/eisechat-server/   应用二进制（publish-linux.sh 产出的 self-contained 包）
```

- 公网只开放 22（SSH，限来源 IP 更佳）、80、443；**8080 不对公网开放**。
- Web 端、Windows 安装包、Android APK 都连同一个域名（HTTPS/WSS）。

---

## 2. 服务器与域名准备

1. 云服务器安装 **Ubuntu 22.04 / 24.04**，配置 2C4G 起（峰值 ≤100 在线）。
2. 安全组入站：`22`（建议限制来源 IP）、`80`、`443`；其余拒绝。
3. 域名 A 记录指向服务器公网 IP（开发期可先用占位域名 `chat.example.com`，上线前替换）。
4. 安装基础工具：

```bash
sudo apt-get update
sudo apt-get install -y curl unzip sqlite3 rsync
```

5. 安装 Caddy（官方 apt 源，自动 HTTPS）：

```bash
sudo apt-get install -y debian-keyring debian-archive-keyring apt-transport-https curl
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | sudo gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' | sudo tee /etc/apt/sources.list.d/caddy-stable.list
sudo apt-get update && sudo apt-get install -y caddy
```

---

## 3. 发布并安装服务端

### 3.1 在开发机打包（Windows 亦可用）

```bash
bash infra/publish-linux.sh 1.0.0
# 产出：docs/test/publish-linux/chat-server-1.0.0-linux-x64.zip（docs/test/ 不入库）
```

把 zip 上传到服务器（如 `/tmp`）：

```bash
scp docs/test/publish-linux/chat-server-1.0.0-linux-x64.zip root@<服务器IP>:/tmp/
```

### 3.2 在服务器安装

把仓库中的 `infra/` 目录一并上传/解压；`install.sh` 需要同目录的 `eisechat.service` 与 `caddy/Caddyfile`。

```bash
sudo bash infra/install.sh /tmp/chat-server-1.0.0-linux-x64.zip
```

脚本会：创建 `eisechat` 系统用户与 `/var/lib/eisechat` 数据目录 → 解压应用到 `/opt/eisechat-server` →
安装 `eisechat.service` 并 `enable --now` → 等待 `/api/health` 通过 → 提示 Caddy 配置。

验证：

```bash
curl http://127.0.0.1:8080/api/health
# {"status":"ok","serverTime":...,"version":"1.0.0","protocolVersion":1,"db":"ok"}
sudo systemctl status eisechat --no-pager
```

### 3.3 配置 HTTPS（Caddy）

把 `infra/caddy/Caddyfile` 里的 `chat.example.com` 与 `admin@example.com` 换成真实域名/邮箱：

```bash
sudo cp infra/caddy/Caddyfile /etc/caddy/Caddyfile
sudo nano /etc/caddy/Caddyfile        # 改域名与邮箱
sudo caddy validate --config /etc/caddy/Caddyfile
sudo systemctl reload caddy
curl -I https://<你的域名>/           # 200/30x，证书自动签发
```

浏览器打开 `https://<你的域名>/`，在登录页切换「注册」，用邮箱验证码注册首个账号。

### 3.4 管理员提权（v1 手工操作）

计划中管理员用于 Web 端 `/admin` 查看用户与审计日志；v1 无后台提权界面，注册后手工执行一次：

```bash
sqlite3 /var/lib/eisechat/chat.db "UPDATE users SET role='admin' WHERE email='<你的邮箱>';"
```

---

## 4. 配置项（环境变量 / appsettings）

服务端配置读取顺序：**secrets.json（可选） > 环境变量 > appsettings.{Environment}.json > appsettings.json**。
生产环境通过 `/etc/systemd/system/eisechat.service` 的 `Environment=` 覆盖，常用：

| 配置 | 默认 | 说明 |
|---|---|---|
| `App__DataDirectory` | `/var/lib/eisechat`（install.sh 已设） | SQLite 与数据根目录 |
| `App__FileDirectory` | `/var/lib/eisechat/files` | 聊天图片/文件 |
| `App__DownloadDirectory` | `/var/lib/eisechat/downloads` | `GET /download` 分发的安装包 |
| `App__UploadMaxBytes` | 52428800（50MB） | 单文件上限 |
| `App__RegistrationEnabled` | true | 关闭注册时设为 false |
| `App__AllowedEmailDomains` | 空（不限制） | 逗号/数组形式的白名单域名 |
| `App__GroupMaxMembers` | 100 | 群成员上限（2~500） |
| `App__GroupOnlyOwnerCanInvite` | false | 是否仅群主可邀请 |
| `App__OrphanFileCleanupIntervalHours` | 24 | 孤儿文件清理周期（小时） |
| `RateLimit__*` | 见 appsettings.json | 验证码/登录限流阈值 |
| `Jwt__ExpireDays` | 7 | 登录有效期（天） |
| `Kestrel__Endpoints__Http__Url` | `http://127.0.0.1:8080` | 监听地址 |

修改后 `sudo systemctl restart eisechat` 生效。

**密钥纪律**：`secrets.json`（JWT 密钥）由服务端首次启动自动生成（32 字节随机、权限 600），
**不要** 提交、复制到聊天工具或写入日志；丢失后所有旧 token 失效（用户需重新登录）。

---

## 5. 邮件通道（**上线前必须配置**）

- `Development` 环境使用 Mock 邮件：验证码打印到服务端控制台 stdout + 固定测试码 `123456`，
  便于本地开发；**生产环境不可用**。
- 当前 `SmtpEmailSender` 为占位实现（M1-04 明确）：`Production` 下发送验证码会返回 500
  `INTERNAL_ERROR`。**因此正式上线前必须完成 SMTP / 免费邮件 API 实现**（M5 计划项），
  并在 `appsettings.Production.json` 或 systemd 环境中配置：
  - `Email__SmtpHost`、`Email__SmtpPort`（587/465）、`Email__SmtpUseSsl`、`Email__SmtpUser`、
    `Email__SmtpPassword`（用环境变量，勿写仓库）、`Email__FromAddress`、`Email__FromName`。
- 邮件通道要求：发件量小（内部 10~100 人），企业邮箱 SMTP 授权码或 Brevo/Resend 免费额度均可。
- 上线前用真实邮箱走一遍「注册 → 收码 → 登录」，并观察是否进垃圾箱。

---

## 6. 备份与恢复（**必须演练一次**）

### 6.1 每日备份（cron）

```bash
sudo mkdir -p /var/backups/eisechat
sudo crontab -e
# 每天 02:00 备份，顺手同步到异机（可选）
0 2 * * * EISECHAT_DATA_DIR=/var/lib/eisechat EISECHAT_REMOTE=user@backup-host:/srv/eisechat-backup bash /opt/eisechat-server/infra/backup.sh
```

`backup.sh` 用 SQLite `VACUUM INTO` 在线备份（不一致风险低），打包 `files/` 与 `downloads/`，
归档为 `/var/backups/eisechat/eisechat-YYYYMMDD-HHMM.tar.gz`，**保留最近 30 份**，日志写
`/var/backups/eisechat/backup.log`；失败以非 0 退出。

### 6.2 恢复

```bash
sudo bash infra/restore.sh /var/backups/eisechat/eisechat-20261025-0200.tar.gz
```

恢复脚本会先停服务、把当前数据备份到 `/var/lib/eisechat.pre-restore-*`、解包覆盖、修权限、
启动并等待健康检查通过；完成后用两个账号抽查最近消息与文件下载。

### 6.3 演练要求

- 上线前**至少演练一次**恢复，并在 `docs/deploy.md` 中记录演练时间与结果（AGENTS §6）。
- 演练不删除线上数据：在本地或测试服务器用同一份备份先演练。

---

## 7. 升级与回滚

1. 开发机 `bash infra/publish-linux.sh <新版本>`，上传 zip；
2. `sudo bash infra/install.sh /tmp/chat-server-<新版本>-linux-x64.zip`（覆盖 `/opt/eisechat-server`，
   数据目录不动）；
3. 服务端启动时自动执行 EF 迁移（仅允许向后兼容增量，见 `protocol/schema.sql` 与
   `docs/internal-plan.md` §13）；
4. 验证：`curl http://127.0.0.1:8080/api/health`、Web 登录、收发一条消息；
5. 回滚：重新上传旧版本 zip 执行 install.sh；如新版本已迁移数据库且不可逆，需用升级前备份
   `restore.sh` 恢复（因此升级前建议先手动跑一次备份）。

---

## 8. 卸载

```bash
sudo systemctl disable --now eisechat
sudo rm -f /etc/systemd/system/eisechat.service
sudo systemctl daemon-reload
sudo rm -rf /opt/eisechat-server
# 数据默认保留；确认不再需要后手工删除（不可恢复！）
# sudo rm -rf /var/lib/eisechat
```

Windows 客户端卸载：设置 → 应用 → EiseChat → 卸载（或 `Uninstall EiseChat.exe`）；卸载不影响服务端数据。

---

## 9. Windows 安装包与 SmartScreen

- 安装包名：`EiseChat-Setup-1.0.0.exe`（NSIS 每用户安装、免签名，见 M4-03）。
- 首次双击若出现 SmartScreen「Windows 已保护你的电脑」：点击「更多信息 → 仍要运行」。
  这是未购买代码签名证书的正常提示（AGENTS §2.2 不产生授权费用）。
- 安装完成后启动客户端，在登录页「服务器地址」填 `https://<你的域名>`（或本机调试用
  `http://127.0.0.1:8080`），保存后重新连接。

---

## 10. 故障排查

| 现象 | 排查 |
|---|---|
| 打不开页面 | `curl -I https://<域名>/`、`systemctl status caddy`、`curl http://127.0.0.1:8080/api/health`；确认安全组 80/443、DNS 解析 |
| 服务起不来 | `journalctl -u eisechat -n 100 --no-pager`；检查 `/var/lib/eisechat` 权限（须 eisechat 可写）与端口占用 |
| 验证码收不到 | 检查第 5 节邮件配置；`journalctl -u eisechat -f`（Production 下若 SMTP 未实现会返回 500） |
| 登录后频繁掉线 | 检查系统时间（JWT 依赖 UTC）；看 Caddy 日志与客户端网络；重连会走 `sync` 补拉 |
| 磁盘增长快 | 检查 `/var/lib/eisechat/files` 与 `downloads`；孤儿文件清理默认 24h 一轮（`App__OrphanFileCleanupIntervalHours`），可手动清理过期安装包 |
| 备份失败 | `/var/backups/eisechat/backup.log`；确认 sqlite3/rsync 已装、目标路径可写 |
| 忘记管理员 | `sqlite3 /var/lib/eisechat/chat.db "UPDATE users SET role='admin' WHERE email='...';"` |

常用命令：

```bash
sudo systemctl status|restart|stop eisechat
sudo journalctl -u eisechat -f
curl -s http://127.0.0.1:8080/api/health
sudo tail -f /var/log/caddy/eisechat-access.log
sqlite3 /var/lib/eisechat/chat.db '.tables'
```

---

## 11. 上线检查清单

- [ ] 域名解析、安全组 22/80/443、Caddy 证书正常（`https` 可用）
- [ ] `/api/health` 返回 `db:"ok"`，`version` 与安装包一致
- [ ] 邮件通道可用（注册/登录收到验证码），或明确暂用备用方案
- [ ] 注册开关/邮箱白名单按需配置；首个管理员已提权
- [ ] 每日备份 cron 生效，**恢复演练完成并记录**
- [ ] 服务端时间与时区正确（UTC 存储，前端本地化显示）
- [ ] 安装包下载页 `GET /download` 可访问，`downloads/` 已放安装包
- [ ] 两个账号实测：登录、单聊、群聊、图片/文件、断线重连补拉
