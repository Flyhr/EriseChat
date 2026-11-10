# EiseChat WebSocket 协议 v1

> 本文件是**协议帧与字段的唯一来源**（`AGENTS.md` §1/§5）；表结构唯一来源为 `protocol/schema.sql`。
> 依据：`docs/internal-plan.md` §4.2 / §4.3（计划 V2.3.5）与 `docs/project-overview.md` §11.1。
> 版本：`protocolVersion = 1`（2026-10-25 初版）；变更必须先改本文件与计划文档，再改实现，且只允许向后兼容的增量（见 §8）。

## 1. 传输与连接

- **地址**：`wss://<域名>/ws`（开发期可用 `ws://127.0.0.1:6580/ws`）；REST 与 WebSocket 同域。
- **编码**：UTF-8（无 BOM）；一条 WebSocket 文本消息 = 一帧；**一行一帧**，JSON 序列化后不得换行或格式化。
- 只接受文本帧；二进制帧忽略并记日志。
- **单帧上限 256 KiB**；超过时服务端关闭连接（close code `1009`）。
- **连接生命周期**：
  1. 客户端建立连接后 **10 秒内发送 `auth`（必须是首帧）**，否则服务端发送 `error`（`AUTH_REQUIRED`）并关闭（`4002`）。
  2. 服务端校验 JWT / `deviceId` → 回 `hello`；失败回 `error` 后关闭。
  3. 进入就绪态，服务端开始推送帧。
- **多端在线（F-22）**：同一账号最多允许 **3 个**同时在线连接（`App:MaxConnectionsPerUser` 可配，默认 3，取值裁到 1~10）。认证成功时若该账号连接数超过上限，服务端踢「最早建立」的连接（先发 `kicked` 帧，再以 `4001` 关闭），其余连接不受影响；不按设备去重（同一浏览器多标签页各算一条）。断线不删数据，靠 `seq` 补拉。
- **心跳**：客户端每 25 秒发 `ping`（带 `ts`）；30 秒内未收到 `pong` 判定断线。服务端也可主动发 `ping`，客户端必须立即回 `pong`。
- **重连**：断线后按 `1s / 2s / 5s / 10s / 30s` 指数退避，每次叠加 0~500ms 随机抖动；重连成功后必须补拉（见 §6.2）。
- **关闭码**（WebSocket close code）：

| close code | 场景 | 客户端处理 |
|---|---|---|
| `1000` | 正常关闭 | 不重连 |
| `1001` | 服务端维护/重启 | 按退避重连 |
| `1009` | 单帧超过 256 KiB | 修正后重连 |
| `4001` | 被服务端踢下线（`kicked` 已先行）：同账号连接数超上限时踢最早连接，或在「登录设备」中远程退出 | 停止自动重连，清理登录态并回登录页 |
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

## 3. 帧总表（与计划 §4.2 一一对应，共 23 个 type）

| type | 方向 | 用途 | 备注 |
|---|---|---|---|
| `auth` | C→S | 首帧认证 | 必须为连接后的第一帧 |
| `hello` | S→C | 认证成功 | 返回连接参数与协议版本 |
| `ping` | 双向 | 心跳请求 | 客户端 25 秒一次 |
| `pong` | 双向 | 心跳响应 | 原样回传 `ts` |
| `message.send` | C→S | 发送消息 | 带 `clientMsgId` 幂等键 |
| `message.ack` | S→C | 落库回执 | 仅发送者连接收到 |
| `message.new` | S→C | 新消息广播 | 会话成员（发送连接除外） |
| `message.recall` | C→S | 撤回消息 | 仅发送者本人、120 秒窗口内（F-18） |
| `message.recalled` | S→C | 撤回广播 | 会话全部在线连接，含操作者连接（F-18） |
| `message.edit` | C→S | 编辑文本消息 | 仅发送者本人、900 秒窗口内（F-18） |
| `message.edited` | S→C | 编辑广播 | 会话全部在线连接，含操作者连接（F-18） |
| `sync.request` | C→S | 补拉请求 | 每会话携带 `lastSeq` |
| `sync.response` | S→C | 补拉响应 | 每会话每批 ≤200 条 |
| `conv.list` | 双向 | 会话列表 | C→S 请求 / S→C 返回全量 |
| `conv.updated` | S→C | 会话变更推送 | 新建、标题/公告/群头像变更、群置顶消息变更、成员、最新消息变化 |
| `read.update` | C→S | 上报已读游标 | 打开会话滚到底部时上报 |
| `read.updated` | S→C | 已读广播 | 单聊推对方成员；本人其他连接也同步（多端已读） |
| `typing` | 双向 | 正在输入 | 3 秒节流，不落库 |
| `presence` | S→C | 在线状态 | 有共同会话或好友关系 |
| `friend.changed` | S→C | 好友/申请变更推送 | 客户端按 `kind` 更新本地好友与申请状态 |
| `favorite.changed` | S→C | 个人收藏变更推送 | 同账号全部在线连接；客户端重新拉取收藏列表（F-38） |
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
| `msgType` | string | 是 | `text` / `image` / `file` / `voice`；`system` 仅服务端生成，客户端发送会被拒绝 |
| `content` | string / object | 是 | `text` 为字符串；`image` / `file` 为 FileContent（见 §7）；`voice` 为 VoiceContent（见 §7）；大小受服务端配置限制（当前 v1 实现：纯文本 ≤ 4096 字符，超出回 `error`（`MESSAGE_TOO_LARGE`）） |
| `replyTo` | string | 否 | 被回复消息的 `messageId`；必须为**同一会话内已存在**的消息，否则回 `error`（`VALIDATION_FAILED`），不落库 |

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
| `reply` | object | 否 | 回复引用摘要（见下「Reply 对象」）；仅当本次为回复且被引用消息存在时返回 |

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
| `msgType` | string | 是 | `text` / `image` / `file` / `voice` / `system` |
| `content` | string / object | 是 | 按 `msgType` 解释，见下 |
| `replyTo` | string | 否 | 被回复消息的 `messageId` |
| `reply` | object | 否 | 回复引用摘要（见下「Reply 对象」）；不落库、按需组装，仅当 `replyTo` 指向的消息存在时返回；旧客户端忽略即可 |
| `createdAt` | integer | 是 | 创建时间（UTC 毫秒） |
| `deletedAt` | integer | 否 | 撤回时间（UTC 毫秒）；非空表示已撤回，此时 `content` 为空文本、不再返回 `fileId` 等文件信息（F-18） |
| `editedAt` | integer | 否 | 最近一次编辑时间（UTC 毫秒）；未编辑过则缺省（F-18） |

