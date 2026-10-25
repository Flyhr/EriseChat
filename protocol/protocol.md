# EiseChat WebSocket 协议 v1

> 本文件是**协议帧与字段的唯一来源**（`AGENTS.md` §1/§5）；表结构唯一来源为 `protocol/schema.sql`。
> 依据：`docs/internal-plan.md` §4.2 / §4.3（计划 V2.3.2）与 `docs/project-overview.md` §11.1。
> 版本：`protocolVersion = 1`（2026-10-25 初版）；变更必须先改本文件与计划文档，再改实现，且只允许向后兼容的增量（见 §8）。

## 1. 传输与连接

- **地址**：`wss://<域名>/ws`（开发期可用 `ws://127.0.0.1:8080/ws`）；REST 与 WebSocket 同域。
- **编码**：UTF-8（无 BOM）；一条 WebSocket 文本消息 = 一帧；**一行一帧**，JSON 序列化后不得换行或格式化。
- 只接受文本帧；二进制帧忽略并记日志。
- **单帧上限 256 KiB**；超过时服务端关闭连接（close code `1009`）。
- **连接生命周期**：
  1. 客户端建立连接后 **10 秒内发送 `auth`（必须是首帧）**，否则服务端发送 `error`（`AUTH_REQUIRED`）并关闭（`4002`）。
  2. 服务端校验 JWT / `deviceId` → 回 `hello`；失败回 `error` 后关闭。
  3. 进入就绪态，服务端开始推送帧。
- **单活动连接**：同一账号新连接认证成功时，旧连接收到 `kicked` 帧后由服务端关闭（`4001`）；旧客户端**不得自动重连**，清理登录态并回登录页。
- **心跳**：客户端每 25 秒发 `ping`（带 `ts`）；30 秒内未收到 `pong` 判定断线。服务端也可主动发 `ping`，客户端必须立即回 `pong`。
- **重连**：断线后按 `1s / 2s / 5s / 10s / 30s` 指数退避，每次叠加 0~500ms 随机抖动；重连成功后必须补拉（见 §6.2）。
- **关闭码**（WebSocket close code）：

| close code | 场景 | 客户端处理 |
|---|---|---|
| `1000` | 正常关闭 | 不重连 |
| `1001` | 服务端维护/重启 | 按退避重连 |
| `1009` | 单帧超过 256 KiB | 修正后重连 |
| `4001` | 被同账号新连接踢下线（`kicked` 已先行） | 不回登录页自动重连 |
| `4002` | 认证失败/超时、协议版本不兼容、帧格式错误、服务端关闭中 | 按错误码处理；认证类回登录页 |

## 2. 帧通用约定

- 所有帧均为 JSON 对象，必须含 `type`（字符串，点分隔，全小写，如 `message.send`）。
- `reqId`：C→S 请求可携带（字符串，1~64 字符，客户端生成）；服务端在对应响应帧中原样回传；S→C 主动推送不带。**需要匹配响应的请求应始终携带 `reqId`**。
- 字段命名：JSON 用 camelCase；数据库列用 snake_case（见 `protocol/schema.sql`），映射在 DTO/实体层完成。
- 类型约定：

| 类型 | 说明 |
|---|---|
| `string` | UTF-8 字符串；ID 为 UUID 文本（小写、36 位带连字符） |
| `integer` | 64 位整数；时间一律为 **UTC 毫秒**；`seq` 为会话内序号 |
| `boolean` | `true` / `false` |
| `object` / `array` | 见各帧定义 |
| `null` | 空值；可选字段缺省时可不出现，等价于未设置 |

- **未知字段忽略**；**未知 `type` 忽略且不断连**（为向后兼容预留）。
- 非法 JSON 或缺少 `type` → 服务端回 `error`（`BAD_FRAME`）并关闭 `4002`。
- 认证完成前只接受 `auth` 帧；收到其它帧 → `error`（`AUTH_REQUIRED`）并关闭 `4002`。
- `seq` 由服务端在事务内分配，客户端不得携带、不得修改；消息落库后不可变。

