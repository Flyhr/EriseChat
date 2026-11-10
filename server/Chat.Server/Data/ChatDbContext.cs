using Chat.Protocol;
using Chat.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Chat.Server.Data;

/// <summary>
/// EriseChat 数据库上下文。表结构唯一来源：<c>protocol/schema.sql</c>，迁移位于 <c>Data/Migrations</c>。
/// 约定：表名/列名 snake_case；时间列 INTEGER 存 UTC 毫秒（C# long）；枚举以 TEXT 小写保存，
/// 并由各表 CHECK 约束兜底；外键 ON DELETE 行为严格按 schema.sql 配置。
/// </summary>
public sealed class ChatDbContext(DbContextOptions<ChatDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<EmailCode> EmailCodes => Set<EmailCode>();

    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<ConversationMember> ConversationMembers => Set<ConversationMember>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<StoredFile> Files => Set<StoredFile>();

    public DbSet<Invite> Invites => Set<Invite>();

    public DbSet<AdminAuditLog> AdminAuditLogs => Set<AdminAuditLog>();

    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    public DbSet<Friend> Friends => Set<Friend>();

    public DbSet<FriendRequest> FriendRequests => Set<FriendRequest>();

    public DbSet<Favorite> Favorites => Set<Favorite>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureUsers(modelBuilder);
        ConfigureEmailCodes(modelBuilder);
        ConfigureSessions(modelBuilder);
        ConfigureConversations(modelBuilder);
        ConfigureConversationMembers(modelBuilder);
        ConfigureMessages(modelBuilder);
        ConfigureFiles(modelBuilder);
        ConfigureInvites(modelBuilder);
        ConfigureAdminAuditLogs(modelBuilder);
        ConfigureAppSettings(modelBuilder);
        ConfigureFriends(modelBuilder);
        ConfigureFriendRequests(modelBuilder);
        ConfigureFavorites(modelBuilder);
    }

    /// <summary>枚举 ↔ TEXT 小写转换（与 schema.sql 的 CHECK 值域一致）。</summary>
    private static ValueConverter<TEnum, string> LowercaseEnum<TEnum>()
        where TEnum : struct, Enum
        => new(value => value.ToString().ToLowerInvariant(),
            text => Enum.Parse<TEnum>(text, ignoreCase: true));

    private static void ConfigureUsers(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users", table =>
            {
                table.HasCheckConstraint("ck_users_role", "role IN ('user', 'admin')");
                table.HasCheckConstraint("ck_users_status", "status IN ('active', 'disabled')");
            });

            entity.HasKey(user => user.Id);
            entity.Property(user => user.Id).HasColumnName("id");
            entity.Property(user => user.Email).HasColumnName("email").IsRequired();
            entity.HasIndex(user => user.Email).IsUnique();
            entity.Property(user => user.DisplayName).HasColumnName("display_name").IsRequired();
            entity.Property(user => user.PasswordHash).HasColumnName("password_hash");
            entity.Property(user => user.EmailVerifiedAt).HasColumnName("email_verified_at");
            entity.Property(user => user.AvatarFileId).HasColumnName("avatar_file_id");
            entity.Property(user => user.Role)
                .HasColumnName("role")
                .HasConversion(LowercaseEnum<UserRole>())
                .HasDefaultValueSql("'user'")
                // EF 警告 20601：枚举 CLR 默认值与库默认值相同；配一个不存在的哨兵值关闭警告，插入时显式写值。
                .HasSentinel((UserRole)(-1));
            entity.Property(user => user.Status)
                .HasColumnName("status")
                .HasConversion(LowercaseEnum<UserStatus>())
                .HasDefaultValueSql("'active'")
                .HasSentinel((UserStatus)(-1));
            entity.Property(user => user.CreatedAt).HasColumnName("created_at");
        });
    }

    private static void ConfigureEmailCodes(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EmailCode>(entity =>
        {
            entity.ToTable("email_codes", table =>
                table.HasCheckConstraint("ck_email_codes_purpose", "purpose IN ('register', 'login', 'reset')"));

            entity.HasKey(code => code.Id);
            entity.Property(code => code.Id).HasColumnName("id");
            entity.Property(code => code.Email).HasColumnName("email").IsRequired();
            entity.Property(code => code.Purpose)
                .HasColumnName("purpose")
                .HasConversion(LowercaseEnum<EmailCodePurpose>());
            entity.Property(code => code.CodeHash).HasColumnName("code_hash").IsRequired();
            entity.Property(code => code.ExpiresAt).HasColumnName("expires_at");
            entity.Property(code => code.Attempts).HasColumnName("attempts").HasDefaultValue(0);
            entity.Property(code => code.CreatedAt).HasColumnName("created_at");
            entity.Property(code => code.Ip).HasColumnName("ip");

            entity.HasIndex(code => new { code.Email, code.Purpose, code.CreatedAt })
                .HasDatabaseName("idx_email_codes_email_purpose_created_at");
            entity.HasIndex(code => code.ExpiresAt).HasDatabaseName("idx_email_codes_expires_at");
        });
    }

    private static void ConfigureSessions(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Session>(entity =>
        {
            entity.ToTable("sessions");

            entity.HasKey(session => session.Id);
            entity.Property(session => session.Id).HasColumnName("id");
            entity.Property(session => session.UserId).HasColumnName("user_id").IsRequired();
            entity.Property(session => session.DeviceId).HasColumnName("device_id").IsRequired();
            entity.Property(session => session.JwtId).HasColumnName("jwt_id").IsRequired();
            entity.HasIndex(session => session.JwtId).IsUnique();
            entity.Property(session => session.CreatedAt).HasColumnName("created_at");
            entity.Property(session => session.LastSeenAt).HasColumnName("last_seen_at");
            entity.Property(session => session.RevokedAt).HasColumnName("revoked_at");

            entity.HasOne<User>().WithMany().HasForeignKey(session => session.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(session => session.UserId).HasDatabaseName("idx_sessions_user_id");
        });
    }

    private static void ConfigureConversations(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Conversation>(entity =>
        {
            entity.ToTable("conversations", table =>
            {
                table.HasCheckConstraint("ck_conversations_type", "type IN ('direct', 'group')");
                table.HasCheckConstraint("ck_conversations_direct_key",
                    "((type = 'direct' AND direct_key IS NOT NULL) OR (type = 'group' AND direct_key IS NULL))");
            });

            entity.HasKey(conversation => conversation.Id);
            entity.Property(conversation => conversation.Id).HasColumnName("id");
            entity.Property(conversation => conversation.Type)
                .HasColumnName("type")
                .HasConversion(LowercaseEnum<ConversationType>());
            entity.Property(conversation => conversation.Title).HasColumnName("title");
            entity.Property(conversation => conversation.Announcement).HasColumnName("announcement");
            entity.Property(conversation => conversation.AvatarFileId).HasColumnName("avatar_file_id");
            // F-36：群置顶消息 ID（便利引用、无外键）；NULL = 无置顶。
            entity.Property(conversation => conversation.PinnedMessageId).HasColumnName("pinned_message_id");
            entity.Property(conversation => conversation.DirectKey).HasColumnName("direct_key");
            entity.HasIndex(conversation => conversation.DirectKey).IsUnique();
            entity.Property(conversation => conversation.CreatedBy).HasColumnName("created_by").IsRequired();
            entity.Property(conversation => conversation.CreatedAt).HasColumnName("created_at");
            entity.Property(conversation => conversation.LastSeq).HasColumnName("last_seq").HasDefaultValue(0L);
            entity.Property(conversation => conversation.LastMessageId).HasColumnName("last_message_id");
            entity.HasIndex(conversation => conversation.LastMessageId)
                .HasDatabaseName("idx_conversations_last_message_id");
            // EF 会为外键自动补索引；显式命名以与 schema.sql 的 idx_* 命名保持一致（M1-02 同步补入 schema.sql）。
            entity.HasIndex(conversation => conversation.CreatedBy)
                .HasDatabaseName("idx_conversations_created_by");

            entity.HasOne<User>().WithMany().HasForeignKey(conversation => conversation.CreatedBy)
                .OnDelete(DeleteBehavior.ClientNoAction);
        });
    }

    private static void ConfigureConversationMembers(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ConversationMember>(entity =>
        {
            entity.ToTable("conversation_members", table =>
                table.HasCheckConstraint("ck_conversation_members_role", "role IN ('owner', 'member')"));

            entity.HasKey(member => new { member.ConversationId, member.UserId });
            entity.Property(member => member.ConversationId).HasColumnName("conversation_id");
            entity.Property(member => member.UserId).HasColumnName("user_id");
            entity.Property(member => member.Role)
                .HasColumnName("role")
                .HasConversion(LowercaseEnum<MemberRole>())
                .HasDefaultValueSql("'member'")
                // Owner 是枚举 CLR 默认值（0），若不关闭 OnAdd 生成，EF 插入时会省略该列而落库默认值 'member'；
                // 这里强制显式写入，保证创建者的 owner 角色正确落库（迁移 DDL 的 DEFAULT 'member' 仍保留）。
                .ValueGeneratedNever();
            entity.Property(member => member.JoinedAt).HasColumnName("joined_at");
            entity.Property(member => member.LastReadSeq).HasColumnName("last_read_seq").HasDefaultValue(0L);
            // F-10：成员级设置（当前用户自己的置顶/免打扰）；只新增列，旧数据按默认值 0 兼容。
            entity.Property(member => member.Pinned).HasColumnName("pinned").HasDefaultValue(false);
            entity.Property(member => member.Muted).HasColumnName("muted").HasDefaultValue(false);
            entity.Property(member => member.MarkedUnread).HasColumnName("marked_unread").HasDefaultValue(false);

            entity.HasOne<Conversation>().WithMany()
                .HasForeignKey(member => member.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<User>().WithMany()
                .HasForeignKey(member => member.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(member => member.UserId).HasDatabaseName("idx_conversation_members_user_id");
        });
    }

    private static void ConfigureMessages(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Message>(entity =>
        {
            entity.ToTable("messages", table =>
                table.HasCheckConstraint("ck_messages_msg_type", "msg_type IN ('text', 'image', 'file', 'voice', 'system')"));

            entity.HasKey(message => message.Id);
            entity.Property(message => message.Id).HasColumnName("id");
            entity.Property(message => message.ConversationId).HasColumnName("conversation_id").IsRequired();
            entity.Property(message => message.SenderId).HasColumnName("sender_id").IsRequired();
            entity.Property(message => message.ClientMsgId).HasColumnName("client_msg_id").IsRequired();
            entity.Property(message => message.Seq).HasColumnName("seq");
            entity.Property(message => message.MsgType)
                .HasColumnName("msg_type")
                .HasConversion(LowercaseEnum<MessageType>());
            entity.Property(message => message.ContentJson).HasColumnName("content_json").IsRequired();
            entity.Property(message => message.FileId).HasColumnName("file_id");
            entity.Property(message => message.ReplyTo).HasColumnName("reply_to");
            entity.Property(message => message.CreatedAt).HasColumnName("created_at");
            entity.Property(message => message.DeletedAt).HasColumnName("deleted_at");
            entity.Property(message => message.EditedAt).HasColumnName("edited_at");

            entity.HasIndex(message => new { message.ConversationId, message.ClientMsgId }).IsUnique();
            entity.HasIndex(message => new { message.ConversationId, message.Seq })
                .HasDatabaseName("idx_messages_conversation_seq");
            entity.HasIndex(message => message.ReplyTo).HasDatabaseName("idx_messages_reply_to");
            // M3-01：image/file 消息引用的文件 ID（无外键，应用层保证引用完整性），索引用于下载鉴权与孤儿清理。
            entity.HasIndex(message => message.FileId).HasDatabaseName("idx_messages_file_id");
            // EF 会为外键自动补索引；显式命名以与 schema.sql 的 idx_* 命名保持一致（M1-02 同步补入 schema.sql）。
            entity.HasIndex(message => message.SenderId).HasDatabaseName("idx_messages_sender_id");

            entity.HasOne<Conversation>().WithMany()
                .HasForeignKey(message => message.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<User>().WithMany()
                .HasForeignKey(message => message.SenderId)
                .OnDelete(DeleteBehavior.ClientNoAction);
            entity.HasOne<Message>().WithMany()
                .HasForeignKey(message => message.ReplyTo)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }

    private static void ConfigureFiles(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StoredFile>(entity =>
        {
            entity.ToTable("files", table =>
                table.HasCheckConstraint("ck_files_size", "size >= 0"));

            entity.HasKey(file => file.Id);
            entity.Property(file => file.Id).HasColumnName("id");
            entity.Property(file => file.OwnerId).HasColumnName("owner_id").IsRequired();
            entity.Property(file => file.OriginalName).HasColumnName("original_name").IsRequired();
            entity.Property(file => file.Mime).HasColumnName("mime").IsRequired();
            entity.Property(file => file.Size).HasColumnName("size");
            entity.Property(file => file.StoragePath).HasColumnName("storage_path").IsRequired();
            entity.Property(file => file.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(file => file.OwnerId).HasDatabaseName("idx_files_owner_id");

            entity.HasOne<User>().WithMany().HasForeignKey(file => file.OwnerId)
                .OnDelete(DeleteBehavior.ClientNoAction);
        });
    }

    private static void ConfigureInvites(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Invite>(entity =>
        {
            entity.ToTable("invites");

            entity.HasKey(invite => invite.Code);
            entity.Property(invite => invite.Code).HasColumnName("code");
            entity.Property(invite => invite.CreatedBy).HasColumnName("created_by").IsRequired();
            entity.Property(invite => invite.UsedBy).HasColumnName("used_by");
            entity.Property(invite => invite.ExpiresAt).HasColumnName("expires_at");
            entity.Property(invite => invite.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(invite => invite.CreatedBy).HasDatabaseName("idx_invites_created_by");
            // EF 会为外键自动补索引；显式命名以与 schema.sql 的 idx_* 命名保持一致（M1-02 同步补入 schema.sql）。
            entity.HasIndex(invite => invite.UsedBy).HasDatabaseName("idx_invites_used_by");

            entity.HasOne<User>().WithMany().HasForeignKey(invite => invite.CreatedBy)
                .OnDelete(DeleteBehavior.ClientNoAction);
            entity.HasOne<User>().WithMany().HasForeignKey(invite => invite.UsedBy)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }

    private static void ConfigureAdminAuditLogs(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AdminAuditLog>(entity =>
        {
            entity.ToTable("admin_audit_logs");

            entity.HasKey(log => log.Id);
            entity.Property(log => log.Id).HasColumnName("id");
            entity.Property(log => log.AdminId).HasColumnName("admin_id").IsRequired();
            entity.Property(log => log.Action).HasColumnName("action").IsRequired();
            entity.Property(log => log.Target).HasColumnName("target");
            entity.Property(log => log.DetailJson).HasColumnName("detail_json");
            entity.Property(log => log.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(log => new { log.AdminId, log.CreatedAt })
                .HasDatabaseName("idx_admin_audit_logs_admin_created_at");

            entity.HasOne<User>().WithMany().HasForeignKey(log => log.AdminId)
                .OnDelete(DeleteBehavior.ClientNoAction);
        });
    }

    private static void ConfigureAppSettings(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppSetting>(entity =>
        {
            entity.ToTable("app_settings");

            entity.HasKey(setting => setting.Key);
            entity.Property(setting => setting.Key).HasColumnName("key");
            entity.Property(setting => setting.Value).HasColumnName("value").IsRequired();
        });
    }

    private static void ConfigureFavorites(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Favorite>(entity =>
        {
            entity.ToTable("favorites");
            entity.HasKey(favorite => new { favorite.UserId, favorite.MessageId });
            entity.Property(favorite => favorite.UserId).HasColumnName("user_id");
            entity.Property(favorite => favorite.MessageId).HasColumnName("message_id");
            entity.Property(favorite => favorite.CreatedAt).HasColumnName("created_at");

            entity.HasOne<User>().WithMany()
                .HasForeignKey(favorite => favorite.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Message>().WithMany()
                .HasForeignKey(favorite => favorite.MessageId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(favorite => new { favorite.UserId, favorite.CreatedAt })
                .HasDatabaseName("idx_favorites_user_created");
            entity.HasIndex(favorite => favorite.MessageId).HasDatabaseName("idx_favorites_message");
        });
    }

    private static void ConfigureFriends(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Friend>(entity =>
        {
            entity.ToTable("friends");

            // 单向关系：双向外键都指向 users；互为好友时双方各一行（F-17）。
            entity.HasKey(friend => new { friend.OwnerUserId, friend.FriendUserId });
            entity.Property(friend => friend.OwnerUserId).HasColumnName("owner_user_id").IsRequired();
            entity.Property(friend => friend.FriendUserId).HasColumnName("friend_user_id").IsRequired();
            entity.Property(friend => friend.Remark).HasColumnName("remark");
            entity.Property(friend => friend.GroupName).HasColumnName("group_name");
            entity.Property(friend => friend.CreatedAt).HasColumnName("created_at");

            entity.HasOne<User>().WithMany()
                .HasForeignKey(friend => friend.OwnerUserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<User>().WithMany()
                .HasForeignKey(friend => friend.FriendUserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(friend => friend.OwnerUserId).HasDatabaseName("idx_friends_owner");
            entity.HasIndex(friend => friend.FriendUserId).HasDatabaseName("idx_friends_friend");
        });
    }

    private static void ConfigureFriendRequests(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FriendRequest>(entity =>
        {
            entity.ToTable("friend_requests", table =>
                table.HasCheckConstraint(
                    "ck_friend_requests_status",
                    "status IN ('pending', 'accepted', 'rejected', 'cancelled')"));

            entity.HasKey(request => request.Id);
            entity.Property(request => request.Id).HasColumnName("id");
            entity.Property(request => request.FromUserId).HasColumnName("from_user_id").IsRequired();
            entity.Property(request => request.ToUserId).HasColumnName("to_user_id").IsRequired();
            entity.Property(request => request.Message).HasColumnName("message");
            entity.Property(request => request.Status)
                .HasColumnName("status")
                .HasConversion(LowercaseEnum<FriendRequestStatus>())
                .HasDefaultValueSql("'pending'")
                // 同上：Status 的 CLR 默认值 Pending 与库默认值相同，用哨兵值消除 EF 20601 警告。
                .HasSentinel((FriendRequestStatus)(-1));
            entity.Property(request => request.CreatedAt).HasColumnName("created_at");
            entity.Property(request => request.HandledAt).HasColumnName("handled_at");

            // 同一对用户 pending 期间至多一条（部分唯一索引）；历史申请可保留多条。
            entity.HasIndex(request => new { request.FromUserId, request.ToUserId })
                .IsUnique()
                .HasFilter("status = 'pending'")
                .HasDatabaseName("ux_friend_requests_pending");
            entity.HasIndex(request => new { request.ToUserId, request.Status })
                .HasDatabaseName("idx_friend_requests_to_status");
            entity.HasIndex(request => new { request.FromUserId, request.Status })
                .HasDatabaseName("idx_friend_requests_from_status");

            entity.HasOne<User>().WithMany()
                .HasForeignKey(request => request.FromUserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<User>().WithMany()
                .HasForeignKey(request => request.ToUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