**Reply 对象（回复引用摘要，`Message.reply` / `message.ack.reply`，均可选）**：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `messageId` | string | 是 | 被引用消息 ID（= 发送时的 `replyTo`） |
| `seq` | integer | 是 | 被引用消息在会话内的序号 |
| `senderId` | string | 是 | 被引用消息的发送者用户 ID |
| `msgType` | string | 是 | 被引用消息类型：`text` / `image` / `file` / `voice` / `system` |
| `text` | string | 否 | 文本摘要：`text` 取原文前 120 字符（超出截断并追加 `…`）；`system` 取系统文案；`voice` 固定「[语音]」；`image` / `file` 不返回 |
| `fileId` | string | 否 | `image` / `file` 消息的文件 ID（§7 FileContent.fileId）；`voice` 不返回 |
| `name` | string | 否 | `image` / `file` 消息的文件名；`voice` 不返回 |

规则：

1. `reply` **不落库**，服务端由 `messages.reply_to`（存被引用消息的 `messageId`）按需组装；`replyTo` 指向的消息已不存在（如物理删除导致外键置 NULL）时省略 `reply`。
2. 摘要截断在服务端完成（120 字符，按 Unicode 码点计，超出追加 `…`）；客户端展示时可再按自身宽度截断，但不得改写语义。
3. `message.send.replyTo` 必须为同一会话内**已存在**的 `messageId`；不存在或属于其它会话 → 回 `error`（`VALIDATION_FAILED`，文案「replyTo 指向的消息不存在或不属于该会话」），不落库。
4. `ack` 与 `message.new`、历史分页、`sync.response` 中的 Message 都携带同一套 `reply` 摘要；旧客户端忽略新增可选字段即可，帧 `type` 与 `protocolVersion` 不变（向后兼容增量）。

`content` 按 `msgType` 的格式：

- `text`：字符串（纯文本，客户端渲染时必须转义，禁止注入 HTML）。
- `image` / `file`：FileContent 对象，格式见 §7。
- `voice`：VoiceContent 对象（`{ fileId, durationMs }`），格式与校验见 §7。
- `system`：服务端生成的系统事件对象（M3 使用），核心字段为 `event`（事件名，必填）、`actorId`（触发者，可选）、`targetUserIds`（涉及成员，可选）、`text`（服务端生成的中文描述，可选）；后续新增可选字段属向后兼容增量。
- 系统消息事件名（v1，M3）：`group.created`、`member.joined`、`member.left`、`member.removed`；`text` 为服务端生成的中文描述，客户端渲染为居中小字，不做 i18n。
- F-24 追加系统消息事件 `owner.transferred`（群主转让）：`actorId` 为原群主、`targetUserIds` 为新群主 userId、`text` 形如「张三 将群主转让给 李四」；旧客户端按未知事件渲染为「系统消息」，符合向后兼容规则。

#### 消息撤回 / 编辑（`message.recall` / `message.recalled` / `message.edit` / `message.edited`，F-18）

`message.recall`（C→S）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `type` | string | 是 | 固定 `message.recall` |
| `reqId` | string | 否 | 建议携带，失败时 `error` 回传 |
| `conversationId` | string | 是 | 目标会话 ID |
| `messageId` | string | 是 | 待撤回消息 ID |

`message.recalled`（S→C）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `type` | string | 是 | 固定 `message.recalled` |
| `conversationId` | string | 是 | 会话 ID |
| `messageId` | string | 是 | 被撤回消息 ID |
| `seq` | integer | 是 | 被撤回消息的会话内序号 |
| `deletedAt` | integer | 是 | 撤回时间（UTC 毫秒） |
| `operatorId` | string | 是 | 操作者用户 ID（当前仅发送者本人） |

`message.edit`（C→S）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `type` | string | 是 | 固定 `message.edit` |
| `reqId` | string | 否 | 建议携带，失败时 `error` 回传 |
| `conversationId` | string | 是 | 目标会话 ID |
| `messageId` | string | 是 | 待编辑消息 ID |
| `content` | string | 是 | 新文本内容，trim 后 1..4096 字符 |

`message.edited`（S→C）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `type` | string | 是 | 固定 `message.edited` |
| `conversationId` | string | 是 | 会话 ID |
| `messageId` | string | 是 | 被编辑消息 ID |
| `seq` | integer | 是 | 被编辑消息的会话内序号 |
| `content` | string | 是 | 编辑后的文本内容 |
| `editedAt` | integer | 是 | 编辑时间（UTC 毫秒） |
| `operatorId` | string | 是 | 操作者用户 ID（当前仅发送者本人） |

规则：

1. 时间窗口自消息 `createdAt` 起算（服务端 `AppOptions` 可配置）：撤回 **120 秒**、编辑 **900 秒**；超出窗口 → `error`（`VALIDATION_FAILED`，文案「超过可撤回时间」/「超过可编辑时间」）。
2. 撤回仅允许**发送者本人**，`msgType` ∈ `text` / `image` / `file`；编辑仅允许**发送者本人**、仅 `msgType=text`。非发送者 → `error`（`FORBIDDEN`）；消息不存在或不属于该会话 → `error`（`NOT_FOUND`）；`system` 消息或已撤回消息编辑 → `error`（`VALIDATION_FAILED`）。
3. 撤回幂等：已撤回消息再次 `message.recall` 视为成功，仅向请求连接重发一条 `message.recalled`（`deletedAt` 不变），不重复广播、不重复落库。
4. 撤回后服务端返回的 Message：`content` 为空文本、不再携带 `fileId` / `name` 等文件信息；`messages.file_id` 保留供孤儿清理引用，文件不立即删除。
5. 编辑内容 trim 后必须 1..4096 字符（按 Unicode 码点计），否则 `VALIDATION_FAILED`；窗口内可多次编辑，每次刷新 `editedAt`；`seq` / `clientMsgId` / 幂等语义不变。
6. 广播：`message.recalled` / `message.edited` 发送给该会话**全部在线连接（含操作者自己的连接）**，用于发送方确认与多端一致；离线成员不推送，重连后以历史/同步数据为准。
7. 历史分页与 `sync.response` 返回最新 `deletedAt` / `editedAt` / `content`；`sync.response` 另通过 `updates` 补齐 `updatedAfter` 之后的状态变化（撤回/编辑），见 §4.4。客户端进入会话时仍会拉取最新一页作为兜底，但不再依赖它补齐离线变更。
8. `reply` 摘要：被引用消息已撤回时 `reply.text` 固定为「[已撤回]」、`messageId` 保留（点击可定位到占位行）。



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
| `updatedAfter` | integer | 否 | 客户端上次完成状态同步的服务端时间（UTC 毫秒）；缺省或 `0` 表示不补状态变化 |

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
| `updates` | array | 否 | 状态变化消息：该会话中 `edited_at`/`deleted_at` 晚于 `updatedAfter` 的消息；`seq` 不变，仅用于覆盖本地缓存；缺省表示没有 |
| `updatedThrough` | integer | 否 | 下次请求可用的 `updatedAfter`（服务端时钟，UTC 毫秒） |

