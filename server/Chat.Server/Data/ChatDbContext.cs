using Chat.Protocol;
using Chat.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Chat.Server.Data;

/// <summary>
/// EiseChat 数据库上下文。表结构唯一来源：<c>protocol/schema.sql</c>，迁移位于 <c>Data/Migrations</c>。
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
                .HasDefaultValueSql("'user'");
            entity.Property(user => user.Status)
                .HasColumnName("status")
                .HasConversion(LowercaseEnum<UserStatus>())
                .HasDefaultValueSql("'active'");
            entity.Property(user => user.CreatedAt).HasColumnName("created_at");
        });
    }

    private static void ConfigureEmailCodes(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EmailCode>(entity =>
        {
            entity.ToTable("email_codes", table =>
                table.HasCheckConstraint("ck_email_codes_purpose", "purpose IN ('register', 'login')"));

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
                table.HasCheckConstraint("ck_messages_msg_type", "msg_type IN ('text', 'image', 'file', 'system')"));

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
            entity.Property(message => message.ReplyTo).HasColumnName("reply_to");
            entity.Property(message => message.CreatedAt).HasColumnName("created_at");
            entity.Property(message => message.DeletedAt).HasColumnName("deleted_at");

            entity.HasIndex(message => new { message.ConversationId, message.ClientMsgId }).IsUnique();
            entity.HasIndex(message => new { message.ConversationId, message.Seq })
                .HasDatabaseName("idx_messages_conversation_seq");
            entity.HasIndex(message => message.ReplyTo).HasDatabaseName("idx_messages_reply_to");
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
}