## 3. 帧总表（与计划 §4.2 一一对应，共 17 个 type）

| type | 方向 | 用途 | 备注 |
|---|---|---|---|
| `auth` | C→S | 首帧认证 | 必须为连接后的第一帧 |
| `hello` | S→C | 认证成功 | 返回连接参数与协议版本 |
| `ping` | 双向 | 心跳请求 | 客户端 25 秒一次 |
| `pong` | 双向 | 心跳响应 | 原样回传 `ts` |
| `message.send` | C→S | 发送消息 | 带 `clientMsgId` 幂等键 |
| `message.ack` | S→C | 落库回执 | 仅发送者连接收到 |
| `message.new` | S→C | 新消息广播 | 会话成员（发送连接除外） |
| `sync.request` | C→S | 补拉请求 | 每会话携带 `lastSeq` |
| `sync.response` | S→C | 补拉响应 | 每会话每批 ≤200 条 |
| `conv.list` | 双向 | 会话列表 | C→S 请求 / S→C 返回全量 |
| `conv.updated` | S→C | 会话变更推送 | 新建、标题、成员、最新消息变化 |
| `read.update` | C→S | 上报已读游标 | 打开会话滚到底部时上报 |
| `read.updated` | S→C | 已读广播 | 仅单聊推送给对方 |
| `typing` | 双向 | 正在输入 | 3 秒节流，不落库 |
| `presence` | S→C | 在线状态 | 有共同会话的成员 |
| `kicked` | S→C | 被踢下线 | 随后关闭连接 |
| `error` | S→C | 统一错误 | 错误码见 §5 |

## 4. 帧定义

### 4.1 `auth` / `hello`（认证）

`auth`（C→S，连接后首帧）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `type` | string | 是 | 固定 `auth` |
| `reqId` | string | 否 | 建议携带，`hello`/`error` 回传 |
| `token` | string | 是 | REST 登录返回的 JWT accessToken |
| `deviceId` | string | 是 | 设备标识（1~128 字符，客户端持久化；同一设备重连复用） |
| `protocolVersion` | integer | 否 | 客户端协议版本，缺省视为 `1` |

`hello`（S→C）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `type` | string | 是 | 固定 `hello` |
| `reqId` | string | 否 | 回传 `auth` 的 `reqId` |
| `userId` | string | 是 | 当前登录用户 ID |
| `heartbeatSec` | integer | 是 | 服务端建议心跳间隔，当前为 `25` |
| `serverTime` | integer | 是 | 服务端当前时间（UTC 毫秒） |
| `protocolVersion` | integer | 是 | 服务端协议版本，当前为 `1` |

规则：

1. `token` 无效、过期或会话已撤销 → `error`（`AUTH_FAILED` / `AUTH_EXPIRED`）后关闭 `4002`。
2. `auth.protocolVersion` 与服务端不兼容 → `error`（`PROTOCOL_VERSION_MISMATCH`）后关闭 `4002`。
3. 认证成功后旧连接被踢（§1），新连接开始接收推送；`hello.serverTime` 用于客户端校准展示时间。
4. 客户端收到 `hello` 后必须校验 `protocolVersion`；不兼容时提示升级并主动关闭。

### 4.2 `ping` / `pong`（心跳）

`ping`（双向）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `type` | string | 是 | 固定 `ping` |
| `ts` | integer | 是 | 发送方当前时间（UTC 毫秒） |

`pong`（双向）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `type` | string | 是 | 固定 `pong` |
| `ts` | integer | 是 | 原样回传对应 `ping` 的 `ts` |

规则：收到 `ping` 立即回 `pong`；心跳帧不落库、不携带 `reqId`；客户端以「收到 `pong` 或任意帧」判断连接存活。