规则：

1. 只返回 `seq > lastSeq` 的消息，按 `seq` 升序；**每会话每批最多 200 条**。
2. `hasMore = true` 时客户端应以「已收到的最大 `seq` 作为新 `lastSeq`」继续发 `sync.request`，直到 `hasMore = false` 追平。
3. `lastSeq` 必须满足 `0 ≤ lastSeq ≤ 会话当前 lastSeq`，否则回 `error`（`VALIDATION_FAILED`）。
4. `sync.request` 是只读操作，可重复调用；客户端仍需按 `messageId` 去重、按 `seq` 排序。
5. 补拉只返回消息，不返回会话元数据；会话元数据由 `conv.list` / `conv.updated` 维护。
6. `cursors[].updatedAfter`（F-34）用于补齐已缓存消息的撤回/编辑：服务端把 `edited_at` 或 `deleted_at` 晚于该时间的消息放入对应 batch 的 `updates`，按状态变更时间升序，随批一次性返回（状态变化量远小于消息量，不参与 `messages` 的每批 ≤200 条分页）。`updates` 中的消息 `seq` 不变，客户端只能按 `messageId` 覆盖已缓存消息，不得据此新增或重排消息。
7. 客户端收到 batch 后把本会话 `updatedAfter` 更新为 `updatedThrough`（即使 `updates` 为空；缺省表示未请求状态同步）。`updatedThrough` 由服务端时钟产生，客户端不得使用本地时钟。
8. 只有 `messages` 与 `updates` 都为空的会话不返回 batch；`hasMore` 仍只描述 `messages`（seq 补拉）是否还有更早未返回消息。

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
| `conversations` | array | 是 | 发生变化的 Conversation 数组；被移出/主动退出场景可为空数组 |
| `removedConversationIds` | string[] | 否 | 当前连接用户被移出/主动退出群聊时，服务端推送该帧，`conversations` 可为空数组，`removedConversationIds` 列出被移除的会话；客户端收到后移除对应会话的本地状态（列表项、消息缓存、游标） |

规则：触发场景为「新建会话、标题变更、公告变更、群头像变更、群置顶消息设置/替换/取消（含置顶消息被撤回）、成员加入/退出、新消息导致 `lastSeq` / `lastMessage` 变化、当前用户手动标为未读/已读」。客户端按 `conversationId` 合并（upsert）；同一变化可能同时收到 `message.new` 与 `conv.updated`，客户端需去重。被移出/主动退出场景下，该用户只收到 `removedConversationIds` 帧，不再收到该会话的 `conv.updated`。群资料（标题/公告）变更时服务端按**收件人视角**逐连接下发 `conv.updated`（每个成员的 `pinned` / `muted` / `lastReadSeq` 只反映自己），不会互相覆盖。

群聊创建与成员管理走 REST（见计划 §7）；成功后服务端在同一事务内写一条 `msgType=system` 消息（`seq` 严格 +1），并推送 `message.new`（在线成员）与 `conv.updated`（含全体成员的最新 Conversation）；客户端对 `message.new` 按 `messageId` 去重。

**会话对象（Conversation）**：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `conversationId` | string | 是 | 会话 ID |
| `type` | string | 是 | `direct`（单聊）/ `group`（群聊） |
| `title` | string | 是 | 群标题；单聊为空字符串（客户端用对方昵称展示） |
| `createdAt` | integer | 是 | 创建时间（UTC 毫秒） |
| `lastSeq` | integer | 是 | 会话最新序号 |
| `lastReadSeq` | integer | 是 | 当前用户在该会话的已读游标 |
| `unread` | integer | 否 | 当前用户的未读数：只统计**他人发送**且 `seq > lastReadSeq` 的消息（自己发送的消息不计入）；服务端实时计算，客户端可直接展示；旧客户端忽略 |
| `markedUnread` | bool | 否 | 当前用户是否手动「标为未读」（成员级设置，默认 `false`）：只影响未读筛选与提示，**不改** `lastReadSeq`、不撤销对端已读回执、不增加 `unread`；旧客户端忽略 |
| `pinnedMessage` | object / null | 否 | 群置顶消息快照（Message）；仅 `group` 可能有值，单聊恒为 `null`；服务端只在消息存在且未撤回时下发，`null` 表示无置顶（F-36）；旧客户端忽略 |
| `pinned` | bool | 否 | 当前用户是否置顶该会话（成员级设置，默认 `false`；旧客户端忽略） |
| `muted` | bool | 否 | 当前用户是否对该会话免打扰（成员级设置，默认 `false`；旧客户端忽略） |
| `announcement` | string / null | 否 | 群公告（≤500 字符，支持换行）；`null` / 缺省表示无公告；单聊恒为 `null`；旧客户端忽略 |
| `avatarFileId` | string / null | 否 | 群头像 `files.id`；仅 `group` 可能有值，单聊恒为 `null`；`null` / 缺省 = 未设置（F-23）；旧客户端忽略 |
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

> 未读数由服务端在会话对象 `unread` 字段实时计算并下发：只统计他人发送、`seq > lastReadSeq` 的消息，自己发送的消息不计入（例如向未打开的会话转发消息后，自己不会收到未读徽标）。客户端不应再用 `lastSeq − lastReadSeq` 估算（会把本人消息误计为未读）；该字段可由旧客户端忽略（旧客户端仍会按旧公式展示）。

> `pinned` / `muted` / `markedUnread` 是**当前登录用户自己的成员级设置**（`conversation_members` 行上的列）：服务端在 `conv.list`、`conv.updated`、`GET /api/conversations` 中按各自收件人填充，不广播给其他成员的其它设备；改动入口为 `PATCH /api/conversations/{conversationId}/member-settings`（body `{pinned?, muted?, markedUnread?}`，至少一项）。其中 `markedUnread` 变更时，服务端向**该用户本人的全部在线连接**推送一次按本人视角构建的 `conv.updated`（同账号多端同步），不向会话其他成员推送；`read.update` 不清除 `markedUnread`，只有当客户端显式 PATCH `markedUnread=false`（打开会话或「标为已读」）时清除。会话排序规则属客户端展示层：`conv.list` 仍按最近消息时间倒序，客户端可将置顶会话排在前面（置顶组内保持时间倒序）。

