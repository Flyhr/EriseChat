package com.eisechat.mobile;

import android.app.Activity;
import android.app.Application;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;

import androidx.annotation.Nullable;
import androidx.core.app.NotificationCompat;
import androidx.core.app.NotificationManagerCompat;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.IOException;
import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.ThreadLocalRandom;
import java.util.concurrent.TimeUnit;

import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;
import okhttp3.WebSocket;
import okhttp3.WebSocketListener;

/**
 * 后台消息提醒：应用退到后台后由前台服务保持一条 WebSocket 连接，收到 message.new 时发送系统通知。
 *
 * <p>与 WebView 内连接的分工：应用在前台时完全由 Web 端负责提醒，本服务只维持连接、不弹通知；
 * 应用退到后台后才在通知栏提示。通知点击回传 conversationId 由 {@link BackgroundNotifyPlugin} 转给 Web 侧。</p>
 *
 * <p>协议遵循 protocol/protocol.md：连接 /ws 后 10 秒内发送 auth 首帧；收到 hello 校验 protocolVersion；
 * 应用层心跳每 25 秒发送带 ts 的 ping，收到 ping 立即回传同 ts 的 pong，30 秒无有效响应判定断线；
 * 断线按 1/2/5/10/30 秒加抖动重连；认证失败、协议不兼容、kicked、关闭码 4001/4002 终止连接。</p>
 *
 * <p>约束：连接使用当前登录 token（仅内存，不打印日志）；后台连接占用服务端每账号 3 条连接上限之一；
 * 本连接只接收推送与发通知，不发送 read.update，不推进已读或同步游标。</p>
 */
public class ChatSocketService extends Service {
    public static final String EXTRA_WS_URL = "eisechat_ws_url";
    public static final String EXTRA_API_BASE = "eisechat_api_base";
    public static final String EXTRA_TOKEN = "eisechat_token";
    public static final String EXTRA_DEVICE_ID = "eisechat_device_id";
    public static final String EXTRA_USER_ID = "eisechat_user_id";
    /** 由用户在前台显式开启（区别于系统 START_STICKY 重建）。 */
    public static final String EXTRA_USER_INITIATED = "eisechat_user_initiated";

    private static final String CHANNEL_SERVICE = "background_service";
    public static final String CHANNEL_MESSAGES = "messages";
    private static final int NOTIFICATION_ID_SERVICE = 10101;
    private static final int NOTIFICATION_ID_MESSAGE = 10102;
    private static final int MAX_PREVIEW_LENGTH = 120;

    /** 协议版本（protocol/protocol.md §1，当前为 1）。 */
    private static final int PROTOCOL_VERSION = 1;
    /** auth 首帧发送时限：连接建立后 10 秒内（protocol §4.1）。 */
    private static final long AUTH_TIMEOUT_MS = 10_000L;
    /** 应用层心跳间隔：25 秒（protocol §4.2）。 */
    private static final long HEARTBEAT_INTERVAL_MS = 25_000L;
    /** 无有效响应判定断线：30 秒（protocol §4.2）。 */
    private static final long HEARTBEAT_TIMEOUT_MS = 30_000L;
    /** 心跳检查节拍。 */
    private static final long HEARTBEAT_CHECK_MS = 5_000L;
    /** 重连退避：1/2/5/10/30 秒 + 随机抖动（AGENTS §5.8）。 */
    private static final long[] RECONNECT_DELAYS_MS = {1_000L, 2_000L, 5_000L, 10_000L, 30_000L};
    private static final long RECONNECT_JITTER_MAX_MS = 300L;

    /** 通知点击回调：由插件在 load 时注册，用于把 conversationId 转发给 WebView。 */
    public interface ClickReporter {
        void onNotificationClick(String conversationId);
    }

    private static volatile ClickReporter clickReporter;
    private static volatile boolean lifecycleInstalled;
    private static volatile boolean running;
    private static volatile boolean connected;
    private static volatile boolean appForeground = true;
    private static volatile String foregroundConversationId;
    private static volatile String pendingConversationId;
    /** 因被踢/认证失败等原因主动终止后，忽略同参数的系统自动重建，避免互踢循环。 */
    private static volatile boolean terminalStopped;
    /** 最近一次致命终止原因（auth_failed/protocol_mismatch/kicked/auth_closed），供状态展示使用。 */
    private static volatile String lastStopReason;