### 4.3 `message.send` / `message.ack` / `message.new`（消息）

`message.send`（C→S）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `type` | string | 是 | 固定 `message.send` |
| `reqId` | string | 否 | 建议携带，`ack` 回传 |
| `clientMsgId` | string | 是 | 客户端生成的 UUID；会话内唯一，幂等键 |
| `conversationId` | string | 是 | 目标会话 ID |
| `msgType` | string | 是 | `text` / `image` / `file`；`system` 仅服务端生成，客户端发送会被拒绝 |
| `content` | string / object | 是 | `text` 为字符串；`image` / `file` 为 FileContent（见 §7）；大小受服务端配置限制（当前 v1 实现：纯文本 ≤ 4096 字符，超出回 `error`（`MESSAGE_TOO_LARGE`）） |
| `replyTo` | string | 否 | 被回复消息的 `messageId` |

`message.ack`（S→C，仅发送者连接收到）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `type` | string | 是 | 固定 `message.ack` |
| `reqId` | string | 否 | 回传 `message.send` 的 `reqId` |
| `clientMsgId` | string | 是 | 回传客户端幂等键 |
| `messageId` | string | 是 | 服务端消息 ID（`messages.id`） |
| `conversationId` | string | 是 | 会话 ID |
| `seq` | integer | 是 | 会话内序号，严格 `+1` |
| `createdAt` | integer | 是 | 落库时间（UTC 毫秒） |

规则：

1. `(conversationId, clientMsgId)` 幂等：重复发送（含客户端重试）**不重复落库**，直接回相同 `messageId` / `seq` / `createdAt` 的 `ack`。
2. `ack` 只回发送连接；发送者以 `ack` 判定「已发送」，不依赖 `message.new`。
3. `message.send` 校验失败（非成员、参数错误、内容超限等）只回 `error`，不落库。

`message.new`（S→C 推送）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `type` | string | 是 | 固定 `message.new` |
| `message` | object | 是 | 消息对象，见下表 |

规则：广播给**会话内除发送连接外的成员**；发送者以 `message.ack` 为准（避免同连接重复）。后续支持多设备时，发送者的其它设备作为接收方收到 `message.new`，协议不变。

**消息对象（Message）**：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `messageId` | string | 是 | 消息 ID（`messages.id`） |
| `conversationId` | string | 是 | 会话 ID |
| `senderId` | string | 是 | 发送者用户 ID |
| `clientMsgId` | string | 否 | 发送者客户端幂等键；服务端广播时可不带 |
| `seq` | integer | 是 | 会话内序号 |
| `msgType` | string | 是 | `text` / `image` / `file` / `system` |
| `content` | string / object | 是 | 按 `msgType` 解释，见下 |
| `replyTo` | string | 否 | 被回复消息的 `messageId` |
| `createdAt` | integer | 是 | 创建时间（UTC 毫秒） |
| `deletedAt` | integer | 否 | 预留（v1 不做撤回，恒为空） |

`content` 按 `msgType` 的格式：

- `text`：字符串（纯文本，客户端渲染时必须转义，禁止注入 HTML）。
- `image` / `file`：FileContent 对象，格式见 §7。
- `system`：服务端生成的系统事件对象（M3 使用），核心字段为 `event`（事件名，必填）、`actorId`（触发者，可选）、`targetUserIds`（涉及成员，可选）、`text`（服务端生成的中文描述，可选）；后续新增可选字段属向后兼容增量。

### 4.4 `sync.request` / `sync.response`（补拉同步）

`sync.request`（C→S）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `type` | string | 是 | 固定 `sync.request` |
| `reqId` | string | 否 | 建议携带，`sync.response` 回传 |
| `cursors` | array | 是 | 每项 `{ conversationId, lastSeq }`；1~100 项；同一 `conversationId` 不得重复 |

cursor 对象：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `conversationId` | string | 是 | 会话 ID |
| `lastSeq` | integer | 是 | 客户端本地已收到的最大序号；`0` 表示从最早开始 |

