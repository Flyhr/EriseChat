# EiseChat Android 安装与联调说明（M5）

> 本文是 Android 客户端（Capacitor 壳 + `apps/web` 构建产物）的安装、配置、构建与签名说明。
> 协议与表结构见 `protocol/protocol.md`、`protocol/schema.sql`；服务器部署见 `docs/deploy.md`；项目介绍以 `README.md` 为准。

## 1. 交付物

| 文件 | 说明 |
|---|---|
| `EiseChat-1.0.0.apk` | Android 安装包（自签名，侧载安装，不上架应用商店） |
| 下载地址 | 服务端 `GET /download` 下载页（`data/downloads/` 放安装包） |

- 本机产物：`data/downloads/EiseChat-1.0.0.apk`（3,276,544 B，SHA256 `dee0898924f96da0688cd2f2ac5d6aa7878adab668fec0acd74d4debf6984d14`），`/download` 页面显示大小与下载链接
- 包名：`com.eisechat.mobile`（与 Windows 壳 `com.eisechat.desktop` 区分）
- 版本：`versionName` 为 `1.0.0`，`versionCode` 规则 `major*10000 + minor*100 + patch`（1.0.0 → 10000）
- 最低系统：Android 8.0（API 26）；目标/编译 API 36
- 自签名安装包在部分手机上会提示「未知来源/风险应用」，需用户确认后安装

## 2. 安装与首次登录

1. 手机允许浏览器/文件管理器「安装未知应用」（设置 → 安全 → 安装未知应用）。
2. 通过下载页 `http://<服务器地址>:8080/download` 或数据线把 `EiseChat-1.0.0.apk` 传到手机并安装。
3. 打开 EiseChat，在登录页点「服务器地址」填写服务端地址：
   - 手机与开发机同一局域网：`http://<开发机局域网 IP>:8080`（不能用 `127.0.0.1`，那指向手机自身）
   - 互联网部署：`https://<域名>`（Caddy 自动 HTTPS，见 `docs/deploy.md`）
4. 新账号先切「注册」→「发送验证码」（开发环境固定测试码 `123456`，生产环境走真实邮件）→ 填昵称/验证码注册并登录。
5. 登录后可搜索用户发起单聊/建群、收发文本、图片与文件消息；断线重连后自动补拉消息。

## 3. 网络与明文 HTTP（内网联调）

- `capacitor.config.ts` 设置 `server.androidScheme = 'http'`：页面 origin 为 `http://localhost`，避免 https 页面调用 http API 被 WebView 判定为混合内容。
- `AndroidManifest.xml` 开启 `android:usesCleartextTraffic="true"`，仅用于**内网联调**（http 服务器）。
- 生产环境建议使用 https 域名并在后续版本收紧明文策略；服务端数据仍以服务端为唯一事实源。

## 4. 已知限制（v1）

- **无系统级离线推送**：新消息在打开应用时补拉；Android 后台可能冻结进程，收不到即时提醒（P2 再评估前台服务）。
- 不做 iOS；不做应用商店上架；不做自动更新。
- 覆盖升级必须使用**同一个 keystore** 签名，否则系统拒绝安装（只能先卸载，聊天数据在服务端不受影响）。
- 应用内缓存（IndexedDB）仅作展示缓存，卸载即清空；服务端数据不受影响。

## 5. 构建与签名（开发机）

### 5.1 环境

- JDK 21（推荐）：`JAVA_HOME` 指向 JDK 目录（本机为 `D:/softWare/jdk21`，Temurin 21.0.12.1）
- Android SDK：platforms `android-36`、build-tools `36.0.0`、platform-tools（Gradle 构建可能自动安装 build-tools 35）；本机原 `C:/Program Files (x86)/Android/android-sdk` 只读，复制到可写目录 `D:/softWare/android-sdk` 并接受许可
- `apps/mobile/android/local.properties`（不入库）：
  `sdk.dir=D:/softWare/android-sdk`（本机；按实际 SDK 路径填写）
- 首次构建会下载 Gradle 与 AGP 依赖（数分钟到十几分钟，缓存于 `~/.gradle`）；本机 `gradle-wrapper.properties` 使用腾讯镜像，网络受限环境可同样处理

### 5.2 keystore（禁止入库，必须离线备份）

```bash
mkdir -p infra/keys
"$JAVA_HOME/bin/keytool" -genkeypair -v \
  -keystore infra/keys/eisechat-release.jks -alias eisechat \
  -keyalg RSA -keysize 2048 -validity 10000 \
  -storepass '<随机密码>' -keypass '<随机密码>' \
  -dname "CN=EiseChat, OU=Internal, O=EiseChat, L=Local, ST=Local, C=CN"
```

`infra/keys/keystore.properties`（不入库）：

```properties
storeFile=../../../infra/keys/eisechat-release.jks
storePassword=<与上面一致>
keyAlias=eisechat
keyPassword=<与上面一致>
```

> `infra/keys/` 已在 `.gitignore` 中忽略。**务必离线备份 keystore 与密码**：丢失后无法对已安装用户做覆盖升级（只能卸载重装）。

### 5.3 打包

```bash
bash infra/build-apk.sh 1.0.0
```

脚本流程：`apps/web` 构建 → `npx cap sync android` → `./gradlew assembleRelease` → 复制为
`data/downloads/EiseChat-1.0.0.apk` 并打印 SHA256。产物 `apps/mobile/android/app/build/`、`data/downloads/*.apk` 均不入库。

版本号规则与三端（服务端 `/api/health`、Windows 安装包、APK）保持 `1.0.0`；发版时同时修改
`apps/desktop/package.json`、`apps/web/package.json`、`apps/mobile/android/app/build.gradle`。

### 5.4 静态校验（可选）

```bash
export JAVA_HOME=D:/softWare/jdk21   # apksigner.bat 依赖 java
SDK=D:/softWare/android-sdk
"$SDK/build-tools/36.0.0/aapt2.exe" dump badging data/downloads/EiseChat-1.0.0.apk | head
"$SDK/build-tools/36.0.0/apksigner.bat" verify --verbose data/downloads/EiseChat-1.0.0.apk
unzip -l data/downloads/EiseChat-1.0.0.apk | grep assets/public/index.html
```

校验基线（1.0.0）：`package=com.eisechat.mobile`、`versionCode=10000`、`versionName=1.0.0`、`minSdkVersion=26`、`targetSdkVersion=36`、`application-label='EiseChat'`、`INTERNET` 权限；签名 `Verifies` 且 v2 scheme=true（v1 未启用，minSdk 26 无需要）。

## 6. 故障排查

| 现象 | 排查 |
|---|---|
| 页面白屏 | 确认打包前 `apps/web` 已用 `base: './'` 构建（M4-07）；重新执行 `cap sync` 后打包 |
| 登录提示 Failed to fetch / 无法连接服务器 | 检查登录页「服务器地址」是否填写；手机与服务器是否同一网络；服务端是否监听 `0.0.0.0:8080` 且防火墙放行；https 页面不能调 http API |
| 安装被拦截 | 允许「安装未知应用」；确认 APK 完整（对比下载页 SHA256） |
| 覆盖安装失败 | 新旧 APK 签名不一致；使用同一 keystore 重新打包，或卸载旧版后安装 |
| 收不到新消息提醒 | v1 无系统推送，打开应用会自动补拉；后台被系统冻结属预期 |
