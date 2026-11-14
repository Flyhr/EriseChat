// EriseChat WebSocket 协议类型（protocolVersion = 1）。
//
// 唯一来源：protocol/protocol.md；服务端 DTO 在 server/Chat.Protocol（二者必须一致）。
// 修改规则：先改 protocol/protocol.md 与服务端 DTO，再同步本文件；只允许向后兼容的增量
//（AGENTS.md §2.7、docs/project-overview.md §14）。
// 本文件只是类型镜像，不做运行时校验；帧解析/序列化在 core/ws.ts。
// 协议版本运行时值见 core/config.ts（默认 1，可由 VITE_PROTOCOL_VERSION 覆盖）。

/** 22 个帧 type（protocol.md §3；F-18 新增撤回/编辑 4 个） */
export type FrameType =
  | 'auth'
  | 'hello'
  | 'ping'
  | 'pong'
  | 'message.send'
  | 'message.ack'
  | 'message.new'
  | 'message.recall'
  | 'message.recalled'
  | 'message.edit'
  | 'message.edited'
  | 'sync.request'
  | 'sync.response'
  | 'conv.list'
  | 'conv.updated'
  | 'read.update'
  | 'read.updated'
  | 'typing'
  | 'presence'
  | 'friend.changed'
  | 'favorite.changed'
  | 'kicked'
  | 'error'

export type MessageType = 'text' | 'image' | 'file' | 'voice' | 'system'
/** 客户端可发送的消息类型（system 仅服务端生成，见 protocol.md §4.3） */
export type MessageSendType = 'text' | 'image' | 'file' | 'voice'
export type ConversationType = 'direct' | 'group'
export type MemberRole = 'owner' | 'member'
export type UserRole = 'user' | 'admin'
export type UserStatus = 'active' | 'disabled'

/** WebSocket 错误码（protocol.md §5.1） */
export type WsErrorCode =
  | 'BAD_FRAME'
  | 'AUTH_REQUIRED'
  | 'AUTH_FAILED'
  | 'AUTH_EXPIRED'
  | 'PROTOCOL_VERSION_MISMATCH'
  | 'FORBIDDEN'
  | 'NOT_MEMBER'
  | 'NOT_FOUND'
  | 'VALIDATION_FAILED'
  | 'MESSAGE_TOO_LARGE'
  | 'RATE_LIMITED'
  | 'INTERNAL_ERROR'

/** 账号与 REST 错误码（protocol.md §5.2） */
export type RestErrorCode =
  | 'UNAUTHORIZED'
  | 'CODE_INVALID'
  | 'CODE_EXPIRED'
  | 'CODE_ATTEMPTS_EXCEEDED'
  | 'EMAIL_RATE_LIMITED'
  | 'EMAIL_DOMAIN_NOT_ALLOWED'
  | 'REGISTRATION_DISABLED'
  | 'EMAIL_ALREADY_REGISTERED'
  | 'INVITE_REQUIRED'
  | 'INVITE_INVALID'
  | 'INVITE_EXPIRED'
  | 'PASSWORD_INVALID'
  | 'ACCOUNT_LOCKED'
  | 'ACCOUNT_DISABLED'
  | 'FILE_TOO_LARGE'
  | 'FILE_TYPE_NOT_ALLOWED'

export type ErrorCode = WsErrorCode | RestErrorCode

/** 图片/文件消息内容（protocol.md §7） */
export interface FileContent {
  fileId: string
  name: string
  size: number
  mime: string
  /** 图片像素宽（C2a，仅 image 消息）：用于加载前预留占位，避免列表跳动。 */
  width?: number
  /** 图片像素高（C2a，同上）。 */
  height?: number
  /** 相册批次标识（C2a）：同一次确认发送的多张图片共享；旧端忽略并逐图显示。 */
  albumId?: string
}

/** 语音消息内容（F-28，protocol.md §7 第 9 条）：只传元数据，音频走 REST。 */
export interface VoiceContent {
  fileId: string
  /** 时长（毫秒，1000~60000，客户端测量上报；服务端只做范围校验）。 */
  durationMs: number
}

/** 系统消息内容（服务端生成，M3 使用；核心字段 event 必填） */
export interface SystemContent {
  event: string
  actorId?: string
  targetUserIds?: string[]
  text?: string
}

export type MessageContent = string | FileContent | VoiceContent | SystemContent

/**
 * 回复引用摘要（F-09，protocol.md §4.3「Reply 对象」）：不落库，服务端随消息/ack 按需组装。
 * text 已由服务端截断到 120 字符；图片/文件只带 fileId + name，不带 text。
 */
export interface MessageReply {
  messageId: string
  seq: number
  senderId: string
  msgType: MessageType
  text?: string
  fileId?: string
  name?: string
}