    private final Handler handler = new Handler(Looper.getMainLooper());
    private final OkHttpClient httpClient = new OkHttpClient.Builder()
        .connectTimeout(10, TimeUnit.SECONDS)
        .readTimeout(0, TimeUnit.MILLISECONDS)
        .writeTimeout(10, TimeUnit.SECONDS)
        // OkHttp 控制帧 ping（WebSocket 协议层）：与项目 JSON 心跳（protocol §4.2）相互独立，
        // 控制帧 pong 不回调 onMessage，因此不作为应用层存活依据。
        .pingInterval(25, TimeUnit.SECONDS)
        .build();

    private String wsUrl;
    private String apiBase;
    private String token;
    private String deviceId;
    private String userId;

    private WebSocket webSocket;
    private int generation;
    private boolean authenticated;
    private boolean stopped;
    private int reconnectAttempt;
    private long lastInboundAt;
    private long lastPingSentAt;

    private final Map<String, String> conversationTitles = new HashMap<>();
    private final Map<String, Boolean> mutedConversations = new HashMap<>();
    private final Map<String, String> memberNames = new HashMap<>();
    private final Runnable reconnectRunnable = this::connect;

    private final Runnable heartbeatRunnable = new Runnable() {
        @Override
        public void run() {
            if (stopped || !authenticated || webSocket == null) {
                return;
            }
            long now = System.currentTimeMillis();
            // 30 秒无有效响应：按断线处理（关闭当前连接并退避重连）。
            if (lastInboundAt > 0 && now - lastInboundAt > HEARTBEAT_TIMEOUT_MS) {
                closeSocket("heartbeat_timeout");
                scheduleReconnect();
                return;
            }
            if (now - lastPingSentAt >= HEARTBEAT_INTERVAL_MS) {
                lastPingSentAt = now;
                sendJsonFrame(webSocket, pingFrame(now), "heartbeat_ping_error");
            }
            handler.postDelayed(this, HEARTBEAT_CHECK_MS);
        }
    };

