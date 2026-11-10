-- ============================================================
-- EriseChat 数据库表结构 v1（唯一来源）
-- ============================================================
-- 依据：docs/internal-plan.md §4.4（计划 V2.3.5）、docs/project-overview.md §11.2
-- 配套：protocol/protocol.md（帧与字段唯一来源）
--
-- 约定：
--   * 数据库：SQLite，单文件 /data/chat.db；表名与列名 snake_case
--   * 主键：UUID 文本（小写、36 位带连字符，服务端用 Guid.NewGuid().ToString() 生成）
--   * 时间：一律 INTEGER，存 UTC 毫秒（如 created_at、expires_at）
--   * 布尔：INTEGER（0/1）
--   * 枚举：TEXT + CHECK 约束（值域见各列注释）
--   * JSON：content_json / detail_json 存 JSON 文本；text 消息为 JSON 字符串，其余为 JSON 对象
--
-- 运行时由服务端连接初始化（不在本文件执行）：
--   PRAGMA journal_mode = WAL;  PRAGMA foreign_keys = ON;
--   迁移：EF Core 启动时自动执行（server/Chat.Server/Data/Migrations），必须幂等；
--   本文件与 EF 迁移同步维护；只允许向后兼容增量（新增表/新增可空列或带默认值列/新增索引）。
--
-- 与 EF 迁移的表示差异（语义等价）：
--   * UNIQUE 列约束在迁移中以唯一索引（IX_*）实现；
--   * 外键列索引由 EF 外键约定生成时，本文件已显式列出、迁移以同名 idx_* 索引落地。
-- ============================================================

-- ------------------------------------------------------------
-- users：用户账号
-- role   : user / admin（管理员）
-- status : active / disabled（禁用后不可登录、不发消息）
-- avatar_file_id 为便利引用（指向 files.id），不加外键以避免 users ↔ files 循环依赖；
-- 由服务端在文件清理时维护一致性。
-- ------------------------------------------------------------
CREATE TABLE users (
    id                TEXT PRIMARY KEY NOT NULL,
    email             TEXT NOT NULL UNIQUE,
    display_name      TEXT NOT NULL,
    password_hash     TEXT,
    email_verified_at INTEGER,
    avatar_file_id    TEXT,
    role              TEXT NOT NULL DEFAULT 'user' CHECK (role IN ('user', 'admin')),
    status            TEXT NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'disabled')),
    created_at        INTEGER NOT NULL
);

-- ------------------------------------------------------------
-- email_codes：邮箱验证码（只存哈希，不存明文）
-- purpose : register / login / reset（F-13 找回密码）
-- ip      : 请求来源 IP（可为空，便于限流排查）
-- ------------------------------------------------------------
CREATE TABLE email_codes (
    id         TEXT PRIMARY KEY NOT NULL,
    email      TEXT NOT NULL,
    purpose    TEXT NOT NULL CHECK (purpose IN ('register', 'login', 'reset')),
    code_hash  TEXT NOT NULL,
    expires_at INTEGER NOT NULL,
    attempts   INTEGER NOT NULL DEFAULT 0,
    created_at INTEGER NOT NULL,
    ip         TEXT
);

CREATE INDEX idx_email_codes_email_purpose_created_at ON email_codes (email, purpose, created_at);
CREATE INDEX idx_email_codes_expires_at ON email_codes (expires_at);

-- ------------------------------------------------------------
-- sessions：登录会话（JWT 可撤销；jwt_id 对应 JWT 的 jti）
-- device_id    : 客户端设备标识（同一设备重连复用）
-- revoked_at   : 非空表示已撤销/注销
-- ------------------------------------------------------------
CREATE TABLE sessions (
    id           TEXT PRIMARY KEY NOT NULL,
    user_id      TEXT NOT NULL,
    device_id    TEXT NOT NULL,
    jwt_id       TEXT NOT NULL UNIQUE,
    created_at   INTEGER NOT NULL,
    last_seen_at INTEGER NOT NULL,
    revoked_at   INTEGER,
    FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
);

CREATE INDEX idx_sessions_user_id ON sessions (user_id);

-- ------------------------------------------------------------
-- files：上传文件元数据（二进制存磁盘 /data/files/yyyyMM/{fileId}{ext}）
-- ------------------------------------------------------------
CREATE TABLE files (
    id            TEXT PRIMARY KEY NOT NULL,
    owner_id      TEXT NOT NULL,
    original_name TEXT NOT NULL,
    mime          TEXT NOT NULL,
    size          INTEGER NOT NULL CHECK (size >= 0),
    storage_path  TEXT NOT NULL,
    created_at    INTEGER NOT NULL,
    FOREIGN KEY (owner_id) REFERENCES users (id)
);

CREATE INDEX idx_files_owner_id ON files (owner_id);