`sync.response`（S→C）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `type` | string | 是 | 固定 `sync.response` |
| `reqId` | string | 否 | 回传请求的 `reqId` |
| `batches` | array | 是 | 每项 `{ conversationId, messages }`；无新消息的会话不出现 |
| `hasMore` | boolean | 是 | `true` 表示至少一个会话还有未返回消息，需继续请求 |

batch 对象：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `conversationId` | string | 是 | 会话 ID |
| `messages` | array | 是 | Message 数组，按 `seq` **升序**；每条 `seq > lastSeq` |

规则：

1. 只返回 `seq > lastSeq` 的消息，按 `seq` 升序；**每会话每批最多 200 条**。
2. `hasMore = true` 时客户端应以「已收到的最大 `seq` 作为新 `lastSeq`」继续发 `sync.request`，直到 `hasMore = false` 追平。
3. `lastSeq` 必须满足 `0 ≤ lastSeq ≤ 会话当前 lastSeq`，否则回 `error`（`VALIDATION_FAILED`）。
4. `sync.request` 是只读操作，可重复调用；客户端仍需按 `messageId` 去重、按 `seq` 排序。
5. 补拉只返回消息，不返回会话元数据；会话元数据由 `conv.list` / `conv.updated` 维护。

### 4.5 `conv.list` / `conv.updated`（会话列表）

`conv.list`（C→S 请求）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `type` | string | 是 | 固定 `conv.list` |
| `reqId` | string | 否 | 建议携带，响应回传 |

`conv.list`（S→C 响应）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `type` | string | 是 | 固定 `conv.list` |
| `reqId` | string | 否 | 回传请求的 `reqId` |
| `conversations` | array | 是 | Conversation 数组（当前用户全部会话，按最近消息时间倒序；无消息的会话按创建时间倒序） |

`conv.updated`（S→C 推送）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `type` | string | 是 | 固定 `conv.updated` |
| `conversations` | array | 是 | 发生变化的 Conversation 数组 |

规则：触发场景为「新建会话、标题变更、成员加入/退出、新消息导致 `lastSeq` / `lastMessage` 变化」。客户端按 `conversationId` 合并（upsert）；同一变化可能同时收到 `message.new` 与 `conv.updated`，客户端需去重。

**会话对象（Conversation）**：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `conversationId` | string | 是 | 会话 ID |
| `type` | string | 是 | `direct`（单聊）/ `group`（群聊） |
| `title` | string | 是 | 群标题；单聊为空字符串（客户端用对方昵称展示） |
| `createdAt` | integer | 是 | 创建时间（UTC 毫秒） |
| `lastSeq` | integer | 是 | 会话最新序号 |
| `lastReadSeq` | integer | 是 | 当前用户在该会话的已读游标 |
| `lastMessage` | object / null | 否 | 最新消息快照（Message，可为 `null`） |
| `members` | array | 是 | 成员数组，见下表 |

**成员对象（ConversationMember）**：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `userId` | string | 是 | 用户 ID |
| `displayName` | string | 是 | 昵称 |
| `email` | string | 是 | 邮箱（内部版可直接下发） |
| `role` | string | 是 | `owner` / `member` |
| `joinedAt` | integer | 是 | 加入时间（UTC 毫秒） |
| `avatarFileId` | string | 否 | 头像文件 ID |

> 未读数由客户端按 `未读 = lastSeq − lastReadSeq` 计算，协议不单独下发 `unread`（`AGENTS.md` §5.6）。

### 4.6 `read.update` / `read.updated`（已读）

`read.update`（C→S）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `type` | string | 是 | 固定 `read.update` |
| `conversationId` | string | 是 | 会话 ID |
| `lastReadSeq` | integer | 是 | 已读到的最新序号（打开会话滚到底部时上报） |

