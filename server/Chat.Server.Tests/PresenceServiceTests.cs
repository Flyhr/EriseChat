using Chat.Server.Modules.Realtime;
using Chat.Server.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chat.Server.Tests;

/// <summary>
/// M3-06：在线状态查询单测（M3-05 的 PresenceService.FindPeerUserIdsAsync，已改 internal 供测试）。
/// 只测「共同会话成员」纯查询；presence 0→1 / 1→0 的实际推送依赖真实 WebSocket 连接，
/// 由 M3-11 e2e 覆盖，这里不 mock 连接层。
/// </summary>
public sealed class PresenceServiceTests
{
    [Fact]
    public async Task FindPeerUserIds_DedupesAcrossSharedConversations()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var userA = await TestSupport.SeedUserAsync(db, "presence-a@example.com");
        var userB = await TestSupport.SeedUserAsync(db, "presence-b@example.com");
        // 单聊 + 群聊都与 B 共享：结果应去重为 [B]。
        await TestSupport.SeedDirectConversationAsync(db, userA, userB);
        await TestSupport.SeedGroupConversationAsync(db, userA, userB);

        var (presence, provider) = CreatePresenceService(database);
        await using (provider)
        {
            var peers = await presence.FindPeerUserIdsAsync(userA.Id, CancellationToken.None);

            Assert.Equal(new[] { userB.Id }, peers);
        }
    }

    [Fact]
    public async Task FindPeerUserIds_NoSharedConversation_ReturnsEmpty()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var userA = await TestSupport.SeedUserAsync(db, "presence-iso-a@example.com");
        var userB = await TestSupport.SeedUserAsync(db, "presence-iso-b@example.com");
        var lonely = await TestSupport.SeedUserAsync(db, "presence-lonely@example.com");
        await TestSupport.SeedDirectConversationAsync(db, userA, userB);

        var (presence, provider) = CreatePresenceService(database);
        await using (provider)
        {
            var peers = await presence.FindPeerUserIdsAsync(lonely.Id, CancellationToken.None);

            Assert.Empty(peers);
        }
    }

    [Fact]
    public async Task FindPeerUserIds_DoesNotIncludeSelf()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var userA = await TestSupport.SeedUserAsync(db, "presence-self-a@example.com");
        var userB = await TestSupport.SeedUserAsync(db, "presence-self-b@example.com");
        var userC = await TestSupport.SeedUserAsync(db, "presence-self-c@example.com");
        await TestSupport.SeedGroupConversationAsync(db, userA, userB, userC);

        var (presence, provider) = CreatePresenceService(database);
        await using (provider)
        {
            var peers = await presence.FindPeerUserIdsAsync(userA.Id, CancellationToken.None);

            Assert.Equal(2, peers.Count);
            Assert.Contains(userB.Id, peers);
            Assert.Contains(userC.Id, peers);
            Assert.DoesNotContain(userA.Id, peers);
        }
    }

    [Fact]
    public async Task FindPeerUserIds_IncludesFriendWithoutSharedConversation()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var userA = await TestSupport.SeedUserAsync(db, "presence-friend-a@example.com");
        var userB = await TestSupport.SeedUserAsync(db, "presence-friend-b@example.com");
        // F-17：好友关系（无共同会话）也应互相收到在线状态。
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        db.Friends.Add(new Chat.Server.Data.Entities.Friend
        {
            OwnerUserId = userA.Id,
            FriendUserId = userB.Id,
            CreatedAt = now,
        });
        db.Friends.Add(new Chat.Server.Data.Entities.Friend
        {
            OwnerUserId = userB.Id,
            FriendUserId = userA.Id,
            CreatedAt = now,
        });
        await db.SaveChangesAsync();

        var (presence, provider) = CreatePresenceService(database);
        await using (provider)
        {
            var peers = await presence.FindPeerUserIdsAsync(userA.Id, CancellationToken.None);

            Assert.Equal(new[] { userB.Id }, peers);
        }
    }

    private static (PresenceService Presence, ServiceProvider Provider) CreatePresenceService(TestDatabase database)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => database.CreateContext());
        var provider = services.BuildServiceProvider();
        var presence = new PresenceService(
            new ConnectionManager(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PresenceService>.Instance);
        return (presence, provider);
    }
}