-- ------------------------------------------------------------
-- conversations：会话（单聊 / 群聊）
-- type         : direct / group
-- title        : 群标题；单聊为空（客户端用对方昵称展示）
-- announcement : 群公告（≤500 字符，接口层校验；NULL = 无公告；单聊恒为 NULL；F-14）
-- avatar_file_id: 群头像 files.id 便利引用（无外键；NULL = 未设置；单聊恒为 NULL；F-23）
-- direct_key   : 单聊唯一键（两个用户 ID 排序后拼接，如 "u1:u2"）；群聊为 NULL
-- last_seq     : 会话最新消息序号，只增不减（未读与补拉基数）
-- last_message_id / pinned_message_id 为便利引用（指向 messages.id），不加外键以避免循环依赖
-- pinned_message_id: 群置顶消息 ID（仅 group、NULL = 无置顶；F-36）
-- ------------------------------------------------------------
CREATE TABLE conversations (
    id              TEXT PRIMARY KEY NOT NULL,
    type            TEXT NOT NULL CHECK (type IN ('direct', 'group')),
    title           TEXT,
    announcement    TEXT,
    avatar_file_id  TEXT,
    pinned_message_id TEXT,
    direct_key      TEXT UNIQUE,
    created_by      TEXT NOT NULL,
    created_at      INTEGER NOT NULL,
    last_seq        INTEGER NOT NULL DEFAULT 0,
    last_message_id TEXT,
    CHECK ((type = 'direct' AND direct_key IS NOT NULL) OR (type = 'group' AND direct_key IS NULL)),
    FOREIGN KEY (created_by) REFERENCES users (id)
);

CREATE INDEX idx_conversations_last_message_id ON conversations (last_message_id);
CREATE INDEX idx_conversations_created_by ON conversations (created_by);

-- ------------------------------------------------------------
-- conversation_members：会话成员
-- role          : owner / member
-- last_read_seq : 已读游标，只增不减（未读 = 会话 last_seq − last_read_seq）
-- pinned        : 当前用户是否置顶该会话（成员级设置，0/1，默认 0；F-10）
-- muted         : 当前用户是否对该会话免打扰（成员级设置，0/1，默认 0；F-10）
-- marked_unread : 当前用户是否手动标为未读（成员级设置，0/1，默认 0；独立于真实未读，
--                 不回退 last_read_seq、不撤销对端已读回执、不增加真实未读数；跨端同步）
-- ------------------------------------------------------------
CREATE TABLE conversation_members (
    conversation_id TEXT NOT NULL,
    user_id         TEXT NOT NULL,
    role            TEXT NOT NULL DEFAULT 'member' CHECK (role IN ('owner', 'member')),
    joined_at       INTEGER NOT NULL,
    last_read_seq   INTEGER NOT NULL DEFAULT 0,
    pinned          INTEGER NOT NULL DEFAULT 0,
    muted           INTEGER NOT NULL DEFAULT 0,
    marked_unread   INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (conversation_id, user_id),
    FOREIGN KEY (conversation_id) REFERENCES conversations (id) ON DELETE CASCADE,
    FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
);

CREATE INDEX idx_conversation_members_user_id ON conversation_members (user_id);

-- ------------------------------------------------------------
-- messages：消息
-- seq           : 会话内严格单调 +1，服务端在事务内分配，落库后不可变
-- client_msg_id : 客户端 UUID 幂等键，(conversation_id, client_msg_id) 唯一
-- content_json  : 按 msg_type 解释的 JSON 文本（text 为 JSON 字符串，image/file/voice/system 为 JSON 对象）
-- file_id       : image/file/voice 消息引用的 files.id；text/system 为 NULL。v1 不建库级外键：
--                 SQLite 对已有表加 FK 需整表重建，引用完整性由应用层（发送时校验文件存在与归属）保证，
--                 孤儿清理兜底；下载鉴权与孤儿清理都依赖本列（M3-01）
-- reply_to      : 被回复消息 ID（自引用）
-- deleted_at    : 预留（v1 不做撤回，恒为 NULL；P2 撤回使用）
-- ------------------------------------------------------------
CREATE TABLE messages (
    id              TEXT PRIMARY KEY NOT NULL,
    conversation_id TEXT NOT NULL,
    sender_id       TEXT NOT NULL,
    client_msg_id   TEXT NOT NULL,
    seq             INTEGER NOT NULL,
    msg_type        TEXT NOT NULL CHECK (msg_type IN ('text', 'image', 'file', 'voice', 'system')),
    content_json    TEXT NOT NULL,
    file_id         TEXT,
    reply_to        TEXT,
    created_at      INTEGER NOT NULL,
    deleted_at      INTEGER,
    edited_at       INTEGER,
    UNIQUE (conversation_id, client_msg_id),
    FOREIGN KEY (conversation_id) REFERENCES conversations (id) ON DELETE CASCADE,
    FOREIGN KEY (sender_id) REFERENCES users (id),
    FOREIGN KEY (reply_to) REFERENCES messages (id) ON DELETE SET NULL
);

CREATE INDEX idx_messages_conversation_seq ON messages (conversation_id, seq);
CREATE INDEX idx_messages_reply_to ON messages (reply_to);
CREATE INDEX idx_messages_sender_id ON messages (sender_id);
CREATE INDEX idx_messages_file_id ON messages (file_id);