`read.updated`（S→C）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `type` | string | 是 | 固定 `read.updated` |
| `conversationId` | string | 是 | 会话 ID |
| `userId` | string | 是 | 已读游标所属用户 |
| `lastReadSeq` | integer | 是 | 该用户最新的已读序号 |

规则：

1. 服务端校验请求者是会话成员；`0 ≤ lastReadSeq ≤ 会话当前 lastSeq`，越界回 `error`（`VALIDATION_FAILED`）。
2. 服务端按 `max(现有 last_read_seq, 上报值)` 更新 `conversation_members.last_read_seq`，只增不减。
3. `read.updated` 仅单聊推送给**对方成员**（不回显给上报者）；群聊 v1 不做群已读详情，只更新游标、不广播。

### 4.7 `typing` / `presence`

`typing`（双向）：

| 方向 | 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|---|
| C→S | `type` | string | 是 | 固定 `typing` |
| C→S | `conversationId` | string | 是 | 目标会话；`userId` 以连接身份为准，客户端无需发送 |
| S→C | `type` | string | 是 | 固定 `typing` |
| S→C | `conversationId` | string | 是 | 会话 ID |
| S→C | `userId` | string | 是 | 正在输入的用户 ID |

规则：客户端 3 秒节流；服务端只转发给会话内其他成员，不落库；接收端 3 秒内未收到同一用户的新 `typing` 则自动清除指示。

`presence`（S→C）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `type` | string | 是 | 固定 `presence` |
| `userId` | string | 是 | 用户 ID |
| `online` | boolean | 是 | 是否在线 |

规则：在用户认证成功（上线）与连接关闭/心跳超时（下线）时，推送给与其有共同会话的在线成员；v1 不做频繁状态刷新。

### 4.8 `kicked` / `error`

`kicked`（S→C）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `type` | string | 是 | 固定 `kicked` |
| `reason` | string | 是 | 原因描述，如「账号在其他设备登录」 |

规则：服务端发送 `kicked` 后关闭旧连接（`4001`）；旧客户端必须停止自动重连、清理登录态并回登录页。

`error`（S→C）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `type` | string | 是 | 固定 `error` |
| `code` | string | 是 | 错误码，见 §5 |
| `message` | string | 是 | 可展示的中文说明（不包含敏感信息） |
| `reqId` | string | 否 | 回传对应请求的 `reqId` |

规则：非致命错误只回 `error`，连接保持；致命错误先回 `error`（若连接仍可写），再关闭（close code 见 §5）。

## 5. 错误码

错误码枚举的唯一来源为本表；实现放 `server/Chat.Protocol/Enums.cs`，前端 `apps/web/src/types/frames.d.ts` 镜像。REST 错误响应复用同一 `code` 枚举（响应体 `{ code, message, traceId }` 由 M1 的 API 层实现，`traceId` 用于排查，不返回敏感信息）。

### 5.1 WebSocket 错误码

| code | 说明 | 连接处理 |
|---|---|---|
| `BAD_FRAME` | 帧不是合法 JSON、缺 `type` 或字段类型错误 | 致命：回 `error` 后关闭 `4002` |
| `AUTH_REQUIRED` | 认证前发送了非 `auth` 帧，或 10 秒未认证 | 致命：回 `error` 后关闭 `4002` |
| `AUTH_FAILED` | token 无效、`deviceId` 缺失或格式非法 | 致命：回 `error` 后关闭 `4002` |
| `AUTH_EXPIRED` | JWT 过期或会话已被撤销 | 致命：回 `error` 后关闭 `4002` |
| `PROTOCOL_VERSION_MISMATCH` | `protocolVersion` 不兼容 | 致命：回 `error` 后关闭 `4002` |
| `FORBIDDEN` | 无权限执行该操作 | 非致命 |
| `NOT_MEMBER` | 不是目标会话成员 | 非致命 |
| `NOT_FOUND` | 资源不存在（会话、消息等） | 非致命 |
| `VALIDATION_FAILED` | 字段缺失、类型错误、数值越界等 | 非致命 |
| `MESSAGE_TOO_LARGE` | 消息内容超过服务端配置上限 | 非致命：只拒绝该消息 |
| `RATE_LIMITED` | 操作过于频繁 | 非致命 |
| `INTERNAL_ERROR` | 服务端内部错误（日志带 traceId） | 非致命；连接不可用时服务端可关闭 |