/** 消息对象（protocol.md §4.3） */
export interface Message {
  messageId: string
  conversationId: string
  senderId: string
  clientMsgId?: string
  seq: number
  msgType: MessageType
  content: MessageContent
  replyTo?: string
  /** F-09：被引用消息摘要（服务端组装；不存在/已删除时不返回）。 */
  reply?: MessageReply
  createdAt: number
  deletedAt?: number
  /** F-18：编辑时间（UTC 毫秒）；非空表示编辑过。 */
  editedAt?: number
  /**
   * C3：表情回应摘要（按当前账号计算 mine）；无回应或已撤回消息缺省。
   * 最后一个回应被移除时服务端返回空数组，客户端据此清空旧摘要。
   */
  reactions?: ReactionSummary[]
  reactionRevision?: number
}

/** C3 表情回应摘要（protocol.md §4.3） */
export interface ReactionSummary {
  /** 稳定表情标识（服务端白名单，如 thumbs_up / heart） */
  emoji: string
  count: number
  /** 当前账号是否选择了该表情 */
  mine: boolean
}

/** 会话成员（protocol.md §4.5） */
export interface ConversationMember {
  userId: string
  displayName: string
  /** 邮箱：会话成员投影已不下发（字段省略），仅本人账户接口保留。 */
  email?: string
  role: MemberRole
  joinedAt: number
  avatarFileId?: string
}

/** 会话对象（protocol.md §4.5） */
export interface Conversation {
  conversationId: string
  type: ConversationType
  title: string
  createdAt: number
  lastSeq: number
  lastReadSeq: number
  /** 当前用户是否置顶（F-10：成员级设置，按收件人填充，不广播） */
  pinned?: boolean
  /**
   * 当前用户未读数（F-26）：只统计他人发送且 seq 大于 lastReadSeq 的消息，自己发送的不计入；
   * 旧服务端不返回该字段时，客户端回退到 lastSeq − lastReadSeq 估算。
   */
  unread?: number
  /** 当前用户是否免打扰（F-10：成员级设置，按收件人填充，不广播） */
  muted?: boolean
  /**
   * 当前用户是否手动标为未读（成员级设置）：只影响未读筛选与提示，不改 lastReadSeq、
   * 不撤销对端已读回执、不增加真实未读数；同账号多端通过 conv.updated 同步。
   */
  markedUnread?: boolean
  /** 群置顶消息（F-36）：每群最多一条；已撤回的置顶消息由服务端置空、不再下发 */
  pinnedMessage?: Message | null
  /** 群公告（F-14）：≤500 字符、支持换行；null/缺省 = 无公告；单聊恒为 null */
  announcement?: string | null
  /** 群头像（F-23）：files.id；null/缺省 = 未设置；仅群聊可能非空，单聊恒为 null */
  avatarFileId?: string | null
  lastMessage?: Message | null
  members: ConversationMember[]
}

// ===== 帧定义（protocol.md §4） =====

export interface FrameBase {
  type: FrameType
  /** C→S 请求可携带；服务端在响应帧中原样回传（protocol.md §2） */
  reqId?: string
}

export interface AuthFrame extends FrameBase {
  type: 'auth'
  token: string
  deviceId: string
  protocolVersion?: number
}

export interface HelloFrame extends FrameBase {
  type: 'hello'
  userId: string
  heartbeatSec: number
  serverTime: number
  protocolVersion: number
}

export interface PingFrame extends FrameBase {
  type: 'ping'
  ts: number
}

export interface PongFrame extends FrameBase {
  type: 'pong'
  ts: number
}

export interface MessageSendFrame extends FrameBase {
  type: 'message.send'
  clientMsgId: string
  conversationId: string
  msgType: MessageSendType
  content: string | FileContent | VoiceContent
  replyTo?: string
}

export interface MessageAckFrame extends FrameBase {
  type: 'message.ack'
  clientMsgId: string
  messageId: string
  conversationId: string
  seq: number
  createdAt: number
  /** F-09：回复引用摘要（仅当发送时带 replyTo 且被引用消息存在）。 */
  reply?: MessageReply
}

export interface MessageNewFrame extends FrameBase {
  type: 'message.new'
  message: Message
}

export interface MessageRecallFrame extends FrameBase {
  type: 'message.recall'
  conversationId: string
  messageId: string
}

export interface MessageRecalledFrame extends FrameBase {
  type: 'message.recalled'
  conversationId: string
  messageId: string
  seq: number
  deletedAt: number
  operatorId: string
}

export interface MessageEditFrame extends FrameBase {
  type: 'message.edit'
  conversationId: string
  messageId: string
  content: string
}

export interface MessageEditedFrame extends FrameBase {
  type: 'message.edited'
  conversationId: string
  messageId: string
  seq: number
  content: string
  editedAt: number
  operatorId: string
}

/** C3：回应变化广播（摘要共享、mine 按接收账号计算；空数组表示已无回应）。 */
export interface MessageReactionsFrame extends FrameBase {
  type: 'message.reactions'
  conversationId: string
  messageId: string
  seq: number
  reactions: ReactionSummary[]
  reactionRevision?: number
}

export interface SyncCursor {
  conversationId: string
  lastSeq: number
  /** F-34：上次状态同步的服务端时间（UTC 毫秒）；缺省/0 表示不补撤回编辑 */
  updatedAfter?: number
}

export interface SyncRequestFrame extends FrameBase {
  type: 'sync.request'
  cursors: SyncCursor[]
}