> `pinnedMessage` 为群置顶消息（F-36）。设置/替换入口为 `PUT /api/conversations/{conversationId}/pinned-message`（body `{ "messageId": "<messages.id>" }`，仅群主，单聊不支持），取消入口为 `DELETE /api/conversations/{conversationId}/pinned-message`（仅群主）；成功后按收件人视角逐连接推送 `conv.updated`。每个群最多一条置顶；目标消息必须属于该群且未撤回。置顶消息被撤回时服务端自动清空并在 `message.recalled` 之后推送 `conv.updated`，客户端不得继续展示旧快照。与会话级 `pinned`（F-10）互不影响。错误：会话不存在 `NOT_FOUND`（404）、非群聊 `VALIDATION_FAILED`（400）、非成员 `NOT_MEMBER`（403）、非群主 `FORBIDDEN`（403）、消息不存在或不属于该群 `NOT_FOUND`（404）、已撤回 `VALIDATION_FAILED`（400）。
>
> `announcement` 为群公告，只有 `group` 会话可能有值；修改入口为 `PATCH /api/conversations/{conversationId}`（body `{title?, announcement?, avatarFileId?}`，仅群主可用，空串表示清空（落库 `null`））。
>
> `avatarFileId` 为群头像，只有 `group` 会话可能有值；修改入口同上 `PATCH /api/conversations/{conversationId}`（空串表示清空、缺省表示不修改）；下载走 `GET /api/files/{fileId}`，头像文件可见性规则与个人头像一致（已登录用户可下载、未登录 401）。
>
> 群主转让入口为 `POST /api/conversations/{conversationId}/owner`（body `{ "userId": "<新群主 userId>" }`，仅当前群主可用）：成功后两条成员行 `role` 互换（原群主 → `member`、目标 → `owner`），追加一条 `owner.transferred` 系统消息（`actorId` = 原群主、`targetUserIds` = [新群主]，`seq` 仍严格 +1），并按收件人视角逐连接下发 `conv.updated`（各自 `pinned` / `muted` / `lastReadSeq` 不变）。错误：会话不存在 `NOT_FOUND`（404）、非群聊 `VALIDATION_FAILED`（400）、非会话成员 `NOT_MEMBER`（403）、非群主 `FORBIDDEN`（403）、目标非本群成员或目标为自己 `VALIDATION_FAILED`（400）。

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
3. `read.updated` 推送范围：单聊推送给**对方成员**的所有在线连接；同时推送给**上报者本人的其他在线连接**（排除上报的来源连接，用于多端已读同步），不回显给上报连接本身。群聊 v1 不做群已读详情，只更新游标、**不向其他成员广播**，但仍推给本人其他连接。
4. 新成员加入（建群、被邀请、重新邀请）时，`conversation_members.last_read_seq` 初始化为**加入前的会话 lastSeq**：加入系统消息计 1 条未读，加入前的历史消息不计未读；之后仍按上面的「只增不减」与越界校验规则更新。

### 4.7 `typing` / `presence`

`typing`（双向）：

| 方向 | 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|---|
| C→S | `type` | string | 是 | 固定 `typing` |
| C→S | `conversationId` | string | 是 | 目标会话；`userId` 以连接身份为准，客户端无需发送 |
| S→C | `type` | string | 是 | 固定 `typing` |
| S→C | `conversationId` | string | 是 | 会话 ID |
| S→C | `userId` | string | 是 | 正在输入的用户 ID |

规则：客户端 3 秒节流；服务端只转发给会话内其他成员（群聊同样转发），跳过发送连接，不落库；服务端对「每个连接 × 每个会话」做 **1 秒节流**，超频静默丢弃、不回 `error`；接收端 3 秒内未收到同一用户的新 `typing` 则自动清除指示。

`presence`（S→C）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `type` | string | 是 | 固定 `presence` |
| `userId` | string | 是 | 用户 ID |
| `online` | boolean | 是 | 是否在线 |

规则：仅在该用户连接数 **0→1** 时推送 `online=true`、**1→0** 时推送 `online=false`；推送给与其有共同会话或好友关系的其他在线用户（去重），不落库、不查询离线状态；客户端方向错误（如 C→S 发送 `presence`）按「忽略并记 debug」处理，不断连；v1 不做频繁状态刷新。

### 4.8 `kicked` / `error`

`kicked`（S→C）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `type` | string | 是 | 固定 `kicked` |
| `reason` | string | 是 | 原因描述，如「账号在其他设备登录」 |

规则：`kicked` 用于服务端主动踢下线，两种场景：①同账号连接数超过上限（`App:MaxConnectionsPerUser`，默认 3）时，踢「最早建立」的连接，`reason` 为「账号连接数超出上限（最多 3 端同时在线）」；②在「登录设备」列表中远程退出某设备，`reason` 为「该设备已被退出登录」。服务端发送 `kicked` 后以 `4001` 关闭该连接；客户端必须停止自动重连、清理登录态并回登录页。

`error`（S→C）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `type` | string | 是 | 固定 `error` |
| `code` | string | 是 | 错误码，见 §5 |
| `message` | string | 是 | 可展示的中文说明（不包含敏感信息） |
| `reqId` | string | 否 | 回传对应请求的 `reqId` |

规则：非致命错误只回 `error`，连接保持；致命错误先回 `error`（若连接仍可写），再关闭（close code 见 §5）。

### 4.9 `friend.changed`（好友/申请变更推送）

`friend.changed`（S→C）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `type` | string | 是 | 固定 `friend.changed` |
| `kind` | string | 是 | 变更类型，见下表 |
| `friend` | object / null | 否 | 好友对象快照，见下表；`kind` 为 `request.*` 时可缺省 |
| `request` | object / null | 否 | 申请对象快照，见下表；`kind` 为 `friend.*` 时可缺省 |

`kind` 取值：