### 5.2 账号与 REST 错误码

| code | 说明 |
|---|---|
| `UNAUTHORIZED` | 未登录或 REST 请求缺少有效 token |
| `CODE_INVALID` | 邮箱验证码错误 |
| `CODE_EXPIRED` | 邮箱验证码已过期 |
| `CODE_ATTEMPTS_EXCEEDED` | 验证码错误次数超限，已作废 |
| `EMAIL_RATE_LIMITED` | 验证码发送限流（同邮箱 60 秒/次、10 次/天；同 IP 20 次/小时） |
| `EMAIL_DOMAIN_NOT_ALLOWED` | 邮箱域名不在管理员配置的白名单内 |
| `REGISTRATION_DISABLED` | 注册功能已关闭 |
| `EMAIL_ALREADY_REGISTERED` | 邮箱已注册 |
| `INVITE_REQUIRED` | 当前注册需要邀请码 |
| `INVITE_INVALID` | 邀请码无效或已被使用 |
| `INVITE_EXPIRED` | 邀请码已过期 |
| `PASSWORD_INVALID` | 密码错误 |
| `ACCOUNT_LOCKED` | 登录失败次数过多，临时锁定 |
| `ACCOUNT_DISABLED` | 账号已被禁用 |
| `FILE_TOO_LARGE` | 上传文件超过大小上限（默认 50MB，可配置） |
| `FILE_TYPE_NOT_ALLOWED` | 文件 MIME 或扩展名不在允许范围 |

## 6. 时序示例

### 6.1 建立连接并发送消息（`message.send` → `message.ack`）

步骤：连接 → `auth` → `hello` → 发送 → `ack` → 对端收到 `message.new`。

```json
C→S: {"type":"auth","reqId":"a1","token":"eyJhbGciOi...","deviceId":"desktop-abc123","protocolVersion":1}
S→C: {"type":"hello","reqId":"a1","userId":"9b2f3c1e-5d4a-4c8e-9f10-2a7b6c5d4e3f","heartbeatSec":25,"serverTime":1730000000000,"protocolVersion":1}
C→S: {"type":"message.send","reqId":"r1","clientMsgId":"c-9f2a-4b1c-8d3e-7f6a-5b4c3d2e1f00","conversationId":"cv-1a2b3c4d","msgType":"text","content":"你好"}
S→C: {"type":"message.ack","reqId":"r1","clientMsgId":"c-9f2a-4b1c-8d3e-7f6a-5b4c3d2e1f00","messageId":"m-7d8e9f01","conversationId":"cv-1a2b3c4d","seq":42,"createdAt":1730000000123}
S→C(对端): {"type":"message.new","message":{"messageId":"m-7d8e9f01","conversationId":"cv-1a2b3c4d","senderId":"9b2f3c1e-5d4a-4c8e-9f10-2a7b6c5d4e3f","seq":42,"msgType":"text","content":"你好","createdAt":1730000000123}}
```

说明：客户端收到 `ack` 后把本地消息状态由「发送中」改为「已发送」；收到 `message.new` 按 `messageId` 去重、按 `seq` 排序合并。

### 6.2 断线重连 → 补拉（`sync.request` / `sync.response`）

步骤：断线（不删本地数据）→ 指数退避重连 → `auth` → `hello` → 对每个会话用本地 `lastSeq` 发 `sync.request` → 合并 `sync.response` → `hasMore=false` 追平。

