using Chat.Protocol;
using Chat.Server.Data;
using Chat.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Chat.Server.Tests;

/// <summary>
/// F-12-02：个人资料修改（昵称 + 头像引用）单测。覆盖 trim/边界/控制字符/空请求、
/// 头像仅限本人上传的图片文件、文件不存在与格式校验、会话成员展示名随库更新。
/// </summary>
public sealed class ProfileTests
{
    [Fact]
    public async Task UpdateDisplayName_TrimsAndPersists()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var user = await TestSupport.SeedUserAsync(db, "profile-name@example.com", "旧昵称");
        var auth = TestSupport.CreateAuthService(db, TestSupport.CreateJwtOptions());

        var profile = await auth.UpdateProfileAsync(user.Id, "  新昵称  ", null, CancellationToken.None);

        Assert.Equal("新昵称", profile.DisplayName);
        var stored = await db.Users.AsNoTracking().SingleAsync(item => item.Id == user.Id);
        Assert.Equal("新昵称", stored.DisplayName);
    }

    [Fact]
    public async Task DisplayNameBoundary_OneAnd32AreAccepted()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var user = await TestSupport.SeedUserAsync(db, "profile-boundary@example.com");
        var auth = TestSupport.CreateAuthService(db, TestSupport.CreateJwtOptions());

        var single = await auth.UpdateProfileAsync(user.Id, "张", null, CancellationToken.None);
        Assert.Equal("张", single.DisplayName);

        var max = new string('a', 32);
        var full = await auth.UpdateProfileAsync(user.Id, max, null, CancellationToken.None);
        Assert.Equal(max, full.DisplayName);
    }

    [Fact]
    public async Task DisplayNameInvalid_ValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var user = await TestSupport.SeedUserAsync(db, "profile-invalid@example.com");
        var auth = TestSupport.CreateAuthService(db, TestSupport.CreateJwtOptions());

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => auth.UpdateProfileAsync(user.Id, "   ", null, CancellationToken.None),
            400);
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => auth.UpdateProfileAsync(user.Id, new string('a', 33), null, CancellationToken.None),
            400);
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => auth.UpdateProfileAsync(user.Id, string.Empty, null, CancellationToken.None),
            400);
    }

    [Fact]
    public async Task DisplayNameControlCharacter_ValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var user = await TestSupport.SeedUserAsync(db, "profile-control@example.com");
        var auth = TestSupport.CreateAuthService(db, TestSupport.CreateJwtOptions());

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => auth.UpdateProfileAsync(user.Id, "坏\u0001名", null, CancellationToken.None),
            400);
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => auth.UpdateProfileAsync(user.Id, "换行\n昵称", null, CancellationToken.None),
            400);
    }

    [Fact]
    public async Task AvatarFromOwnImage_PersistsAndReturnedByProfile()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var user = await TestSupport.SeedUserAsync(db, "profile-avatar@example.com");
        var file = await SeedFileAsync(db, user.Id, "image/png");
        var auth = TestSupport.CreateAuthService(db, TestSupport.CreateJwtOptions());

        var updated = await auth.UpdateProfileAsync(user.Id, null, file.Id, CancellationToken.None);
        Assert.Equal(file.Id, updated.AvatarFileId);

        var profile = await auth.GetProfileAsync(user.Id, CancellationToken.None);
        Assert.Equal(file.Id, profile.AvatarFileId);
    }

    [Fact]
    public async Task AvatarFromOtherUser_Forbidden()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var user = await TestSupport.SeedUserAsync(db, "profile-owner@example.com");
        var other = await TestSupport.SeedUserAsync(db, "profile-other@example.com");
        var file = await SeedFileAsync(db, other.Id, "image/png");
        var auth = TestSupport.CreateAuthService(db, TestSupport.CreateJwtOptions());

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.Forbidden,
            () => auth.UpdateProfileAsync(user.Id, null, file.Id, CancellationToken.None),
            403);
    }

    [Fact]
    public async Task AvatarNonImage_Forbidden()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var user = await TestSupport.SeedUserAsync(db, "profile-pdf@example.com");
        var file = await SeedFileAsync(db, user.Id, "application/pdf");
        var auth = TestSupport.CreateAuthService(db, TestSupport.CreateJwtOptions());

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.Forbidden,
            () => auth.UpdateProfileAsync(user.Id, null, file.Id, CancellationToken.None),
            403);
    }

    [Fact]
    public async Task EmptyRequest_ValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var user = await TestSupport.SeedUserAsync(db, "profile-empty@example.com");
        var auth = TestSupport.CreateAuthService(db, TestSupport.CreateJwtOptions());

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => auth.UpdateProfileAsync(user.Id, null, null, CancellationToken.None),
            400);
    }

    [Fact]
    public async Task AvatarFileIdFormat_ValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var user = await TestSupport.SeedUserAsync(db, "profile-format@example.com");
        var auth = TestSupport.CreateAuthService(db, TestSupport.CreateJwtOptions());

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => auth.UpdateProfileAsync(user.Id, null, "  ", CancellationToken.None),
            400);
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => auth.UpdateProfileAsync(user.Id, null, "not-a-known-file-id", CancellationToken.None),
            400);
    }

    [Fact]
    public async Task ConversationList_UsesUpdatedDisplayName()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "profile-list-owner@example.com", "对方旧名");
        var member = await TestSupport.SeedUserAsync(db, "profile-list-member@example.com", "自己");
        await TestSupport.SeedDirectConversationAsync(db, owner, member);
        var auth = TestSupport.CreateAuthService(db, TestSupport.CreateJwtOptions());
        var conversations = TestSupport.CreateConversationService(db);

        await auth.UpdateProfileAsync(owner.Id, "对方新名", null, CancellationToken.None);
        var list = await conversations.ListAsync(member.Id, CancellationToken.None);
        var conversation = Assert.Single(list);
        var memberEntry = Assert.Single(conversation.Members, item => item.UserId == owner.Id);
        Assert.Equal("对方新名", memberEntry.DisplayName);
    }

    private static async Task<StoredFile> SeedFileAsync(ChatDbContext db, string ownerId, string mime)
    {
        var file = new StoredFile
        {
            Id = Guid.NewGuid().ToString("D"),
            OwnerId = ownerId,
            OriginalName = mime.StartsWith("image/", StringComparison.Ordinal) ? "头像.png" : "文档.pdf",
            Mime = mime,
            Size = 1024,
            StoragePath = "202610/" + Guid.NewGuid().ToString("N") + ".bin",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        db.Files.Add(file);
        await db.SaveChangesAsync();
        return file;
    }
}