| kind | 含义 | 附带对象 | 客户端处理 |
|---|---|---|---|
| `request.received` | 收到新的好友申请 | `request` | 加入「收到的申请」列表，提示一次 |
| `request.accepted` | 自己发出的申请被接受 | `request` + `friend` | 移出「发出的申请」，加入好友列表 |
| `request.rejected` | 自己发出的申请被拒绝 | `request` | 移出「发出的申请」 |
| `request.cancelled` | 对方撤销了发给自己的申请 | `request` | 移出「收到的申请」 |
| `friend.added` | 新增好友关系 | `friend` | 加入好友列表 |
| `friend.removed` | 好友关系被删除（任意一方删除） | `friend`（移除前快照） | 移出好友列表 |
| `friend.updated` | 自己的好友备注/分组变更 | `friend` | 更新对应好友行 |

**好友对象（Friend）**：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `userId` | string | 是 | 好友用户 ID |
| `displayName` | string | 是 | 好友昵称 |
| `email` | string | 是 | 好友邮箱（内部版可直接下发） |
| `remark` | string / null | 否 | 我对该好友的备注（空表示无备注） |
| `groupName` | string / null | 否 | 我对该好友的分组名（空表示未分组） |
| `avatarFileId` | string | 否 | 头像文件 ID |
| `createdAt` | integer | 是 | 成为好友的时间（UTC 毫秒） |

**申请对象（FriendRequest）**：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `requestId` | string | 是 | 申请 ID |
| `fromUserId` | string | 是 | 申请人用户 ID |
| `fromDisplayName` | string | 是 | 申请人昵称 |
| `toUserId` | string | 是 | 被申请人用户 ID |
| `toDisplayName` | string | 是 | 被申请人昵称 |
| `message` | string / null | 否 | 附言（≤100 字符） |
| `status` | string | 是 | `pending` / `accepted` / `rejected` / `cancelled` |
| `createdAt` | integer | 是 | 申请时间（UTC 毫秒） |
| `handledAt` | integer / null | 否 | 处理时间（UTC 毫秒） |

规则：

1. `friend.changed` 是**推送帧，不落库**；服务端在好友/申请数据变化的事务提交后推送，离线用户不补推，客户端重连/启动后用 REST（计划 §7）全量拉取并覆盖本地状态。
2. 未知 `kind` 或未知字段按忽略处理，不报错、不断连（§8.2）。
3. 同一变化可能产生多条推送（如自动接受会推 `request.accepted` + `friend.added`），客户端需按 `kind` 幂等处理。
4. `request.accepted` 推给申请人（附 `friend`），`request.rejected` 推给申请人；`request.cancelled` 推给被申请人；`friend.added` / `friend.removed` 推给双方；`friend.updated` 仅推给发起修改的一方。
5. 好友关系与申请数据由服务端维护，客户端不得仅本地修改（数据归属见 `AGENTS.md` §2.8）。

### 4.10 `favorite.changed`（个人收藏变更推送）

`favorite.changed`（S→C，F-38）：当前账号在某台设备新增/取消收藏后，服务端向该账号全部在线连接推送，
用于多端同步收藏列表与消息菜单状态；不落库、离线用户不补推，客户端登录/重连后用 `GET /api/favorites` 全量恢复。

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `type` | string | 是 | 固定 `favorite.changed` |
| `messageId` | string | 是 | 发生变化的原消息 ID |
| `favorited` | boolean | 是 | `true`=新增收藏；`false`=取消收藏 |
| `changedAt` | integer | 是 | 变更时间（UTC 毫秒） |

规则：

1. 只推送给**收藏归属账号本人**的全部连接，不推送给会话其他成员。
2. 客户端收到后可重新拉取收藏列表（幂等），不要仅凭帧增删本地快照，以免错过编辑/撤回导致的不可用状态。
3. 未知字段忽略；旧客户端忽略未知 type。

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

3. **下载**：`GET /api/files/{fileId}`，需登录且请求者必须是引用该文件的会话成员；流式返回，不暴露磁盘路径。只认**未撤回**的引用消息作为会话成员访问依据（F-38 收尾）；上传者本人、个人/群头像等独立授权仍按原规则放行。
4. **图片**：客户端发送前压缩（最长边 1280px、约 ≤300KB）；图片消息与文件消息共用 FileContent。
5. **存储**：`/data/files/yyyyMM/{fileId}{ext}`（逻辑路径，实际目录见 `appsettings.json`）；孤儿文件由后台任务清理。
6. **引用持久化**：服务端在 `messages.file_id` 保存 image/file 消息引用的文件 ID（可空支撑列，不属于帧字段），用于下载时的会话成员鉴权与孤儿文件清理；帧格式不变。
7. **发送校验与内容归一（v1）**：服务端校验 image/file 消息的 `content.fileId` 必须存在于 `files` 表，且 `files.owner_id` 为发送者本人；不存在或不属于本人时回 `error`（`VALIDATION_FAILED`，语义为「文件不存在或无权使用该文件」，不区分两种情况，也不单独下发 403）。落库与广播时 `content` 的 `name`/`size`/`mime` 以 `files` 记录（`original_name`/`size`/`mime`）为准，客户端传入值不作为权威值；同时把文件 ID 写入支撑列 `messages.file_id`（仅 image/file 消息非空，不属于帧字段）。

8. **共享文件汇总（REST，v1，F-15）**：`GET /api/conversations/{conversationId}/files?type=image|file&beforeSeq={seq}&limit={n}`，需登录且为会话成员；`type` 必填（`image` / `file`，其他 400），`limit` 1~100 默认 50，`beforeSeq` 可选正整数（排他，只取 seq 小于它的引用）。只统计 `msg_type` 为 image/file（**不含 voice**）且 `messages.file_id` 非空的引用消息，因此头像、未发送的上传文件天然排除；同一 `fileId` 被多次发送只返回一次，取**最新一条引用消息**（max seq），游标即该 seq；响应 `{ "items": [{ "fileId", "name", "size", "mime", "messageId", "messageSeq", "senderId", "createdAt" }], "hasMore": bool }`，items 按 `messageSeq` 降序。非成员 403、会话不存在 404、非法 type/limit/beforeSeq 400。不新增帧、不改 `protocolVersion`。
9. **语音消息（v1，F-28）**：`msgType` 新增 `voice`（向后兼容增量）。`content` 为 VoiceContent：
   - `fileId`：string，必填，音频文件 `files.id`，归属与发送者校验规则同 image/file（必须为本人上传，否则 `VALIDATION_FAILED`）；
   - `durationMs`：integer，必填，语音时长（毫秒），范围 **1000 ~ 60000**，由客户端测量上报，服务端只做范围校验、不作为可信计时。
   上传仍走 `POST /api/files`（multipart）：允许扩展名 `.webm`、MIME `audio/webm`、魔数 `1A 45 DF A3`（EBML/WebM），单文件 ≤ **5MB**（语音大小上限在消息发送校验中执行；其它文件默认上限仍为 50MB）。落库时 `content` 的 `name`/`size`/`mime` 以 `files` 记录为准，`messages.file_id` 写入该文件 ID；撤回规则与 image/file 相同（发送者本人、2 分钟窗口），撤回后 `content` 为空文本、不再返回文件信息。广播、历史分页与 `sync.response` 原样返回 content_json，不新增帧字段。旧客户端收到 `voice` 时按未知 `msgType` 渲染兜底（建议显示「不支持的消息类型」），不影响其它消息。