```json
C→S: {"type":"auth","reqId":"a2","token":"eyJhbGciOi...","deviceId":"desktop-abc123","protocolVersion":1}
S→C: {"type":"hello","reqId":"a2","userId":"9b2f3c1e-5d4a-4c8e-9f10-2a7b6c5d4e3f","heartbeatSec":25,"serverTime":1730000300000,"protocolVersion":1}
C→S: {"type":"sync.request","reqId":"s1","cursors":[{"conversationId":"cv-1a2b3c4d","lastSeq":40},{"conversationId":"cv-2b3c4d5e","lastSeq":7}]}
S→C: {"type":"sync.response","reqId":"s1","hasMore":false,"batches":[{"conversationId":"cv-1a2b3c4d","messages":[{"messageId":"m-41","conversationId":"cv-1a2b3c4d","senderId":"u-b","seq":41,"msgType":"text","content":"在吗","createdAt":1730000200100},{"messageId":"m-42","conversationId":"cv-1a2b3c4d","senderId":"u-b","seq":42,"msgType":"text","content":"刚才断线了吗","createdAt":1730000200200}]}]}
```

说明：若 `hasMore=true`，以已收到最大 `seq` 更新对应会话的 `lastSeq` 后继续 `sync.request`；每会话每批最多 200 条；补拉与实时 `message.new` 可能重叠，客户端统一按 `messageId` 去重。

### 6.3 收到 `seq` 缺口消息 → 立即补拉

步骤：客户端 `lastSeq=10`；收到 `seq=13` 的 `message.new` → 发现 `13 > 10 + 1` → 暂存该消息 → 立即对 `lastSeq=10` 发起 `sync.request` → 补齐 11~12 后合并 → `lastSeq=13`。

```json
C→S: {"type":"sync.request","reqId":"s2","cursors":[{"conversationId":"cv-1a2b3c4d","lastSeq":10}]}
S→C: {"type":"sync.response","reqId":"s2","hasMore":false,"batches":[{"conversationId":"cv-1a2b3c4d","messages":[{"messageId":"m-11","conversationId":"cv-1a2b3c4d","senderId":"u-b","seq":11,"msgType":"text","content":"11","createdAt":1730000001000},{"messageId":"m-12","conversationId":"cv-1a2b3c4d","senderId":"u-b","seq":12,"msgType":"text","content":"12","createdAt":1730000002000}]}]}
```

说明：补齐前不丢弃已收到的 `seq=13`，也不显示空洞；客户端应缓冲缺口消息，补拉后按 `seq` 合并展示。

## 7. 图片与文件消息

二进制文件**不经过 WebSocket**；WS 只传 `fileId` 等元数据。

1. **上传**：`POST /api/files`（multipart/form-data，需登录 JWT）→ `{ fileId, name, size, mime }`；默认上限 50MB（可配置），服务端校验 MIME/扩展名，不暴露磁盘路径。
2. **发送**：`message.send` 的 `msgType` 为 `image` / `file`，`content` 为 FileContent：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `fileId` | string | 是 | 文件 ID（`files.id`） |
| `name` | string | 是 | 原始文件名 |
| `size` | integer | 是 | 文件大小（字节） |
| `mime` | string | 是 | MIME 类型 |

3. **下载**：`GET /api/files/{fileId}`，需登录且请求者必须是引用该文件的会话成员；流式返回，不暴露磁盘路径。
4. **图片**：客户端发送前压缩（最长边 1280px、约 ≤300KB）；图片消息与文件消息共用 FileContent。
5. **存储**：`/data/files/yyyyMM/{fileId}{ext}`（逻辑路径，实际目录见 `appsettings.json`）；孤儿文件由后台任务清理。

示例（图片消息）：

```json
{"type":"message.send","reqId":"r2","clientMsgId":"c-1b2c-3d4e-5f60-789a-bcdef0123456","conversationId":"cv-1a2b3c4d","msgType":"image","content":{"fileId":"f-12ab34cd","name":"照片.png","size":204800,"mime":"image/png"}}
```