export interface SyncBatch {
  conversationId: string
  messages: Message[]
  /** F-34：状态变化（撤回/编辑）消息；seq 不变，仅覆盖本地缓存 */
  updates?: Message[]
  /** F-34：下次请求可用的 updatedAfter（服务端时钟） */
  updatedThrough?: number
}

export interface SyncResponseFrame extends FrameBase {
  type: 'sync.response'
  batches: SyncBatch[]
  hasMore: boolean
}

/** `conv.list` 请求（C→S）：无额外字段 */
export interface ConvListRequestFrame extends FrameBase {
  type: 'conv.list'
}

/** `conv.list` 响应（S→C）：当前用户全部会话 */
export interface ConvListResponseFrame extends FrameBase {
  type: 'conv.list'
  conversations: Conversation[]
}

export interface ConvUpdatedFrame extends FrameBase {
  type: 'conv.updated'
  /** 发生变化的会话（被移出/主动退出场景可为空数组） */
  conversations: Conversation[]
  /**
   * 当前连接用户被移出/主动退出群聊的会话 ID 列表（protocol.md §4.5）。
   * 客户端收到后移除会话列表项、消息缓存与本地已读/补拉游标；此时 conversations 可为空数组。
   */
  removedConversationIds?: string[]
}

export interface ReadUpdateFrame extends FrameBase {
  type: 'read.update'
  conversationId: string
  lastReadSeq: number
}

export interface ReadUpdatedFrame extends FrameBase {
  type: 'read.updated'
  conversationId: string
  userId: string
  lastReadSeq: number
}

export interface TypingClientFrame extends FrameBase {
  type: 'typing'
  conversationId: string
}

export interface TypingServerFrame extends FrameBase {
  type: 'typing'
  conversationId: string
  userId: string
}

export interface PresenceFrame extends FrameBase {
  type: 'presence'
  userId: string
  online: boolean
}

export interface KickedFrame extends FrameBase {
  type: 'kicked'
  reason: string
}

/** 好友申请状态（protocol.md §4.9） */
export type FriendRequestStatus = 'pending' | 'accepted' | 'rejected' | 'cancelled'

/** 好友对象（protocol.md §4.9，服务端 Friend DTO） */
export interface Friend {
  userId: string
  displayName: string
  email: string
  createdAt: number
  /** 我对该好友的备注（可选；未设置为空） */
  remark?: string
  /** 我设置的分组名（可选；未设置为空） */
  groupName?: string
  /** 头像文件 ID（可选） */
  avatarFileId?: string
}

/** 好友申请对象（protocol.md §4.9，服务端 FriendRequest DTO） */
export interface FriendRequest {
  requestId: string
  fromUserId: string
  fromDisplayName: string
  toUserId: string
  toDisplayName: string
  message?: string
  status: FriendRequestStatus
  createdAt: number
  handledAt?: number
}

/** friend.changed 的 kind（protocol.md §4.9；未知取值必须忽略） */
export type FriendChangedKind =
  | 'request.received'
  | 'request.accepted'
  | 'request.rejected'
  | 'request.cancelled'
  | 'friend.added'
  | 'friend.removed'
  | 'friend.updated'

/** 好友/申请变更推送（F-17，protocol.md §4.9） */
export interface FriendChangedFrame extends FrameBase {
  type: 'friend.changed'
  kind: FriendChangedKind
  friend?: Friend
  request?: FriendRequest
}

/** 个人收藏条目（F-38，protocol §7 第 13 条）：available=false 时 message 恒为 null。 */
export interface FavoriteItem {
  messageId: string
  conversationId: string
  conversationTitle: string
  senderName: string
  /** 发送者头像 files.id（服务端 2026-11-11 追加；旧服务端不返回时回退首字母头像）。 */
  senderAvatarFileId?: string | null
  available: boolean
  favoritedAt: number
  message: Message | null
}

/** 个人收藏变更推送（F-38，protocol §4.10）：只推本人连接，收到后重新拉取列表。 */
export interface FavoriteChangedFrame extends FrameBase {
  type: 'favorite.changed'
  messageId: string
  favorited: boolean
  changedAt: number
}

export interface ErrorFrame extends FrameBase {
  type: 'error'
  code: ErrorCode
  message: string
}

/** 客户端可发送的帧 */
export type ClientFrame =
  | AuthFrame
  | PingFrame
  | PongFrame
  | MessageSendFrame
  | MessageRecallFrame
  | MessageEditFrame
  | SyncRequestFrame
  | ConvListRequestFrame
  | ReadUpdateFrame
  | TypingClientFrame

/** 服务端可下发的帧 */
export type ServerFrame =
  | HelloFrame
  | PingFrame
  | PongFrame
  | MessageAckFrame
  | MessageNewFrame
  | MessageRecalledFrame
  | MessageEditedFrame
  | MessageReactionsFrame
  | SyncResponseFrame
  | ConvListResponseFrame
  | ConvUpdatedFrame
  | ReadUpdatedFrame
  | TypingServerFrame
  | PresenceFrame
  | FriendChangedFrame
  | FavoriteChangedFrame
  | KickedFrame
  | ErrorFrame
