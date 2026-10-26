// EiseChat WebSocket 协议类型（protocolVersion = 1）。
//
// 唯一来源：protocol/protocol.md；服务端 DTO 在 server/Chat.Protocol（二者必须一致）。
// 修改规则：先改 protocol/protocol.md 与服务端 DTO，再同步本文件；只允许向后兼容的增量
//（AGENTS.md §2.7、docs/project-overview.md §14）。
// 本文件只是类型镜像，不做运行时校验；帧解析/序列化在 core/ws.ts。
// 协议版本运行时值见 core/config.ts（默认 1，可由 VITE_PROTOCOL_VERSION 覆盖）。

/** 18 个帧 type（protocol.md §3） */
export type FrameType =
  | 'auth'
  | 'hello'
  | 'ping'
  | 'pong'
  | 'message.send'
  | 'message.ack'
  | 'message.new'
  | 'sync.request'
  | 'sync.response'
  | 'conv.list'
  | 'conv.updated'
  | 'read.update'
  | 'read.updated'
  | 'typing'
  | 'presence'
  | 'friend.changed'
  | 'kicked'
  | 'error'

export type MessageType = 'text' | 'image' | 'file' | 'system'
/** 客户端可发送的消息类型（system 仅服务端生成，见 protocol.md §4.3） */
export type MessageSendType = 'text' | 'image' | 'file'
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
}

/** 系统消息内容（服务端生成，M3 使用；核心字段 event 必填） */
export interface SystemContent {
  event: string
  actorId?: string
  targetUserIds?: string[]
  text?: string
}

export type MessageContent = string | FileContent | SystemContent

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
  createdAt: number
  deletedAt?: number
}

/** 会话成员（protocol.md §4.5） */
export interface ConversationMember {
  userId: string
  displayName: string
  email: string
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
  content: string | FileContent
  replyTo?: string
}

export interface MessageAckFrame extends FrameBase {
  type: 'message.ack'
  clientMsgId: string
  messageId: string
  conversationId: string
  seq: number
  createdAt: number
}

export interface MessageNewFrame extends FrameBase {
  type: 'message.new'
  message: Message
}

export interface SyncCursor {
  conversationId: string
  lastSeq: number
}

export interface SyncRequestFrame extends FrameBase {
  type: 'sync.request'
  cursors: SyncCursor[]
}

export interface SyncBatch {
  conversationId: string
  messages: Message[]
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
  | SyncResponseFrame
  | ConvListResponseFrame
  | ConvUpdatedFrame
  | ReadUpdatedFrame
  | TypingServerFrame
  | PresenceFrame
  | FriendChangedFrame
  | KickedFrame
  | ErrorFrame