10. **客户端更新检查（REST，v1，F-29）**：`GET /api/version`，**匿名可访问**（客户端在登录前也可检查），返回更新清单：
    - `version`：string，服务端程序集版本与安装包文件名版本中的最大值（x.y.z）；
    - `protocolVersion`：integer，当前协议版本；
    - `artifacts`：object，固定含 `desktop` 与 `android` 两个键，值可能为 `null`（该平台暂无产物）；非 null 时结构为 `{ version, fileName, url, size, sha256, modifiedAt }`：`fileName` 为安装包文件名（供客户端保存下载文件用）、`url` 为同源相对地址 `/download/files/{fileName}`（客户端必须与当前服务器地址拼接后使用，禁止跨源下载）、`size` 为字节数、`sha256` 为小写十六进制摘要（客户端下载完成后必须校验一致才可执行/安装）、`modifiedAt` 为最近修改时间（UTC 毫秒）。
    - 产物只认 `EiseChat-Setup-x.y.z.exe` 与 `EiseChat-x.y.z.apk`（文件名版本号必须为三段数字），同平台存在多个合法版本时取最近修改的一个；`/download` 下载页行为不变。
    - 该接口不新增帧、不改库表，`protocolVersion` 仍为 1；Web 浏览器端只提示刷新页面，桌面/APK 壳负责下载与安装。

11. **消息搜索（REST，v1，F-36）**：`GET /api/messages/search`，需登录，参数：
    - `q`（必填，1~50 字符）：普通模式匹配文本内容与图片/文件名（image/file 消息的 `content.name`，正文里的 URL 字样按普通文本处理）；`linkOnly=true` 的链接模式只匹配文本消息中识别出的 HTTP/HTTPS URL，不匹配普通正文或文件名；不搜索附件正文、不抓取网页；
    - `conversationId`（可选）：限定会话内搜索，需为会话成员；
    - `senderId`（可选）：按发送人过滤；
    - `from` / `to`（可选，UTC 毫秒）：按发送时间筛选，包含端点；客户端按用户本地日历换算（本地日期 00:00:00.000 ~ 23:59:59.999）后传入；
    - `limit`（1~50，默认 20）；只返回未撤回消息，且必须属于当前用户所在会话。
    - 分页共三种模式，客户端按请求参数选择：
      - **会话内**（带 `conversationId`）：使用 `beforeSeq`，按 `seq` 倒序翻页；不接受 `timeCursor` / `beforeCreatedAt` / `beforeMessageId`。
      - **旧全局**（不带 `conversationId`）：`timeCursor` 缺省或 `false`，使用 `beforeSeq` 按原 `seq` 倒序规则翻页（兼容旧客户端，不重复返回第一页）。
      - **新全局**（不带 `conversationId` 且 `timeCursor=true`）：首页不传游标；之后用上一页响应 `nextCursor` 的 `beforeCreatedAt` + `beforeMessageId`，按 `(createdAt, messageId)` 倒序翻页，同一时间戳的消息不会重复或漏掉。
    - `timeCursor` 为可选布尔参数，缺省按 `false` 处理（HTTP 请求入口不得要求必填）。
    - `linkOnly` 为可选布尔参数，缺省 `false`（普通搜索）；为 `true` 时只匹配识别出的 HTTP/HTTPS URL，分页模式与 `hasMore` 口径不变。
    - `hasMore` 表示按实际匹配结果是否还有下一页：多取一条实际命中判断，最后一页恰好填满时为 `false`；剩余候选被关键词复核过滤后同样为 `false`（普通模式与链接模式一致）。
    - 切换关键词、筛选项或搜索范围时客户端应从首页重新查询。不新增帧、不改库表，`protocolVersion` 仍为 1。

12. **按目标消息定位上下文（REST，v1，F-36）**：`GET /api/conversations/{conversationId}/messages/context`，需登录且为会话成员。参数：
    - `messageId` 或 `seq`（二选一，必须且只能提供一个）：目标消息；
    - `before` / `after`（可选，0~50，默认 25）：目标前后各返回多少条；返回消息按 `seq` 升序，响应 `hasOlder` / `hasNewer` 表示窗口外是否还有更早/更新消息。
    - 用途：置顶消息、搜索结果的跳转定位；只读取历史，不推进客户端的 `lastSeq` / 同步水位，不得以「从最新消息逐页补拉」代替。目标不存在或不属于该会话返回 404。

13. **个人收藏（REST，v1，F-38）**：收藏由服务端保存（SQLite 为事实源），同账号跨 Web/Windows/Android 同步。均为个人数据，只对当前登录账号生效。

    - `GET /api/favorites`：返回当前账号的收藏列表（按收藏时间倒序），响应为数组；元素：
      - `messageId`、`conversationId`、`conversationTitle`、`senderName`：收藏时的来源信息（标题/昵称取当前值）；
      - `favoritedAt`（UTC 毫秒）；`available`（boolean，原消息当前是否仍可访问）；
      - `message`（object|null）：`available=true` 时为原消息最新快照（含编辑后的 `content`/`editedAt`；格式同 `message.new.message`）；`available=false` 时恒为 `null`，客户端不得展示正文、文件名或附件预览，只显示「原消息不可用」。
      - 不可用原因：原消息已撤回、原消息不存在、或当前账号已失去该会话访问权。收藏条目不随消息撤回/退出会话自动删除，由用户手动移除。
    - `POST /api/favorites`：body `{ "messageId": "..." }`，新增收藏。要求原消息存在、当前账号仍为会话成员、消息未撤回且类型为 text/image/file/voice；单账号最多 500 条（超出返回 `VALIDATION_FAILED`，文案「收藏已达上限（最多 500 条）」）。**幂等**：已收藏时直接返回现有条目，不重复写入，可安全重试。
    - `DELETE /api/favorites/{messageId}`：取消收藏。**幂等**：不存在时同样返回 `204`，可安全重试。
    - 成功后服务端向本人全部在线连接推送 `favorite.changed`（§4.10）。收藏只保存「账号 + 原消息引用 + 收藏时间」，不复制消息内容；原消息编辑后列表显示新内容，撤回/失去访问权后转为不可用。
    - 附件仍走 `GET /api/files/{fileId}` 的会话成员鉴权下载，收藏不绕过文件访问权限。旧版仅存本机 IndexedDB 的收藏由客户端提供显式导入入口，逐条提交原消息引用并由服务端逐条校验；导入成功的客户端记录迁移来源，避免重复导入。
    - 客户端登录或重连认证成功后重新拉取列表，同账号多端通过 `favorite.changed` 刷新；500 条上限与新增写入在同一条 SQL 内原子判断（并发不会超过上限），重复收藏幂等成功。

