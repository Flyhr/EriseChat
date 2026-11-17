using System.Collections.Concurrent;
using Chat.Protocol;
using Chat.Protocol.Frames;
using Chat.Server.Common.Errors;
using Chat.Server.Common.Options;
using Chat.Server.Data;
using Chat.Server.Data.Entities;
using Chat.Server.Modules.Blocks;
using Chat.Server.Modules.Conversations;
using Chat.Server.Modules.Messages;
using Chat.Server.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProtocolMessage = Chat.Protocol.Frames.Message;

namespace Chat.Server.Modules.Calls;

/// <summary>一通 1:1 通话的内存状态（不落库的只有 SDP/ICE 与定时器）。</summary>
public sealed class CallSession
{
    /// <summary>通话 ID（主叫生成）。</summary>
    public required string CallId { get; init; }

    /// <summary>单聊会话 ID。</summary>
    public required string ConversationId { get; init; }

    /// <summary>主叫用户 ID。</summary>
    public required string CallerId { get; init; }

    /// <summary>被叫用户 ID。</summary>
    public required string CalleeId { get; init; }

    /// <summary>通话类型。</summary>
    public required CallKind Kind { get; init; }

    /// <summary>协议中的 kind 文本（audio / video）。</summary>
    public string KindText => Kind == CallKind.Audio ? "audio" : "video";

    /// <summary>当前状态。</summary>
    public CallState State { get; set; } = CallState.Ringing;

    /// <summary>创建时间（UTC 毫秒）。</summary>
    public long CreatedAt { get; init; }

    /// <summary>接听时间。</summary>
    public long? AnsweredAt { get; set; }

    /// <summary>媒体接通时间（首个 call.connected）。</summary>
    public long? ConnectedAt { get; set; }

    /// <summary>结束原因（首个结束方生效）。</summary>
    public string? EndReason { get; set; }

    /// <summary>状态迁移/结束互斥。</summary>
    public object Sync { get; } = new();

    /// <summary>结束只执行一次（Interlocked 交换用字段）。</summary>
    public int EndedFlag;

    /// <summary>振铃超时定时器。</summary>
    public CancellationTokenSource? RingTimer;

    /// <summary>断线宽限定时器。</summary>
    public CancellationTokenSource? DisconnectTimer;
}

/// <summary>
/// 1:1 通话信令与状态机（protocol/protocol.md §3.1）：
/// 邀请 → 振铃 → 接听/拒接/取消 → SDP/ICE 转发 → 媒体接通 → 结束；
/// 结束时更新 calls 并在媒体接通过的通话上补写时间线记录（system + call.completed）。
/// 内存态按 callId 与 userId 建索引，进程重启即丢失（通话本身短生命周期，可接受）。
/// </summary>
public sealed class CallSessionManager
{
    private readonly ConcurrentDictionary<string, CallSession> _byCallId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _activeByUser = new(StringComparer.Ordinal);
    private readonly ConnectionManager _connections;
    private readonly IServiceScopeFactory _scopes;
    private readonly CallOptions _options;
    private readonly ILogger<CallSessionManager> _logger;