    /** 启动前台服务（应用在前台时由 Web 侧调用）；同参数重复调用会复用现有连接。 */
    public static void start(
        Context context,
        String wsUrl,
        String apiBase,
        String token,
        String deviceId,
        String userId
    ) {
        Intent intent = new Intent(context, ChatSocketService.class);
        intent.putExtra(EXTRA_WS_URL, wsUrl);
        intent.putExtra(EXTRA_API_BASE, apiBase);
        intent.putExtra(EXTRA_TOKEN, token);
        intent.putExtra(EXTRA_DEVICE_ID, deviceId);
        intent.putExtra(EXTRA_USER_ID, userId);
        intent.putExtra(EXTRA_USER_INITIATED, true);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            context.startForegroundService(intent);
        } else {
            context.startService(intent);
        }
    }

    /** 停止后台提醒服务（关闭常驻通知并断开连接）。显式停止允许下次重新启动。 */
    public static void stop(Context context) {
        terminalStopped = false;
        context.stopService(new Intent(context, ChatSocketService.class));
    }

    public static boolean isRunning() {
        return running;
    }

    /** WebSocket 是否已完成认证（hello 校验通过）。 */
    public static boolean isConnected() {
        return connected;
    }

    /** 最近一次致命终止原因；任务 B 的状态回传可复用，空表示未发生终止。 */
    @Nullable
    public static String getLastStopReason() {
        return lastStopReason;
    }

    public static void setClickReporter(ClickReporter reporter) {
        clickReporter = reporter;
    }

    /**
     * 监听应用前后台切换：前台统一由 WebView 负责提醒，后台才弹系统通知。
     * 注：Capacitor 8 的 BridgeActivity 将 onResume/onPause 声明为 final，无法在 MainActivity 覆写，
     * 因此用 Application.ActivityLifecycleCallbacks 观察。
     */
    public static void installLifecycleCallbacks(Context context) {
        if (lifecycleInstalled) {
            return;
        }
        Context appContext = context.getApplicationContext();
        if (!(appContext instanceof Application)) {
            return;
        }
        lifecycleInstalled = true;
        ((Application) appContext).registerActivityLifecycleCallbacks(new Application.ActivityLifecycleCallbacks() {
            @Override
            public void onActivityCreated(Activity activity, Bundle savedInstanceState) {
            }

            @Override
            public void onActivityStarted(Activity activity) {
            }

            @Override
            public void onActivityResumed(Activity activity) {
                setAppForeground(true);
            }

            @Override
            public void onActivityPaused(Activity activity) {
                setAppForeground(false);
            }

            @Override
            public void onActivityStopped(Activity activity) {
            }

            @Override
            public void onActivitySaveInstanceState(Activity activity, Bundle outState) {
            }

            @Override
            public void onActivityDestroyed(Activity activity) {
            }
        });
    }

    /** 应用前台/后台切换：前台时不再弹系统通知，交由 WebView 内提醒。 */
    public static void setAppForeground(boolean foreground) {
        appForeground = foreground;
    }

    /** 当前打开的会话：应用在前台时该会话不重复提醒（本实现前台统一不提醒，仅保留状态）。 */
    public static void setForegroundConversation(@Nullable String conversationId) {
        foregroundConversationId = conversationId;
    }

    /** 读取并清除冷启动由通知带起的待打开会话。 */
    @Nullable
    public static String consumePendingClick() {
        String value = pendingConversationId;
        pendingConversationId = null;
        return value;
    }

    /** MainActivity 在 onCreate/onNewIntent 调用：处理通知点击带起的会话。 */
    public static void handleLaunchIntent(@Nullable Intent intent) {
        if (intent == null) {
            return;
        }
        String conversationId = intent.getStringExtra("eisechat_conversation_id");
        if (conversationId == null || conversationId.trim().isEmpty()) {
            return;
        }
        intent.removeExtra("eisechat_conversation_id");
        pendingConversationId = conversationId;
        ClickReporter reporter = clickReporter;
        if (reporter != null) {
            reporter.onNotificationClick(conversationId);
        }
    }

    @Nullable
    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    @Override
    public void onCreate() {
        super.onCreate();
        createChannels();
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent == null) {
            // START_STICKY 被系统重建时没有参数，无法恢复连接（Android 15+ 也不允许从后台重启 dataSync），直接结束。
            stopSelf();
            return START_NOT_STICKY;
        }
        String nextWsUrl = intent.getStringExtra(EXTRA_WS_URL);
        String nextToken = intent.getStringExtra(EXTRA_TOKEN);
        String nextDeviceId = intent.getStringExtra(EXTRA_DEVICE_ID);
        String nextUserId = intent.getStringExtra(EXTRA_USER_ID);
        String nextApiBase = intent.getStringExtra(EXTRA_API_BASE);
        boolean userInitiated = intent.getBooleanExtra(EXTRA_USER_INITIATED, false);
        if (nextWsUrl == null || nextToken == null || nextDeviceId == null || nextUserId == null) {
            stopSelf();
            return START_NOT_STICKY;
        }
        boolean sameSession = nextWsUrl.equals(wsUrl)
            && nextToken.equals(token)
            && nextDeviceId.equals(deviceId)
            && nextUserId.equals(userId);
        if (sameSession && terminalStopped && !userInitiated) {
            // 被踢/认证失败后已被主动终止：忽略系统重建，避免互踢循环；仅用户在前台重新开启时允许恢复。
            stopSelf();
            return START_NOT_STICKY;
        }
        if (!sameSession) {
            // 账号/令牌/服务器变化：先让旧连接与回调失效并清理旧状态，再按新参数认证。
            resetConnectionState();
        }
        wsUrl = nextWsUrl;
        token = nextToken;
        deviceId = nextDeviceId;
        userId = nextUserId;
        apiBase = nextApiBase;
        terminalStopped = false;
        stopped = false;
        running = true;
        updateForegroundNotification(connected ? "正在接收新消息" : "正在连接");
        if (!sameSession || webSocket == null) {
            connect();
        }
        return START_STICKY;
    }

    @Override
    public void onDestroy() {
        stopped = true;
        running = false;
        connected = false;
        handler.removeCallbacksAndMessages(null);
        closeSocket("service_destroyed");
        resetConnectionState();
        super.onDestroy();
    }

    /** Android 15+ dataSync 前台服务累计时长到上限时由系统回调：断开连接并结束服务。 */
    @Override
    public void onTimeout(int startId, int fgsType) {
        stopped = true;
        running = false;
        connected = false;
        lastStopReason = "foreground_service_timeout";
        handler.removeCallbacksAndMessages(null);
        closeSocket("foreground_service_timeout");
        resetConnectionState();
        stopForeground(STOP_FOREGROUND_REMOVE);
        stopSelf();
    }

    private void createChannels() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) {
            return;
        }
        NotificationManager manager = getSystemService(NotificationManager.class);
        if (manager == null) {
            return;
        }
        NotificationChannel service = new NotificationChannel(
            CHANNEL_SERVICE,
            "后台消息服务",
            NotificationManager.IMPORTANCE_LOW
        );
        service.setDescription("应用在后台时接收新消息");
        service.setShowBadge(false);
        manager.createNotificationChannel(service);

        NotificationChannel messages = new NotificationChannel(
            CHANNEL_MESSAGES,
            "新消息",
            NotificationManager.IMPORTANCE_HIGH
        );
        messages.setDescription("收到新消息时提醒");
        manager.createNotificationChannel(messages);
    }

    private Notification buildServiceNotification(String text) {
        return new NotificationCompat.Builder(this, CHANNEL_SERVICE)
            .setSmallIcon(R.drawable.ic_stat_message)
            .setContentTitle("EiseChat")
            .setContentText(text)
            .setContentIntent(buildOpenAppIntent(null))
            .setOngoing(true)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .build();
    }

    /** 更新常驻通知状态文案（正在连接 / 正在接收新消息 / 正在重新连接）。 */
    private void updateForegroundNotification(String text) {
        try {
            startForeground(NOTIFICATION_ID_SERVICE, buildServiceNotification(text));
        } catch (Exception ignored) {
            // 服务已被系统停止等场景：不再展示状态，避免崩溃。
        }
    }

    private PendingIntent buildOpenAppIntent(@Nullable String conversationId) {
        Intent intent = new Intent(this, MainActivity.class);
        intent.setFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP | Intent.FLAG_ACTIVITY_CLEAR_TOP);
        if (conversationId != null) {
            intent.putExtra("eisechat_conversation_id", conversationId);
        }
        int flags = PendingIntent.FLAG_UPDATE_CURRENT;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            flags |= PendingIntent.FLAG_IMMUTABLE;
        }
        return PendingIntent.getActivity(this, conversationId == null ? 0 : conversationId.hashCode(), intent, flags);
    }

    private void connect() {
        if (stopped || webSocket != null || wsUrl == null) {
            return;
        }
        updateForegroundNotification("正在连接");
        connected = false;
        lastInboundAt = 0L;
        lastPingSentAt = 0L;
        final int connectGeneration = ++generation;
        final WebSocket[] socketRef = new WebSocket[1];
        final Runnable authTimeout = () -> {
            if (connectGeneration != generation || socketRef[0] != webSocket) {
                return;
            }
            if (!authenticated) {
                closeSocket("auth_timeout");
                scheduleReconnect();
            }
        };
        handler.postDelayed(authTimeout, AUTH_TIMEOUT_MS);

        Request request;
        try {
            request = new Request.Builder().url(wsUrl).build();
        } catch (IllegalArgumentException e) {
            handler.removeCallbacks(authTimeout);
            closeSocket("invalid_url");
            return;
        }
        WebSocket socket = httpClient.newWebSocket(request, new WebSocketListener() {
            @Override
            public void onOpen(WebSocket webSocket, Response response) {
                if (connectGeneration != generation || webSocket != socketRef[0]) {
                    return;
                }
                // protocol §4.1：连接建立后立即发送 auth 首帧（10 秒内必须完成认证）。
                sendAuthFrame(webSocket);
            }

            @Override
            public void onMessage(WebSocket webSocket, String text) {
                if (connectGeneration != generation || webSocket != socketRef[0]) {
                    return;
                }
                handler.removeCallbacks(authTimeout);
                lastInboundAt = System.currentTimeMillis();
                handleFrame(text, webSocket);
            }

            @Override
            public void onFailure(WebSocket webSocket, Throwable t, @Nullable Response response) {
                if (connectGeneration != generation || webSocket != socketRef[0]) {
                    return;
                }
                handler.removeCallbacks(authTimeout);
                handler.removeCallbacks(heartbeatRunnable);
                ChatSocketService.this.webSocket = null;
                authenticated = false;
                scheduleReconnect();
            }

            @Override
            public void onClosing(WebSocket webSocket, int code, String reason) {
                if (connectGeneration != generation || webSocket != socketRef[0]) {
                    return;
                }
                // 完成关闭握手；重连/终止由 onClosed 统一处理，避免出现第二套恢复机制。
                webSocket.close(1000, null);
            }

            @Override
            public void onClosed(WebSocket webSocket, int code, String reason) {
                if (connectGeneration != generation || webSocket != socketRef[0]) {
                    return;
                }
                handler.removeCallbacks(authTimeout);
                handler.removeCallbacks(heartbeatRunnable);
                ChatSocketService.this.webSocket = null;
                authenticated = false;
                if (stopped) {
                    return;
                }
                // 4002：认证失败/协议不兼容；4001：被服务端踢下线。两者均不再自动重连。
                if (code == 4001 || code == 4002) {
                    terminalStop(code == 4001 ? "kicked" : "auth_closed");
                    return;
                }
                scheduleReconnect();
            }
        });
        socketRef[0] = socket;
        webSocket = socket;
    }

    private void sendAuthFrame(WebSocket socket) {
        JSONObject frame = new JSONObject();
        try {
            frame.put("type", "auth");
            frame.put("reqId", "native-auth-" + generation);
            frame.put("token", token);
            frame.put("deviceId", deviceId);
            frame.put("protocolVersion", PROTOCOL_VERSION);
        } catch (Exception ignored) {
            closeSocket("auth_frame_error");
            return;
        }
        socket.send(frame.toString());
    }

    private JSONObject pingFrame(long timestamp) {
        JSONObject frame = new JSONObject();
        try {
            frame.put("type", "ping");
            frame.put("ts", timestamp);
        } catch (Exception ignored) {
            // 字段均为基础类型，不会失败。
        }
        return frame;
    }

    private void sendPongFrame(WebSocket socket, long timestamp) {
        JSONObject frame = new JSONObject();
        try {
            frame.put("type", "pong");
            frame.put("ts", timestamp);
        } catch (Exception ignored) {
            return;
        }
        sendJsonFrame(socket, frame, "pong_send_error");
    }

    private void sendJsonFrame(@Nullable WebSocket socket, JSONObject frame, String errorReason) {
        if (socket == null) {
            return;
        }
        try {
            if (!socket.send(frame.toString())) {
                handleSendFailure(errorReason);
            }
        } catch (Exception ignored) {
            handleSendFailure(errorReason);
        }
    }

    /** 发送失败视为网络故障：关闭当前连接并走既有退避重连，避免服务运行但不再收消息。 */
    private void handleSendFailure(String reason) {
        if (stopped || terminalStopped) {
            return;
        }
        closeSocket(reason);
        scheduleReconnect();
    }

    private void handleFrame(String text, WebSocket socket) {
        JSONObject frame;
        try {
            frame = new JSONObject(text);
        } catch (Exception e) {
            return;
        }
        String type = frame.optString("type", "");
        switch (type) {
            case "hello": {
                int serverVersion = frame.optInt("protocolVersion", -1);
                if (serverVersion != PROTOCOL_VERSION) {
                    // protocol §4.1 规则 4：协议版本不兼容时主动关闭，不重连。
                    terminalStop("protocol_mismatch");
                    return;
                }
                authenticated = true;
                connected = true;
                reconnectAttempt = 0;
                lastStopReason = null;
                updateForegroundNotification("正在接收新消息");
                startHeartbeat();
                fetchConversations();
                break;
            }
            case "ping":
                // protocol §4.2：收到 ping 立即回 pong，ts 原样回传。
                sendPongFrame(socket, frame.optLong("ts", 0L));
                break;
            case "pong":
                // 有效响应已由 onMessage 更新 lastInboundAt，无需额外处理。
                break;
            case "message.new":
                handleIncomingMessage(frame.optJSONObject("message"));
                break;
            case "conv.updated":
                applyConversations(frame.optJSONArray("conversations"));
                break;
            case "error":
                if (!authenticated) {
                    terminalStop("auth_failed");
                }
                break;
            case "kicked":
                // 账号被踢下线（超限/远程退出）：仅停止原生连接，不清理 WebView 登录态。
                terminalStop("kicked");
                break;
            default:
                break;
        }
    }

    private void startHeartbeat() {
        handler.removeCallbacks(heartbeatRunnable);
        long now = System.currentTimeMillis();
        lastInboundAt = now;
        lastPingSentAt = 0L;
        handler.postDelayed(heartbeatRunnable, HEARTBEAT_CHECK_MS);
    }

    private void handleIncomingMessage(@Nullable JSONObject message) {
        if (message == null) {
            return;
        }
        String conversationId = message.optString("conversationId", "");
        String senderId = message.optString("senderId", "");
        if (conversationId.isEmpty() || senderId.equals(userId)) {
            return;
        }
        if (Boolean.TRUE.equals(mutedConversations.get(conversationId))) {
            return;
        }
        if (appForeground) {
            return;
        }
        String title = conversationTitles.get(conversationId);
        if (title == null || title.isEmpty()) {
            title = memberNames.getOrDefault(senderId, "新消息");
        }
        String senderName = memberNames.getOrDefault(senderId, "成员");
        String preview = previewOf(message);
        String content = conversationTitles.containsKey(conversationId)
            ? senderName + "：" + preview
            : preview;
        showMessageNotification(conversationId, title, content);
    }

    private String previewOf(JSONObject message) {
        String msgType = message.optString("msgType", "text");
        Object content = message.opt("content");
        if (content instanceof String) {
            return truncate(((String) content).replace('\n', ' ').trim());
        }
        if (!(content instanceof JSONObject)) {
            return "[新消息]";
        }
        JSONObject object = (JSONObject) content;
        switch (msgType) {
            case "image":
                return "[图片]";
            case "file": {
                String name = object.optString("name", "");
                return name.isEmpty() ? "[文件]" : "[文件] " + name;
            }
            case "voice":
                return "[语音]";
            default:
                return "[新消息]";
        }
    }

    private String truncate(String text) {
        if (text.isEmpty()) {
            return "[新消息]";
        }
        return text.length() > MAX_PREVIEW_LENGTH ? text.substring(0, MAX_PREVIEW_LENGTH) + "…" : text;
    }

    private void showMessageNotification(String conversationId, String title, String content) {
        if (!NotificationManagerCompat.from(this).areNotificationsEnabled()) {
            return;
        }
        Notification notification = new NotificationCompat.Builder(this, CHANNEL_MESSAGES)
            .setSmallIcon(R.drawable.ic_stat_message)
            .setContentTitle(title)
            .setContentText(content)
            .setStyle(new NotificationCompat.BigTextStyle().bigText(content))
            .setContentIntent(buildOpenAppIntent(conversationId))
            .setAutoCancel(true)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_MESSAGE)
            .build();
        NotificationManagerCompat.from(this).notify(NOTIFICATION_ID_MESSAGE, notification);
    }

    /** 拉取会话列表：用于通知标题、免打扰状态与成员昵称（失败静默，不阻塞消息提醒）。 */
    private void fetchConversations() {
        final String base = apiBase;
        final String bearer = token;
        final int connectGeneration = generation;
        if (base == null || bearer == null) {
            return;
        }
        Request request;
        try {
            request = new Request.Builder()
                .url(trimTrailingSlash(base) + "/conversations")
                .header("Authorization", "Bearer " + bearer)
                .build();
        } catch (IllegalArgumentException e) {
            return;
        }
        final OkHttpClient plainClient = httpClient.newBuilder().readTimeout(15, TimeUnit.SECONDS).build();
        plainClient.newCall(request).enqueue(new okhttp3.Callback() {
            @Override
            public void onFailure(okhttp3.Call call, IOException e) {
                // 拉取失败时保持已有标题/免打扰缓存，不阻塞消息提醒。
            }

            @Override
            public void onResponse(okhttp3.Call call, Response response) {
                if (connectGeneration != generation || !bearer.equals(token)) {
                    // 旧连接/旧账号的迟到响应直接丢弃，避免影响新会话。
                    response.close();
                    return;
                }
                try (Response r = response) {
                    if (!r.isSuccessful() || r.body() == null) {
                        return;
                    }
                    applyConversations(new JSONArray(r.body().string()));
                } catch (Exception ignored) {
                    // 忽略解析失败。
                }
            }
        });
    }

    private synchronized void applyConversations(@Nullable JSONArray conversations) {
        if (conversations == null) {
            return;
        }
        for (int i = 0; i < conversations.length(); i++) {
            JSONObject conversation = conversations.optJSONObject(i);
            if (conversation == null) {
                continue;
            }
            String conversationId = conversation.optString("conversationId", "");
            if (conversationId.isEmpty()) {
                continue;
            }
            String title = conversation.optString("title", "");
            if (!title.isEmpty()) {
                conversationTitles.put(conversationId, title);
            }
            mutedConversations.put(conversationId, conversation.optBoolean("muted", false));
            JSONArray members = conversation.optJSONArray("members");
            if (members == null) {
                continue;
            }
            for (int j = 0; j < members.length(); j++) {
                JSONObject member = members.optJSONObject(j);
                if (member == null) {
                    continue;
                }
                String memberId = member.optString("userId", "");
                String displayName = member.optString("displayName", "");
                if (!memberId.isEmpty() && !displayName.isEmpty()) {
                    memberNames.put(memberId, displayName);
                }
            }
        }
    }

    private static String trimTrailingSlash(String value) {
        return value.endsWith("/") ? value.substring(0, value.length() - 1) : value;
    }

    private void scheduleReconnect() {
        if (stopped || terminalStopped) {
            return;
        }
        long base = RECONNECT_DELAYS_MS[Math.min(reconnectAttempt, RECONNECT_DELAYS_MS.length - 1)];
        reconnectAttempt += 1;
        long jitter = ThreadLocalRandom.current().nextLong(RECONNECT_JITTER_MAX_MS + 1);
        handler.removeCallbacks(heartbeatRunnable);
        handler.removeCallbacks(reconnectRunnable);
        connected = false;
        if (running) {
            updateForegroundNotification("正在重新连接");
        }
        handler.postDelayed(reconnectRunnable, base + jitter);
    }

    /** 终止当前原生连接并停止服务：用于认证失败、协议不兼容、被踢等不可重连场景。 */
    private void terminalStop(String reason) {
        lastStopReason = reason;
        terminalStopped = true;
        stopped = true;
        running = false;
        connected = false;
        handler.removeCallbacksAndMessages(null);
        closeSocket(reason);
        clearRuntimeState();
        stopForeground(STOP_FOREGROUND_REMOVE);
        stopSelf();
    }

    /** 清理随连接存在的会话缓存、去重状态、通知与待点击数据（账号/服务器切换、终止、销毁时调用）。 */
    private void clearRuntimeState() {
        conversationTitles.clear();
        mutedConversations.clear();
        memberNames.clear();
        pendingConversationId = null;
        try {
            NotificationManagerCompat.from(this).cancel(NOTIFICATION_ID_MESSAGE);
        } catch (Exception ignored) {
            // 通知不存在或系统限制：忽略清理失败。
        }
    }

    /** 参数变化时使旧连接与回调失效，并清理旧会话状态，避免旧结果污染新会话。 */
    private void resetConnectionState() {
        generation += 1;
        handler.removeCallbacksAndMessages(null);
        closeSocket("session_reset");
        authenticated = false;
        connected = false;
        reconnectAttempt = 0;
        clearRuntimeState();
    }

    private void closeSocket(String reason) {
        generation += 1;
        WebSocket socket = webSocket;
        webSocket = null;
        authenticated = false;
        connected = false;
        handler.removeCallbacks(heartbeatRunnable);
        handler.removeCallbacks(reconnectRunnable);
        if (socket != null) {
            try {
                socket.close(1000, reason);
            } catch (Exception ignored) {
                // 关闭异常忽略。
            }
        }
    }
}