示例（图片消息）：

```json
{"type":"message.send","reqId":"r2","clientMsgId":"c-1b2c-3d4e-5f60-789a-bcdef0123456","conversationId":"cv-1a2b3c4d","msgType":"image","content":{"fileId":"f-12ab34cd","name":"照片.png","size":204800,"mime":"image/png"}}
```

14. **多选消息与附件转发（REST，v1，F-40）**：单条转发 text/image/file/voice 到另一个当前账号仍可访问的会话；不做合并卡片、不做一次请求发往多个目标，也不新增消息类型。

    - `POST /api/messages/forward`：body `{ "messageId": "...", "targetConversationId": "...", "clientMsgId": "<UUID>" }`，成功返回新消息（结构同 `message.new.message`，`clientMsgId` 为请求值）；失败按错误码返回，错误文案不得包含原消息正文。
    - 服务端校验：目标会话存在且转发者是成员；源消息存在且转发者仍是源会话成员；源消息未撤回且非 system；附件消息的 `files` 记录仍存在。失败返回 `NOT_FOUND`（404）/ `FORBIDDEN`（403）/ `VALIDATION_FAILED`（400）。校验、`seq` 分配与消息插入在同一事务内完成，校验后发生的退群/撤回不会导致旧内容被转发。
    - 目标消息按普通消息落库：目标会话内 `seq` 严格 +1；`clientMsgId` 在 `(conversation_id, client_msg_id)` 唯一，重试命中时返回已存在消息且不重复落库/广播；`senderId` 为转发者，`replyTo` 为空，不复制原消息的 `editedAt`。
    - 附件不复制：新消息与原消息引用同一 `files.id`（`messages.file_id`），客户端不重复下载/上传；此后目标会话成员按现有 `GET /api/files/{id}` 鉴权访问附件。原会话失去访问权不影响目标会话成员访问。
    - 广播：落库成功后向目标会话全部在线连接推送 `message.new`（含转发者本人连接，`clientMsgId` 置空，客户端按 `messageId` 去重）；幂等命中不广播。广播为尽力而为：单个在线连接发送失败只记录并跳过，不影响接口成功返回（消息已落库）。

## 8. 版本与兼容规则

### 8.1 协议版本

- 当前 `protocolVersion = 1`，随计划 V2.3.5 发布。
- 服务端在 `hello` 中下发 `protocolVersion`；客户端在 `auth` 中可带上自身版本（缺省视为 1）。
- 版本不兼容时服务端回 `error`（`PROTOCOL_VERSION_MISMATCH`）并关闭；客户端提示升级，不进入聊天。

### 8.2 帧兼容规则（`AGENTS.md` §2.7）

**允许（向后兼容增量）**：