-- ------------------------------------------------------------
-- invites：注册邀请码（可配置是否需要邀请；code 为明文短码）
-- used_by    : 使用者（已用邀请码只能使用一次）
-- expires_at : NULL 表示不限期
-- ------------------------------------------------------------
CREATE TABLE invites (
    code       TEXT PRIMARY KEY NOT NULL,
    created_by TEXT NOT NULL,
    used_by    TEXT,
    expires_at INTEGER,
    created_at INTEGER NOT NULL,
    FOREIGN KEY (created_by) REFERENCES users (id),
    FOREIGN KEY (used_by) REFERENCES users (id) ON DELETE SET NULL
);

CREATE INDEX idx_invites_created_by ON invites (created_by);
CREATE INDEX idx_invites_used_by ON invites (used_by);

-- ------------------------------------------------------------
-- admin_audit_logs：管理员操作审计
-- detail_json : 操作详情 JSON（不得包含 token/密码/验证码等敏感值）
-- ------------------------------------------------------------
CREATE TABLE admin_audit_logs (
    id          TEXT PRIMARY KEY NOT NULL,
    admin_id    TEXT NOT NULL,
    action      TEXT NOT NULL,
    target      TEXT,
    detail_json TEXT,
    created_at  INTEGER NOT NULL,
    FOREIGN KEY (admin_id) REFERENCES users (id)
);

CREATE INDEX idx_admin_audit_logs_admin_created_at ON admin_audit_logs (admin_id, created_at);

-- ------------------------------------------------------------
-- app_settings：全局配置（key/value，value 为 JSON 文本；敏感值仍放 secrets.json）
-- ------------------------------------------------------------
CREATE TABLE app_settings (
    key   TEXT PRIMARY KEY NOT NULL,
    value TEXT NOT NULL
);

-- ------------------------------------------------------------
-- friends：好友关系（F-17，单向行）
-- owner_user_id  : 关系拥有者（「我的好友」）
-- friend_user_id : 好友用户
-- remark         : 我对该好友的备注（空表示无备注）
-- group_name     : 我对该好友的分组名（空表示未分组）
-- 说明：单向行便于每个人独立维护备注/分组；服务端在成为好友时事务内插入双向两行，
--       删除好友时删除双向两行（不删除会话与消息）。
-- ------------------------------------------------------------
CREATE TABLE friends (
    owner_user_id  TEXT NOT NULL,
    friend_user_id TEXT NOT NULL,
    remark         TEXT,
    group_name     TEXT,
    created_at     INTEGER NOT NULL,
    PRIMARY KEY (owner_user_id, friend_user_id),
    FOREIGN KEY (owner_user_id) REFERENCES users (id) ON DELETE CASCADE,
    FOREIGN KEY (friend_user_id) REFERENCES users (id) ON DELETE CASCADE
);

CREATE INDEX idx_friends_owner ON friends (owner_user_id);
CREATE INDEX idx_friends_friend ON friends (friend_user_id);

-- ------------------------------------------------------------
-- friend_requests：好友申请（F-17）
-- status     : pending / accepted / rejected / cancelled
-- message    : 附言（≤100 字符，可空）
-- handled_at : 处理时间；pending 时为 NULL
-- 说明：部分唯一索引只约束 pending，允许同一对用户保留多条历史申请记录。
-- ------------------------------------------------------------
CREATE TABLE friend_requests (
    id           TEXT PRIMARY KEY NOT NULL,
    from_user_id TEXT NOT NULL,
    to_user_id   TEXT NOT NULL,
    message      TEXT,
    status       TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'accepted', 'rejected', 'cancelled')),
    created_at   INTEGER NOT NULL,
    handled_at   INTEGER,
    FOREIGN KEY (from_user_id) REFERENCES users (id) ON DELETE CASCADE,
    FOREIGN KEY (to_user_id) REFERENCES users (id) ON DELETE CASCADE
);

CREATE UNIQUE INDEX ux_friend_requests_pending
    ON friend_requests (from_user_id, to_user_id)
    WHERE status = 'pending';
CREATE INDEX idx_friend_requests_to_status ON friend_requests (to_user_id, status);
CREATE INDEX idx_friend_requests_from_status ON friend_requests (from_user_id, status);

-- ------------------------------------------------------------
-- favorites：个人收藏（F-38，跨设备同步）
-- user_id    : 收藏归属账号；message_id: 原消息引用；created_at: 收藏时间（UTC 毫秒）
-- 说明：只保存引用不复制内容，原消息编辑/撤回在读取时反映；单账号上限 500 由服务端校验；
--       不随消息撤回或退出会话自动删除，由用户手动移除。
-- ------------------------------------------------------------
CREATE TABLE favorites (
    user_id    TEXT NOT NULL,
    message_id TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    PRIMARY KEY (user_id, message_id),
    FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE,
    FOREIGN KEY (message_id) REFERENCES messages (id) ON DELETE CASCADE
);

CREATE INDEX idx_favorites_user_created ON favorites (user_id, created_at);
CREATE INDEX idx_favorites_message ON favorites (message_id);
