using System.Net.WebSockets;
using System.Text;
using Chat.Protocol.Frames;
using Chat.Server.Common.Options;
using Chat.Server.Data;
using Chat.Server.Modules.Calls;
using Chat.Server.Modules.Messages;
using Chat.Server.Modules.Realtime;
using Chat.Server.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Chat.Server.Tests;

/// <summary>
/// F-22：多端在线（连接上限 + 多端已读）。
/// 用 FakeWebSocket 捕获服务端下发的帧与关闭码，不建立真实 WS 连接（真实链路由 e2e 覆盖）。
/// </summary>
public sealed class MultiConnectionTests
{
    [Fact]
    public async Task KickOverLimit_WithinLimit_DoesNotKick()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (connections, handler, provider) = CreateHandler(database, maxConnectionsPerUser: 3);
        await using (provider)
        {
            var first = Register(connections, "user-1", "device-1");
            var second = Register(connections, "user-1", "device-2");

            await handler.KickOverLimitAsync(second.Connection, "user-1", CancellationToken.None);

            Assert.Equal(2, connections.GetConnections("user-1").Count);
            Assert.Empty(first.Socket.SentTexts);
            Assert.Empty(second.Socket.SentTexts);
            Assert.Null(first.Socket.CloseStatusValue);
        }
    }

    [Fact]
    public async Task KickOverLimit_FourthConnection_KicksOldest()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (connections, handler, provider) = CreateHandler(database, maxConnectionsPerUser: 3);
        await using (provider)
        {
            var first = Register(connections, "user-1", "device-1");
            await Task.Delay(15);
            var second = Register(connections, "user-1", "device-2");
            await Task.Delay(15);
            var third = Register(connections, "user-1", "device-3");
            await Task.Delay(15);
            var fourth = Register(connections, "user-1", "device-4");

            await handler.KickOverLimitAsync(fourth.Connection, "user-1", CancellationToken.None);

            // 只踢最早建立的第一条连接，其余保持在线。
            Assert.Equal(3, connections.GetConnections("user-1").Count);
            Assert.Contains(first.Socket.SentTexts, text => text.Contains("\"type\":\"kicked\"", StringComparison.Ordinal));
            // FrameCodec 会把中文转义为 \uXXXX，因此关闭描述（未转义）里断言原因文案。
            Assert.Contains("账号连接数超出上限", first.Socket.CloseDescriptionValue ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal((WebSocketCloseStatus)WsCloseCodes.Kicked, first.Socket.CloseStatusValue);
            Assert.Empty(second.Socket.SentTexts);
            Assert.Empty(third.Socket.SentTexts);
            Assert.Empty(fourth.Socket.SentTexts);
        }
    }

    [Fact]
    public async Task KickOverLimit_LeavesOtherUsersConnections()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (connections, handler, provider) = CreateHandler(database, maxConnectionsPerUser: 3);
        await using (provider)
        {
            var first = Register(connections, "user-1", "device-1");
            await Task.Delay(15);
            Register(connections, "user-1", "device-2");
            await Task.Delay(15);
            Register(connections, "user-1", "device-3");
            var otherFirst = Register(connections, "user-2", "device-a");
            var otherSecond = Register(connections, "user-2", "device-b");
            await Task.Delay(15);
            var fourth = Register(connections, "user-1", "device-4");

            await handler.KickOverLimitAsync(fourth.Connection, "user-1", CancellationToken.None);

            Assert.Equal(3, connections.GetConnections("user-1").Count);
            Assert.Equal(2, connections.GetConnections("user-2").Count);
            Assert.Empty(otherFirst.Socket.SentTexts);
            Assert.Empty(otherSecond.Socket.SentTexts);
            Assert.Contains(first.Socket.SentTexts, text => text.Contains("\"type\":\"kicked\"", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task KickOverLimit_ClampsLimitToAtLeastOne()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (connections, handler, provider) = CreateHandler(database, maxConnectionsPerUser: 0);
        await using (provider)
        {
            var first = Register(connections, "user-1", "device-1");
            await Task.Delay(15);
            var second = Register(connections, "user-1", "device-2");

            await handler.KickOverLimitAsync(second.Connection, "user-1", CancellationToken.None);

            Assert.Single(connections.GetConnections("user-1"));
            Assert.DoesNotContain(first.Socket.SentTexts, text => !text.Contains("\"type\":\"kicked\"", StringComparison.Ordinal));
            Assert.Equal((WebSocketCloseStatus)WsCloseCodes.Kicked, first.Socket.CloseStatusValue);
        }
    }

    [Fact]
    public async Task ReadUpdate_Direct_PushesToOwnOtherConnectionsAndPeer()
    {
        await using var database = await TestDatabase.CreateAsync();
        (string ConversationId, string UserA, string UserB) seed;
        await using (var db = database.CreateContext())
        {
            var userA = await TestSupport.SeedUserAsync(db, "mc-direct-a@example.com");
            var userB = await TestSupport.SeedUserAsync(db, "mc-direct-b@example.com");
            var conversation = await TestSupport.SeedDirectConversationAsync(db, userA, userB);
            await TestSupport.SeedMessagesAsync(db, conversation.Id, userA.Id, 1);
            seed = (conversation.Id, userA.Id, userB.Id);
        }

        var (connections, handler, provider) = CreateHandler(database);
        await using (provider)
        {
            var a1 = Register(connections, seed.UserA, "a-device-1");
            var a2 = Register(connections, seed.UserA, "a-device-2");
            var b1 = Register(connections, seed.UserB, "b-device-1");

            await handler.HandleReadUpdateAsync(a1.Connection, new ReadUpdateFrame(seed.ConversationId, 1), CancellationToken.None);

            // 本人其他连接收到 read.updated，来源连接不回显。
            Assert.Single(a2.Socket.SentTexts);
            Assert.Contains("\"type\":\"read.updated\"", a2.Socket.SentTexts[0], StringComparison.Ordinal);
            Assert.Contains(seed.ConversationId, a2.Socket.SentTexts[0], StringComparison.Ordinal);
            Assert.Empty(a1.Socket.SentTexts);

            // 单聊对端仍按协议收 read.updated。
            Assert.Single(b1.Socket.SentTexts);
            Assert.Contains("\"type\":\"read.updated\"", b1.Socket.SentTexts[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ReadUpdate_Direct_PushesToAllOwnOtherConnections()
    {
        await using var database = await TestDatabase.CreateAsync();
        (string ConversationId, string UserA, string UserB) seed;
        await using (var db = database.CreateContext())
        {
            var userA = await TestSupport.SeedUserAsync(db, "mc-multi-a@example.com");
            var userB = await TestSupport.SeedUserAsync(db, "mc-multi-b@example.com");
            var conversation = await TestSupport.SeedDirectConversationAsync(db, userA, userB);
            await TestSupport.SeedMessagesAsync(db, conversation.Id, userA.Id, 1);
            seed = (conversation.Id, userA.Id, userB.Id);
        }

        var (connections, handler, provider) = CreateHandler(database);
        await using (provider)
        {
            var a1 = Register(connections, seed.UserA, "a-device-1");
            var a2 = Register(connections, seed.UserA, "a-device-2");
            var a3 = Register(connections, seed.UserA, "a-device-3");
            var b1 = Register(connections, seed.UserB, "b-device-1");

            await handler.HandleReadUpdateAsync(a1.Connection, new ReadUpdateFrame(seed.ConversationId, 1), CancellationToken.None);

            Assert.Empty(a1.Socket.SentTexts);
            Assert.Single(a2.Socket.SentTexts);
            Assert.Single(a3.Socket.SentTexts);
            Assert.Single(b1.Socket.SentTexts);
        }
    }

    [Fact]
    public async Task ReadUpdate_Group_DoesNotPushToOtherMembers()
    {
        await using var database = await TestDatabase.CreateAsync();
        (string ConversationId, string UserA, string UserB) seed;
        await using (var db = database.CreateContext())
        {
            var userA = await TestSupport.SeedUserAsync(db, "mc-group-a@example.com");
            var userB = await TestSupport.SeedUserAsync(db, "mc-group-b@example.com");
            var userC = await TestSupport.SeedUserAsync(db, "mc-group-c@example.com");
            var conversation = await TestSupport.SeedGroupConversationAsync(db, userA, userB, userC);
            await TestSupport.SeedMessagesAsync(db, conversation.Id, userA.Id, 1);
            seed = (conversation.Id, userA.Id, userB.Id);
        }

        var (connections, handler, provider) = CreateHandler(database);
        await using (provider)
        {
            var a1 = Register(connections, seed.UserA, "a-device-1");
            var a2 = Register(connections, seed.UserA, "a-device-2");
            var b1 = Register(connections, seed.UserB, "b-device-1");

            await handler.HandleReadUpdateAsync(a1.Connection, new ReadUpdateFrame(seed.ConversationId, 1), CancellationToken.None);

            // 群聊只同步本人其他连接，不向其他成员广播。
            Assert.Single(a2.Socket.SentTexts);
            Assert.Empty(b1.Socket.SentTexts);
            Assert.Empty(a1.Socket.SentTexts);
        }
    }

    [Fact]
    public async Task ReadUpdate_UnchangedCursor_DoesNotBroadcast()
    {
        await using var database = await TestDatabase.CreateAsync();
        (string ConversationId, string UserA, string UserB) seed;
        await using (var db = database.CreateContext())
        {
            var userA = await TestSupport.SeedUserAsync(db, "mc-repeat-a@example.com");
            var userB = await TestSupport.SeedUserAsync(db, "mc-repeat-b@example.com");
            var conversation = await TestSupport.SeedDirectConversationAsync(db, userA, userB);
            await TestSupport.SeedMessagesAsync(db, conversation.Id, userA.Id, 1);
            seed = (conversation.Id, userA.Id, userB.Id);
        }

        var (connections, handler, provider) = CreateHandler(database);
        await using (provider)
        {
            var a1 = Register(connections, seed.UserA, "a-device-1");
            var a2 = Register(connections, seed.UserA, "a-device-2");
            var b1 = Register(connections, seed.UserB, "b-device-1");

            await handler.HandleReadUpdateAsync(a1.Connection, new ReadUpdateFrame(seed.ConversationId, 1), CancellationToken.None);
            // 重复上报同一游标：changed=false，不应再次广播。
            await handler.HandleReadUpdateAsync(a1.Connection, new ReadUpdateFrame(seed.ConversationId, 1), CancellationToken.None);

            Assert.Single(a2.Socket.SentTexts);
            Assert.Single(b1.Socket.SentTexts);
        }
    }

    private static (WsConnection Connection, FakeWebSocket Socket) Register(
        ConnectionManager connections,
        string userId,
        string deviceId)
    {
        var socket = new FakeWebSocket();
        var connection = new WsConnection(socket, NullLogger<WsConnection>.Instance);
        connection.MarkAuthenticated(userId, deviceId);
        connections.Register(connection);
        return (connection, socket);
    }

    private static (ConnectionManager Connections, WebSocketConnectionHandler Handler, ServiceProvider Provider) CreateHandler(
        TestDatabase database,
        int maxConnectionsPerUser = 3)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => database.CreateContext());
        services.AddScoped(serviceProvider => new ReadService(
            serviceProvider.GetRequiredService<ChatDbContext>(),
            NullLogger<ReadService>.Instance));
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var connections = new ConnectionManager();
        var appOptions = TestSupport.CreateAppOptions();
        appOptions.MaxConnectionsPerUser = maxConnectionsPerUser;
        var presence = new PresenceService(connections, scopeFactory, NullLogger<PresenceService>.Instance);
        var calls = new CallSessionManager(
            connections,
            scopeFactory,
            Options.Create(new CallOptions()),
            NullLogger<CallSessionManager>.Instance);
        var handler = new WebSocketConnectionHandler(
            connections,
            presence,
            scopeFactory,
            calls,
            Options.Create(appOptions),
            NullLoggerFactory.Instance,
            NullLogger<WebSocketConnectionHandler>.Instance);
        return (connections, handler, provider);
    }

    /// <summary>测试用 WS：把服务端 SendAsync 的文本原样记录下来，并记录关闭码。</summary>
    private sealed class FakeWebSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;

        public List<string> SentTexts { get; } = [];

        public WebSocketCloseStatus? CloseStatusValue { get; private set; }

        public string? CloseDescriptionValue { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => CloseStatusValue;

        public override string? CloseStatusDescription => CloseDescriptionValue;

        public override WebSocketState State => _state;

        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            CloseStatusValue = closeStatus;
            CloseDescriptionValue = statusDescription;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) =>
            CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken) =>
            Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            if (buffer.Array is not null)
            {
                SentTexts.Add(Encoding.UTF8.GetString(buffer.Array, buffer.Offset, buffer.Count));
            }

            return Task.CompletedTask;
        }
    }
}