- 新增**可选**字段（接收方必须忽略未知字段）；
- 新增 `type`（接收方必须忽略未知 `type` 且不断连）；
- 新增推送帧的枚举取值（如 `friend.changed` 的 `kind`），接收方必须忽略未知取值；
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
| v1（M3-01 库表增量） | 2026-10-25 | 无帧变更；messages 表新增可选 file_id（引用 files.id，用于下载鉴权与孤儿清理），protocol/schema.sql 同步；protocolVersion 仍为 1 |
| v1（M3-03 校准） | 2026-10-25 | 明确文件消息的 fileId 归属校验、content 以 files 记录为准、messages.file_id 落库；帧格式与 protocolVersion 不变 |
| v1（M3-04 校准） | 2026-10-25 | 新增 conv.updated.removedConversationIds；系统消息事件名 group.created/member.joined/member.left/member.removed；群聊 REST 行为与推送时序；帧格式与 protocolVersion 不变 |
| v1（M3-05 校准） | 2026-10-25 | 明确已读语义（新成员 last_read_seq=加入前 lastSeq；只增不减、越界校验）、typing 服务端 1 秒/连接/会话节流、presence 仅在 0↔1 连接时推送、移出/退出只收 removedConversationIds；帧格式与 protocolVersion 不变 |
| v1（F-17 好友关系） | 2026-10-27 | 新增 `friend.changed` 推送帧（共 18 个 type，§4.9）及 Friend/FriendRequest 对象、7 种 `kind`；presence 推送范围扩展为「有共同会话或好友关系」；新增好友/申请相关 REST（计划 §7）；`protocolVersion` 仍为 1 |
| v1（F-09 回复引用） | 2026-10-27 | 新增可选 Reply 引用摘要（`Message.reply` 与 `message.ack.reply`）：不落库、按需组装，文本摘要 120 字符截断、image/file 带 fileId+name、被引用消息缺失时不返回；明确 `message.send.replyTo` 必须为同会话已存在 messageId，否则 `VALIDATION_FAILED`；帧 type 与 `protocolVersion` 仍为 1 |
| v1（F-10 置顶与免打扰） | 2026-10-27 | Conversation 新增可选字段 `pinned` / `muted`（当前用户成员级设置，默认 false，按收件人填充、不广播）；新增 REST `PATCH /api/conversations/{conversationId}/member-settings`（body `{pinned?, muted?}`，至少一项）；`conversation_members` 新增 `pinned` / `muted` 两列（`protocol/schema.sql` 同步）；置顶排序属客户端展示层，`conv.list` 顺序不变；帧 type 与 `protocolVersion` 仍为 1 |
| v1（F-14 群资料） | 2026-10-28 | Conversation 新增可选字段 `announcement`（群公告，≤500 字符、支持换行，`null` = 无公告，单聊恒为 null）；`conv.updated` 触发场景补充「标题/公告变更」并明确按收件人视角逐连接下发；`conversations` 新增 `announcement TEXT` 列（`protocol/schema.sql` 同步）；新增 REST `PATCH /api/conversations/{conversationId}`（body `{title?, announcement?}`，仅群主）；无新帧、`protocolVersion` 仍为 1 |
| v1（群头像） | 2026-10-28 | Conversation 新增可选字段 `avatarFileId`（群头像 `files.id`，仅 group、单聊恒 null、null=未设置）；`conv.updated` 触发场景补充「群头像变更」；`conversations` 新增 `avatar_file_id TEXT` 列（`protocol/schema.sql` 同步）；`PATCH /api/conversations/{conversationId}` body 增加 `avatarFileId`（空串清空、缺省不修改，仅群主）；头像文件对已登录用户可见；无新帧、`protocolVersion` 仍为 1 |
| v1（F-15 共享文件） | 2026-10-28 | 新增 `GET /api/conversations/{conversationId}/files`（type=image 或 file、beforeSeq、limit）共享文件汇总接口：只统计 `messages.file_id` 非空的 image/file 消息、按文件去重取最新引用 seq、按 seq 降序分页（beforeSeq 排他）；无新帧、不改库表、`protocolVersion` 仍为 1 |
| v1（F-18 撤回与编辑） | 2026-10-28 | 新增 4 个帧（共 22 个 type）：`message.recall` / `message.recalled` / `message.edit` / `message.edited`（§4.3）；撤回窗口 120s、编辑窗口 900s，仅发送者本人，撤回后 content 为空且不再返回文件信息，编辑仅 text、内容 1..4096；Message 新增可选 `editedAt`，`deletedAt` 语义改为「非空=已撤回」；messages 新增 `edited_at INTEGER` 列（`protocol/schema.sql` 同步）；无新 REST 端点，历史/同步返回最新状态；`protocolVersion` 仍为 1 |
| v1（标为未读） | 2026-11-04 | Conversation 新增可选字段 `markedUnread`（成员级设置，默认 `false`）；`conversation_members` 新增 `marked_unread` 列（`protocol/schema.sql` 同步）；`PATCH /api/conversations/{conversationId}/member-settings` body 增加可选 `markedUnread`（至少一项），变更时仅向本人其他在线连接推送按本人视角构建的 `conv.updated`；不改 `last_read_seq`、不撤销对端已读回执、不增加 `unread`；无新帧，`protocolVersion` 仍为 1 |
| v1（群置顶消息） | 2026-11-04 | Conversation 新增可选字段 `pinnedMessage`（群置顶消息快照，仅 group、消息存在且未撤回时下发，null=无置顶）；`conversations` 新增 `pinned_message_id TEXT` 列（`protocol/schema.sql` 同步）；新增 REST `PUT/DELETE /api/conversations/{conversationId}/pinned-message`（仅群主、单聊不支持）；置顶消息被撤回时自动清空并推 `conv.updated`；与会话级 `pinned` 无关；无新帧，`protocolVersion` 仍为 1 |
| v1（F-26 未读口径） | 2026-10-28 | Conversation 新增可选字段 `unread`（当前用户未读数，只统计他人发送且 `seq > lastReadSeq` 的消息，自己发送的消息不计入；服务端实时计算，客户端可直接展示；旧客户端忽略）；无新帧、不改库表、`protocolVersion` 仍为 1 |
| v1（F-38 收藏跨设备同步） | 2026-11-04 | 新增服务端个人收藏（`favorites` 表：账号 + 原消息引用 + 收藏时间，单账号上限 500）与 REST `GET/POST /api/favorites`、`DELETE /api/favorites/{messageId}`（新增/取消幂等）；新增推送帧 `favorite.changed`（共 23 个 type，§4.10），向本人全部在线连接同步；收藏列表只展示仍可访问的原消息，撤回/失去会话访问权显示不可用；收尾：500 上限与新增写入原子执行（并发不超限）、下载鉴权不再以已撤回消息作为成员访问依据、客户端重连后自动刷新；`protocolVersion` 仍为 1 |
| v1（F-40 多选消息与附件转发） | 2026-11-09 | 新增 REST `POST /api/messages/forward`：单条转发 text/image/file/voice 到目标会话（目标会话 `seq` 连续、`clientMsgId` 幂等，重试不重复落库/广播）；附件消息复用原 `files.id`，不复制文件、不要求客户端重新上传，下载鉴权按新消息所在会话判定；服务端校验源消息仍可访问且未撤回、附件仍存在；无新帧，`protocolVersion` 仍为 1 |

| v1（F-24 群主转让） | 2026-10-28 | 系统消息事件新增 `owner.transferred`（群主转让；content 仍为 `{event, actorId, targetUserIds, text}`，`targetUserIds` 为新群主 userId，旧客户端按未知事件回退「系统消息」）；新增 REST `POST /api/conversations/{conversationId}/owner`（body `{userId}`，仅群主，成功后两条成员行 role 互换、追加系统消息、按收件人视角下发 `conv.updated`）；无新帧、不改库表、`protocolVersion` 仍为 1 |
| v1（F-22 多端在线） | 2026-11-01 | 连接策略由「单活动连接」改为「每账号最多 3 个连接」（`App:MaxConnectionsPerUser` 可配，裁到 1~10），超限踢「最早建立」的连接；`read.updated` 增加下发给本人其他连接（多端已读同步），群聊仍不向其他成员广播；`kicked` 帧结构与 `4001` 关闭码不变，仅语义/文案扩展；无新帧、不改库表、`protocolVersion` 仍为 1 |
| v1（F-28 语音消息） | 2026-11-01 | `message.send` 的 `msgType` 新增 `voice`（向后兼容增量），content 为 VoiceContent `{fileId, durationMs}`（durationMs 1000~60000 整数、客户端上报）；新增音频文件规则：`.webm` / `audio/webm` / 魔数 `1A 45 DF A3`、单文件 ≤5MB，上传下载仍走现有 `/api/files`；voice 消息走现有 Message 路径（落库 messages.file_id、共享文件汇总排除 voice、撤回规则同 image/file）；无新帧、不改库表、`protocolVersion` 仍为 1 |
| v1（F-29 客户端更新） | 2026-11-02 | 新增 REST `GET /api/version`（匿名），返回 `{ version, protocolVersion, artifacts: { desktop, android } }`；产物字段 `version` / `url`（同源 `/download/files/{fileName}`）/ `size` / `sha256`（小写十六进制）/ `modifiedAt`（UTC 毫秒）；只认 `EiseChat-Setup-x.y.z.exe` / `EiseChat-x.y.z.apk`，同平台取最近修改的合法产物；客户端下载后必须校验 sha256；无新帧、不改库表、`protocolVersion` 仍为 1 |