## 8. 版本与兼容规则

### 8.1 协议版本

- 当前 `protocolVersion = 1`，随计划 V2.3.2 发布。
- 服务端在 `hello` 中下发 `protocolVersion`；客户端在 `auth` 中可带上自身版本（缺省视为 1）。
- 版本不兼容时服务端回 `error`（`PROTOCOL_VERSION_MISMATCH`）并关闭；客户端提示升级，不进入聊天。

### 8.2 帧兼容规则（`AGENTS.md` §2.7）

**允许（向后兼容增量）**：

- 新增**可选**字段（接收方必须忽略未知字段）；
- 新增 `type`（接收方必须忽略未知 `type` 且不断连）；
- 新增错误码（未知错误码按通用错误提示，不崩溃）；
- 现有字段的取值增加（枚举新增值），但不改变已有值语义。

**不允许**：

- 删除字段、重命名字段、改变字段类型或必填性；
- 改变已有字段/帧的语义（如 `seq` 的含义、`ack` 的触发时机）；
- 复用已废弃的 `type`。

**破坏性变更**：必须升 `protocolVersion`（+1），旧客户端提示升级；服务端至少兼容一个旧客户端版本（见 `docs/project-overview.md` §14）。

### 8.3 数据库兼容规则

- `protocol/schema.sql` 是表结构唯一来源，与 EF Core 迁移（`server/Chat.Server/Data/Migrations`）同步维护。
- 只允许增量：新增表、新增列（可空或带默认值）、新增索引；不允许删列、改列语义、改主键。
- 迁移启动时自动执行且必须幂等；上线前在备份副本上演练。

### 8.4 变更顺序

1. 先改 `protocol/protocol.md`（帧）与 `protocol/schema.sql`（表）；
2. 同步 `docs/internal-plan.md` / `docs/project-overview.md` 受影响段落；
3. 再改服务端 `Chat.Protocol` DTO、`Chat.Server` 实现与前端类型；
4. 不允许「实现先行、文档后补」。

## 9. 与实现及其它文档的关系

| 位置 | 职责 |
|---|---|
| `protocol/protocol.md`（本文件） | 帧、字段、错误码、时序、兼容规则的唯一来源 |
| `protocol/schema.sql` | 表结构、约束与索引的唯一来源 |
| `server/Chat.Protocol/` | 帧 DTO 与枚举（服务端与前端共享契约） |
| `apps/web/src/core/ws.ts`、`core/sync.ts` | 连接状态机、心跳、重连、发送队列、补拉去重 |
| `AGENTS.md` §5 | 消息可靠性硬要求（seq、幂等、补拉、未读、单连接、心跳） |
| `docs/internal-plan.md` §4 | 协议与数据模型的设计依据 |
| `docs/project-overview.md` §11.1 | 协议可靠性约定（与 `AGENTS.md` §5 一致） |

## 附录：变更记录

| 版本 | 日期 | 变更 |
|---|---|---|
| v1（M0-02） | 2026-10-25 | 初版：传输格式、17 个帧、错误码、三条时序、文件消息格式、兼容规则；`protocolVersion = 1` |
| v1（M1-11 校准） | 2026-10-25 | 按 M1 实现补注（帧/字段语义未变）：纯文本消息上限 4096 字符，超出回 `error`（`MESSAGE_TOO_LARGE`）；`conv.list` 响应按最近消息时间倒序（无消息按创建时间）；M1 服务端已处理帧为 `auth`/`hello`/`ping`/`pong`/`message.send`/`message.ack`/`message.new`/`sync.request`/`sync.response`/`conv.list`/`conv.updated`/`kicked`/`error`，`read.update`/`read.updated`/`typing`/`presence` 保留至 M3（当前服务端按未处理帧忽略）；`protocolVersion` 仍为 `1` |