    public CallSessionManager(
        ConnectionManager connections,
        IServiceScopeFactory scopes,
        IOptions<CallOptions> options,
        ILogger<CallSessionManager> logger)
    {
        _connections = connections;
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>call.invite：校验会话/拉黑/可达性/忙线后建会话，推 call.incoming 并回主叫 call.state(ringing)。</summary>
    public async Task HandleInviteAsync(WsConnection caller, CallInviteFrame request, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var callerId = RequireUser(caller);
        var callId = NormalizeCallId(request.CallId);
        var kind = ParseKind(request.Kind);

        if (_byCallId.TryGetValue(callId, out var duplicate))
        {
            await SendStateAsync(caller, duplicate, cancellationToken);
            return;
        }

        var (calleeId, conversationId) = await ResolveDirectCounterpartAsync(
            callerId,
            request.ConversationId,
            cancellationToken);

        using (var scope = _scopes.CreateScope())
        {
            var blocks = scope.ServiceProvider.GetRequiredService<BlockService>();
            if (await blocks.IsBlockedAsync(callerId, calleeId, cancellationToken))
            {
                throw new ApiException(ErrorCodes.CallForbidden, "无法呼叫该用户", StatusCodes.Status403Forbidden);
            }
        }

        var calleeConnections = _connections.GetConnections(calleeId)
            .Where(connection => connection.SupportsCallKind(KindText(kind)))
            .ToList();
        if (calleeConnections.Count == 0)
        {
            throw new ApiException(
                ErrorCodes.CallUnavailable,
                "对方暂时无法接听（没有可接听的设备）",
                StatusCodes.Status409Conflict);
        }

        if (_activeByUser.ContainsKey(callerId) || _activeByUser.ContainsKey(calleeId))
        {
            throw new ApiException(ErrorCodes.CallBusy, "对方正在通话中", StatusCodes.Status409Conflict);
        }

        var session = new CallSession
        {
            CallId = callId,
            ConversationId = conversationId,
            CallerId = callerId,
            CalleeId = calleeId,
            Kind = kind,
            CreatedAt = Now(),
        };

        if (!_byCallId.TryAdd(callId, session))
        {
            await SendStateAsync(caller, _byCallId[callId], cancellationToken);
            return;
        }

        _activeByUser[callerId] = callId;
        _activeByUser[calleeId] = callId;

        await InsertAsync(session, cancellationToken);

        var callerPeer = await LoadPeerAsync(callerId, cancellationToken);
        var incoming = new CallIncomingFrame(
            callId,
            conversationId,
            session.KindText,
            callerPeer,
            session.CreatedAt);
        foreach (var connection in calleeConnections)
        {
            await TrySendAsync(connection, incoming, cancellationToken);
        }

        var calleePeer = await LoadPeerAsync(calleeId, cancellationToken);
        await caller.SendFrameAsync(
            new CallStateFrame(callId, "ringing", session.KindText, calleePeer, session.CreatedAt)
            {
                ReqId = request.ReqId,
            },
            cancellationToken);

        _logger.LogInformation(
            "call.invite：{CallId} {Kind}，{CallerId} → {CalleeId}（被叫 {ConnectionCount} 条连接）",
            callId,
            session.KindText,
            callerId,
            calleeId,
            calleeConnections.Count);

        ScheduleRingTimeout(session);
    }

    /// <summary>call.ringing：被叫已显示来电，转给主叫（可选 UI，不影响状态）。</summary>
    public async Task HandleRingingAsync(WsConnection connection, CallRingingFrame request, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var session = RequireSession(request.CallId);
        var userId = RequireUser(connection);
        if (userId != session.CalleeId)
        {
            throw Forbidden();
        }

        await SendToUserAsync(session.CallerId, new CallRingingFrame(session.CallId), cancellationToken);
    }

    /// <summary>call.accept：被叫接听；首个 accept 生效，其余已振铃连接收 call.cancel(answered_elsewhere)。</summary>
    public async Task HandleAcceptAsync(WsConnection connection, CallAcceptFrame request, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var session = RequireSession(request.CallId);
        var userId = RequireUser(connection);
        if (userId != session.CalleeId)
        {
            throw Forbidden();
        }

        long answeredAt;
        lock (session.Sync)
        {
            if (session.State == CallState.Ringing)
            {
                session.State = CallState.Connecting;
                answeredAt = Now();
                session.AnsweredAt ??= answeredAt;
            }
            else
            {
                // 已接听（或已结束）：本条 accept 视为重复，回当前状态。
                answeredAt = session.AnsweredAt ?? Now();
            }
        }

        CancelRingTimer(session);
        await UpdateAnsweredAsync(session.CallId, answeredAt, cancellationToken);

        var callerPeer = await LoadPeerAsync(session.CallerId, cancellationToken);
        var calleePeer = await LoadPeerAsync(session.CalleeId, cancellationToken);

        await SendToUserAsync(
            session.CallerId,
            new CallAcceptedFrame(session.CallId, calleePeer, answeredAt),
            cancellationToken);
        await connection.SendFrameAsync(
            new CallAcceptedFrame(session.CallId, callerPeer, answeredAt),
            cancellationToken);

        foreach (var other in _connections.GetConnections(session.CalleeId)
                     .Where(other => other.ConnectionId != connection.ConnectionId))
        {
            await TrySendAsync(
                other,
                new CallCancelFrame(session.CallId, "answered_elsewhere"),
                cancellationToken);
        }

        _logger.LogInformation("call.accept：{CallId} 已接听（{CalleeId}）", session.CallId, session.CalleeId);
    }

    /// <summary>call.connected：任一端首次收到远端媒体 → 记 connected_at（只记一次）。</summary>
    public async Task HandleConnectedAsync(WsConnection connection, CallConnectedFrame request, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var session = RequireSession(request.CallId);
        RequireParticipant(connection, session);

        long? connectedAt = null;
        lock (session.Sync)
        {
            if (session.ConnectedAt is null)
            {
                connectedAt = Now();
                session.ConnectedAt = connectedAt;
                session.State = CallState.Active;
            }
        }

        if (connectedAt is not null)
        {
            await UpdateConnectedAsync(session.CallId, connectedAt.Value, cancellationToken);
            _logger.LogInformation("call.connected：{CallId} 媒体已接通", session.CallId);
        }
    }

    /// <summary>call.reject：被叫拒接 → 结束（不写时间线），主叫收 call.end。</summary>
    public async Task HandleRejectAsync(WsConnection connection, CallRejectFrame request, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var session = RequireSession(request.CallId);
        if (RequireUser(connection) != session.CalleeId)
        {
            throw Forbidden();
        }

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? "declined" : request.Reason.Trim();
        await EndAsync(session, reason, cancellationToken);
    }

    /// <summary>call.cancel：主叫在接通前取消 → 结束（不写时间线），被叫收 call.cancel。</summary>
    public async Task HandleCancelAsync(WsConnection connection, CallCancelFrame request, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var session = RequireSession(request.CallId);
        RequireParticipant(connection, session);
        await EndAsync(session, "canceled", cancellationToken);
    }

    /// <summary>call.end：任一端挂断 → 结束并广播；媒体接通过的写时间线记录。</summary>
    public async Task HandleEndAsync(WsConnection connection, CallEndFrame request, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var session = RequireSession(request.CallId);
        RequireParticipant(connection, session);
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? "hangup" : request.Reason.Trim();
        await EndAsync(session, reason, cancellationToken);
    }

    /// <summary>call.sdp：只转发给对端支持该类型的连接，不落库、不写日志正文。</summary>
    public async Task HandleSdpAsync(WsConnection connection, CallSdpFrame request, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var session = RequireSession(request.CallId);
        RequireParticipant(connection, session);
        if (request.Sdp is null || string.IsNullOrWhiteSpace(request.Sdp.Sdp))
        {
            throw ApiException.ValidationFailed("sdp 不能为空");
        }

        var peerId = RequireUser(connection) == session.CallerId ? session.CalleeId : session.CallerId;
        await SendToUserAsync(peerId, request, cancellationToken, session.KindText, except: connection);
    }

    /// <summary>call.ice：只转发给对端支持该类型的连接，不落库、不写日志。</summary>
    public async Task HandleIceAsync(WsConnection connection, CallIceFrame request, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var session = RequireSession(request.CallId);
        RequireParticipant(connection, session);
        var peerId = RequireUser(connection) == session.CallerId ? session.CalleeId : session.CallerId;
        await SendToUserAsync(peerId, request, cancellationToken, session.KindText, except: connection);
    }

    /// <summary>重连/登录后同步当前通话状态（没有进行中的通话时下发 call.state(null)）。</summary>
    public async Task SendStateAsync(WsConnection connection, CancellationToken cancellationToken)
    {
        var userId = connection.UserId;
        if (userId is null)
        {
            return;
        }

        if (!_activeByUser.TryGetValue(userId, out var callId) ||
            !_byCallId.TryGetValue(callId, out var session))
        {
            await connection.SendFrameAsync(new CallStateFrame(null), cancellationToken);
            return;
        }

        await SendStateAsync(connection, session, cancellationToken);
    }

    /// <summary>连接关闭：宽限期后若该用户仍无可用连接则结束通话。</summary>
    public void HandleConnectionClosed(WsConnection connection)
    {
        var userId = connection.UserId;
        if (userId is null || !_activeByUser.TryGetValue(userId, out var callId) ||
            !_byCallId.TryGetValue(callId, out var session))
        {
            return;
        }

        var grace = Math.Max(1, _options.DisconnectGraceSeconds);
        var cts = new CancellationTokenSource();
        lock (session.Sync)
        {
            session.DisconnectTimer?.Cancel();
            session.DisconnectTimer = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(grace), cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_connections.GetConnections(userId).Count > 0)
            {
                return;
            }

            var reason = session.State == CallState.Ringing && userId == session.CallerId
                ? "canceled"
                : "disconnected";
            try
            {
                await EndAsync(session, reason, CancellationToken.None);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "断线结束通话失败：{CallId}", session.CallId);
            }
        });
    }

    private void ScheduleRingTimeout(CallSession session)
    {
        var seconds = Math.Max(5, _options.RingTimeoutSeconds);
        var cts = new CancellationTokenSource();
        session.RingTimer = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                if (session.State == CallState.Ringing)
                {
                    await EndAsync(session, "timeout", CancellationToken.None);
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "振铃超时处理失败：{CallId}", session.CallId);
            }
        });
    }

    private static void CancelRingTimer(CallSession session)
    {
        var timer = Interlocked.Exchange(ref session.RingTimer, null);
        timer?.Cancel();
        timer?.Dispose();
    }

    /// <summary>结束会话（幂等）：先改内存态，再落库/写时间线/通知对端。</summary>
    private async Task EndAsync(CallSession session, string reason, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref session.EndedFlag, 1) != 0)
        {
            return;
        }

        lock (session.Sync)
        {
            session.EndReason ??= reason;
            session.State = CallState.Ended;
        }

        CancelRingTimer(session);
        lock (session.Sync)
        {
            session.DisconnectTimer?.Cancel();
        }

        var endedAt = Now();
        await UpdateEndedAsync(session.CallId, endedAt, reason, cancellationToken);

        _byCallId.TryRemove(session.CallId, out _);
        _activeByUser.TryRemove(new KeyValuePair<string, string>(session.CallerId, session.CallId));
        _activeByUser.TryRemove(new KeyValuePair<string, string>(session.CalleeId, session.CallId));

        var frame = new CallEndFrame(session.CallId, reason);
        await SendToUserAsync(session.CallerId, frame, cancellationToken);
        await SendToUserAsync(session.CalleeId, frame, cancellationToken);

        if (session.ConnectedAt is not null)
        {
            await WriteTimelineAsync(session, endedAt, cancellationToken);
        }

        _logger.LogInformation(
            "call.end：{CallId} reason={Reason}，接通={Connected}",
            session.CallId,
            reason,
            session.ConnectedAt is not null);
    }

    /// <summary>写时间线记录（system + call.completed）并广播 message.new。</summary>
    private async Task WriteTimelineAsync(CallSession session, long endedAt, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var systemMessages = scope.ServiceProvider.GetRequiredService<SystemMessageWriter>();
        var notifier = scope.ServiceProvider.GetRequiredService<ConversationRealtimeNotifier>();

        var conversation = await db.Conversations
            .FirstOrDefaultAsync(row => row.Id == session.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return;
        }

        // SeqAllocator 用原生命令，必须在显式事务里执行（同一事务内 seq +1、写消息、更新 last_seq）。
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var content = new SystemContent(
            "call.completed",
            session.CallerId,
            Text: BuildTimelineText(session, endedAt));
        var message = await systemMessages.AppendAsync(conversation, session.CallerId, content, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        notifier.PushNewMessage(message, new[] { session.CallerId, session.CalleeId });
    }

    private static string BuildTimelineText(CallSession session, long endedAt)
    {
        var kind = session.Kind == CallKind.Audio ? "语音通话" : "视频通话";
        var connectedAt = session.ConnectedAt ?? endedAt;
        var seconds = Math.Max(0, (int)Math.Round((endedAt - connectedAt) / 1000.0));
        var duration = seconds >= 3600
            ? $"{seconds / 3600}:{seconds % 3600 / 60:00}:{seconds % 60:00}"
            : $"{seconds / 60:00}:{seconds % 60:00}";
        var text = $"{kind} · {duration}";
        return string.Equals(session.EndReason, "disconnected", StringComparison.Ordinal)
            ? $"{text} · 连接中断"
            : text;
    }

    private async Task SendStateAsync(WsConnection connection, CallSession session, CancellationToken cancellationToken)
    {
        var peerId = connection.UserId == session.CallerId ? session.CalleeId : session.CallerId;
        var peer = await LoadPeerAsync(peerId, cancellationToken);
        await connection.SendFrameAsync(
            new CallStateFrame(
                session.CallId,
                session.State.ToString().ToLowerInvariant(),
                session.KindText,
                peer,
                session.CreatedAt,
                session.AnsweredAt,
                session.ConnectedAt),
            cancellationToken);
    }

    private async Task<(string CalleeId, string ConversationId)> ResolveDirectCounterpartAsync(
        string callerId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            throw ApiException.ValidationFailed("conversationId 不能为空");
        }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var conversation = await db.Conversations.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == conversationId, cancellationToken);
        if (conversation is null)
        {
            throw new ApiException(ErrorCodes.CallNotFound, "会话不存在", StatusCodes.Status404NotFound);
        }

        if (conversation.Type != ConversationType.Direct)
        {
            throw new ApiException(
                ErrorCodes.CallUnsupported,
                "群会话暂不支持通话",
                StatusCodes.Status400BadRequest);
        }

        var memberIds = await db.ConversationMembers.AsNoTracking()
            .Where(member => member.ConversationId == conversationId)
            .Select(member => member.UserId)
            .ToListAsync(cancellationToken);
        if (!memberIds.Contains(callerId, StringComparer.Ordinal))
        {
            throw Forbidden();
        }

        var calleeId = memberIds.FirstOrDefault(memberId => !string.Equals(memberId, callerId, StringComparison.Ordinal));
        if (calleeId is null)
        {
            throw Forbidden();
        }

        return (calleeId, conversationId);
    }

    private async Task<CallPeer> LoadPeerAsync(string userId, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var user = await db.Users.AsNoTracking()
            .Where(row => row.Id == userId)
            .Select(row => new { row.Id, row.DisplayName, row.AvatarFileId })
            .FirstOrDefaultAsync(cancellationToken);
        return user is null
            ? new CallPeer(userId, "未知用户")
            : new CallPeer(user.Id, user.DisplayName, user.AvatarFileId);
    }

    private async Task SendToUserAsync(
        string userId,
        FrameEnvelope frame,
        CancellationToken cancellationToken,
        string? kind = null,
        WsConnection? except = null)
    {
        foreach (var connection in _connections.GetConnections(userId))
        {
            if (except is not null && connection.ConnectionId == except.ConnectionId)
            {
                continue;
            }

            if (kind is not null && !connection.SupportsCallKind(kind))
            {
                continue;
            }

            await TrySendAsync(connection, frame, cancellationToken);
        }
    }

    private async Task TrySendAsync(WsConnection connection, FrameEnvelope frame, CancellationToken cancellationToken)
    {
        try
        {
            await connection.SendFrameAsync(frame, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "通话帧推送失败：连接 {ConnectionId}", connection.ConnectionId);
        }
    }

    private async Task InsertAsync(CallSession session, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        db.Calls.Add(new Call
        {
            Id = session.CallId,
            ConversationId = session.ConversationId,
            CallerId = session.CallerId,
            CalleeId = session.CalleeId,
            Kind = session.Kind,
            State = session.State,
            CreatedAt = session.CreatedAt,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task UpdateAnsweredAsync(string callId, long answeredAt, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        await db.Calls.Where(row => row.Id == callId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.State, CallState.Connecting)
                .SetProperty(row => row.AnsweredAt, answeredAt), cancellationToken);
    }

    private async Task UpdateConnectedAsync(string callId, long connectedAt, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        await db.Calls
            .Where(row => row.Id == callId && row.ConnectedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.State, CallState.Active)
                .SetProperty(row => row.ConnectedAt, connectedAt), cancellationToken);
    }

    private async Task UpdateEndedAsync(string callId, long endedAt, string reason, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        await db.Calls.Where(row => row.Id == callId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.State, CallState.Ended)
                .SetProperty(row => row.EndedAt, endedAt)
                .SetProperty(row => row.EndReason, reason), cancellationToken);
    }

    private CallSession RequireSession(string callId)
    {
        if (string.IsNullOrWhiteSpace(callId) || !_byCallId.TryGetValue(callId.Trim(), out var session))
        {
            throw new ApiException(ErrorCodes.CallNotFound, "通话不存在或已结束", StatusCodes.Status404NotFound);
        }

        return session;
    }

    private static string RequireUser(WsConnection connection) =>
        connection.UserId ?? throw new ApiException(
            ErrorCodes.AuthRequired,
            "认证后才能使用通话",
            StatusCodes.Status401Unauthorized);

    private void RequireParticipant(WsConnection connection, CallSession session)
    {
        var userId = RequireUser(connection);
        if (!string.Equals(userId, session.CallerId, StringComparison.Ordinal) &&
            !string.Equals(userId, session.CalleeId, StringComparison.Ordinal))
        {
            throw Forbidden();
        }
    }

    private static ApiException Forbidden() =>
        new(ErrorCodes.CallForbidden, "无权参与该通话", StatusCodes.Status403Forbidden);

    private void EnsureEnabled()
    {
        if (!_options.Enabled)
        {
            throw new ApiException(
                ErrorCodes.CallUnsupported,
                "当前服务端未启用通话",
                StatusCodes.Status400BadRequest);
        }
    }

    private static string NormalizeCallId(string? callId)
    {
        var value = callId?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 64)
        {
            throw ApiException.ValidationFailed("callId 不能为空且不超过 64 字符");
        }

        return value;
    }

    private static CallKind ParseKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "audio" => CallKind.Audio,
        "video" => CallKind.Video,
        _ => throw new ApiException(
            ErrorCodes.CallUnsupported,
            "通话类型不支持（audio / video）",
            StatusCodes.Status400BadRequest),
    };

    private static string KindText(CallKind kind) => kind == CallKind.Audio ? "audio" : "video";
}
