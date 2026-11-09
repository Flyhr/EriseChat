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
import java.util.ArrayList;
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
    private static final String MESSAGE_TAG_PREFIX = "eisechat-conv:";
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
        void onNotificationClick(String conversationId, @Nullable String userId, @Nullable String serverUrl);
    }

    /** 服务状态回调：由插件在 load 时注册，实时更新设置页显示。 */
    public interface StateReporter {
        void onStateChanged(boolean running, boolean connected, @Nullable String stopReason);
    }

    private static volatile ClickReporter clickReporter;
    private static volatile StateReporter stateReporter;
    private static volatile boolean lifecycleInstalled;
    private static volatile boolean running;
    private static volatile boolean connected;
    private static volatile boolean appForeground = true;
    private static volatile String foregroundConversationId;
    private static volatile String pendingConversationId;
    /** 通知点击携带的来源账号与服务器，用于冷启动后校验是否仍属当前登录。 */
    private static volatile String pendingClickUserId;
    private static volatile String pendingClickServer;
    /** 因被踢/认证失败等原因主动终止后，忽略同参数的系统自动重建，避免互踢循环。 */
    private static volatile boolean terminalStopped;
    /** 最近一次致命终止原因（auth_failed/protocol_mismatch/kicked/auth_closed），供状态展示使用。 */
    private static volatile String lastStopReason;
    /** 终止原因所属的会话（userId|wsUrl）；跨服务实例/进程保留，防止 WebView 重载后自动恢复。 */
    private static volatile String lastStopSessionKey;
    /** 当前服务实例已配置的会话键；切换账号/服务器时据此清理旧的终止标记。 */
    private static volatile String configuredSessionKey;

    private static final String STATE_PREFS = "eisechat_background_state";
    private static final String PREF_TERMINAL_SESSION = "terminal_session";
    private static final String PREF_TERMINAL_REASON = "terminal_reason";

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
    /** 每个会话已展示通知对应的消息（用于去重、编辑与撤回匹配）。 */
    private final Map<String, String> notifiedMessageIds = new HashMap<>();
    private final Map<String, Long> notifiedMessageSeqs = new HashMap<>();
    private final Map<String, String> notifiedTitles = new HashMap<>();
    /** 会话状态就绪前到达的最新消息候选（每会话仅保留最新一条）。 */
    private final Map<String, JSONObject> pendingCandidates = new HashMap<>();
    /** 每个会话已应用状态的版本：事件更新递增，用于 GET 响应按会话合并。 */
    private final Map<String, Long> conversationVersions = new HashMap<>();
    private long conversationVersionCounter;
    private static volatile ChatSocketService currentInstance;
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

    /** 启动前台服务（用户显式开启或登录态自动同步）；同参数重复调用会复用现有连接。 */
    public static void start(
        Context context,
        String wsUrl,
        String apiBase,
        String token,
        String deviceId,
        String userId,
        boolean userInitiated
    ) {
        Intent intent = new Intent(context, ChatSocketService.class);
        intent.putExtra(EXTRA_WS_URL, wsUrl);
        intent.putExtra(EXTRA_API_BASE, apiBase);
        intent.putExtra(EXTRA_TOKEN, token);
        intent.putExtra(EXTRA_DEVICE_ID, deviceId);
        intent.putExtra(EXTRA_USER_ID, userId);
        intent.putExtra(EXTRA_USER_INITIATED, userInitiated);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            context.startForegroundService(intent);
        } else {
            context.startService(intent);
        }
    }

    /** 首次启动服务前创建通知渠道（幂等）；供插件在权限检查前预先创建。 */
    public static void ensureChannels(Context context) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) {
            return;
        }
        NotificationManager manager = context.getSystemService(NotificationManager.class);
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

    /** 停止后台提醒服务（关闭常驻通知并断开连接）。显式停止允许下次重新启动。 */
    public static void stop(Context context) {
        terminalStopped = false;
        lastStopReason = null;
        lastStopSessionKey = null;
        configuredSessionKey = null;
        context
            .getApplicationContext()
            .getSharedPreferences(STATE_PREFS, Context.MODE_PRIVATE)
            .edit()
            .remove(PREF_TERMINAL_SESSION)
            .remove(PREF_TERMINAL_REASON)
            .apply();
        context.stopService(new Intent(context, ChatSocketService.class));
    }

    private static String sessionKeyOf(String userId, String wsUrl) {
        return (userId == null ? "" : userId) + "|" + (wsUrl == null ? "" : wsUrl);
    }

    private String persistedTerminalReason(String sessionKey) {
        if (!sessionKey.equals(lastStopSessionKey) && lastStopSessionKey != null) {
            return null;
        }
        String storedSession = getSharedPreferences(STATE_PREFS, MODE_PRIVATE)
            .getString(PREF_TERMINAL_SESSION, null);
        if (!sessionKey.equals(storedSession)) {
            return null;
        }
        return getSharedPreferences(STATE_PREFS, MODE_PRIVATE).getString(PREF_TERMINAL_REASON, null);
    }

    private void rememberTerminalState(String reason) {
        String sessionKey = sessionKeyOf(userId, wsUrl);
        lastStopReason = reason;
        lastStopSessionKey = sessionKey;
        getSharedPreferences(STATE_PREFS, MODE_PRIVATE)
            .edit()
            .putString(PREF_TERMINAL_SESSION, sessionKey)
            .putString(PREF_TERMINAL_REASON, reason)
            .apply();
    }

    private void clearTerminalState() {
        terminalStopped = false;
        lastStopReason = null;
        lastStopSessionKey = null;
        getSharedPreferences(STATE_PREFS, MODE_PRIVATE)
            .edit()
            .remove(PREF_TERMINAL_SESSION)
            .remove(PREF_TERMINAL_REASON)
            .apply();
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

    public static void setStateReporter(StateReporter reporter) {
        stateReporter = reporter;
    }

    private void publishState() {
        StateReporter reporter = stateReporter;
        if (reporter != null) {
            reporter.onStateChanged(running, connected, lastStopReason);
        }
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
    public static JSONObject consumePendingClick() {
        String conversationId = pendingConversationId;
        String userId = pendingClickUserId;
        String serverUrl = pendingClickServer;
        pendingConversationId = null;
        pendingClickUserId = null;
        pendingClickServer = null;
        if (conversationId == null || conversationId.trim().isEmpty()) {
            return null;
        }
        JSONObject result = new JSONObject();
        try {
            result.put("conversationId", conversationId);
            if (userId != null && !userId.trim().isEmpty()) {
                result.put("userId", userId);
            }
            if (serverUrl != null && !serverUrl.trim().isEmpty()) {
                result.put("serverUrl", serverUrl);
            }
        } catch (Exception ignored) {
            return null;
        }
        return result;
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
        String clickUserId = intent.getStringExtra("eisechat_click_user_id");
        String clickServer = intent.getStringExtra("eisechat_click_server");
        intent.removeExtra("eisechat_conversation_id");
        intent.removeExtra("eisechat_click_user_id");
        intent.removeExtra("eisechat_click_server");
        pendingConversationId = conversationId;
        pendingClickUserId = clickUserId;
        pendingClickServer = clickServer;
        ClickReporter reporter = clickReporter;
        if (reporter != null) {
            reporter.onNotificationClick(conversationId, clickUserId, clickServer);
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
        currentInstance = this;
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
        String sessionKey = sessionKeyOf(nextUserId, nextWsUrl);
        String storedTerminalReason = persistedTerminalReason(sessionKey);
        if (!userInitiated && storedTerminalReason != null) {
            // 被踢/认证失败/超时终止后仅允许用户主动开启恢复；WebView 重载、系统重建和服务新实例的
            // 空连接字段都不能绕过（会话键持久化，跨进程实例有效）。
            lastStopReason = storedTerminalReason;
            lastStopSessionKey = sessionKey;
            publishState();
            stopSelf();
            return START_NOT_STICKY;
        }
        if (!sameSession) {
            // 账号/令牌/服务器变化：先让旧连接与回调失效并清理旧状态，再按新参数认证。
            resetConnectionState();
        }
        if (userInitiated) {
            clearTerminalState();
        } else if (configuredSessionKey != null && !configuredSessionKey.equals(sessionKey)) {
            // 自动同步切换到另一个账号/服务器：旧会话的终止标记不再适用。
            clearTerminalState();
        }
        configuredSessionKey = sessionKey;
        wsUrl = nextWsUrl;
        token = nextToken;
        deviceId = nextDeviceId;
        userId = nextUserId;
        apiBase = nextApiBase;
        terminalStopped = false;
        stopped = false;
        running = true;
        publishState();
        if (!promoteForeground(connected ? "正在接收新消息" : "正在连接")) {
            return START_NOT_STICKY;
        }
        if (!sameSession || webSocket == null) {
            connect();
        }
        return START_STICKY;
    }

    /** 首次提升前台服务失败时明确终止并回传原因，避免留下「运行中」的错误状态。 */
    private boolean promoteForeground(String text) {
        try {
            startForeground(NOTIFICATION_ID_SERVICE, buildServiceNotification(text));
            return true;
        } catch (Exception error) {
            lastStopReason = "foreground_start_failed";
            terminalStopped = true;
            stopped = true;
            running = false;
            connected = false;
            handler.removeCallbacksAndMessages(null);
            closeSocket("foreground_start_failed");
            clearRuntimeState();
            publishState();
            stopSelf();
            return false;
        }
    }

    @Override
    public void onDestroy() {
        currentInstance = null;
        stopped = true;
        running = false;
        closeSocket("service_destroyed");
        clearRuntimeState();
        publishState();
        super.onDestroy();
    }

    /** Android 15+ dataSync 前台服务累计时长到上限时由系统回调：断开连接并结束服务。 */
    @Override
    public void onTimeout(int startId, int fgsType) {
        stopped = true;
        running = false;
        lastStopReason = "foreground_service_timeout";
        closeSocket("foreground_service_timeout");
        clearRuntimeState();
        publishState();
        stopForeground(STOP_FOREGROUND_REMOVE);
        stopSelf();
    }

    private void createChannels() {
        ensureChannels(this);
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
            // 通知携带来源账号与服务器：冷启动/旧通知可据登录态判断是否仍应打开。
            if (userId != null) {
                intent.putExtra("eisechat_click_user_id", userId);
            }
            if (apiBase != null) {
                intent.putExtra("eisechat_click_server", apiBase);
            }
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
                handler.post(() -> {
                    if (connectGeneration != generation || webSocket != socketRef[0]) {
                        return;
                    }
                    // protocol §4.1：连接建立后立即发送 auth 首帧（10 秒内必须完成认证）。
                    sendAuthFrame(webSocket);
                });
            }

            @Override
            public void onMessage(WebSocket webSocket, String text) {
                // 统一回到服务 Handler 线程：连接状态与通知缓存只在主线程读写。
                handler.post(() -> {
                    if (connectGeneration != generation || webSocket != socketRef[0]) {
                        return;
                    }
                    handler.removeCallbacks(authTimeout);
                    lastInboundAt = System.currentTimeMillis();
                    handleFrame(text, webSocket);
                });
            }

            @Override
            public void onFailure(WebSocket webSocket, Throwable t, @Nullable Response response) {
                handler.post(() -> {
                    if (connectGeneration != generation || webSocket != socketRef[0]) {
                        return;
                    }
                    handler.removeCallbacks(authTimeout);
                    handler.removeCallbacks(heartbeatRunnable);
                    ChatSocketService.this.webSocket = null;
                    authenticated = false;
                    scheduleReconnect();
                });
            }

            @Override
            public void onClosing(WebSocket webSocket, int code, String reason) {
                handler.post(() -> {
                    if (connectGeneration != generation || webSocket != socketRef[0]) {
                        return;
                    }
                    // 服务端以 4001/4002 关闭时在 onClosing 已能拿到终止语义，不能因客户端回 1000 完成握手而丢失。
                    if (code == 4001 || code == 4002) {
                        terminalStop(code == 4001 ? "kicked" : "auth_closed");
                        return;
                    }
                    // 普通关闭：完成握手；重连由 onClosed 统一处理，避免出现第二套恢复机制。
                    webSocket.close(1000, null);
                });
            }

            @Override
            public void onClosed(WebSocket webSocket, int code, String reason) {
                handler.post(() -> {
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
                });
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
                pendingCandidates.clear();
                publishState();
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
            case "message.edited":
                handleEditedMessage(frame);
                break;
            case "message.recalled":
                handleRecalledMessage(frame);
                break;
            case "conv.updated":
                applyConversations(
                    frame.optJSONArray("conversations"),
                    frame.optJSONArray("removedConversationIds")
                );
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
        String messageId = message.optString("messageId", "");
        long seq = message.optLong("seq", 0L);
        if (conversationId.isEmpty() || messageId.isEmpty() || senderId.equals(userId)) {
            return;
        }
        if (messageId.equals(notifiedMessageIds.get(conversationId))
            || seq <= notifiedMessageSeqs.getOrDefault(conversationId, 0L)) {
            return;
        }
        if (!hasConversationState(conversationId)) {
            // 该会话标题/免打扰未知时不能按「允许提醒」处理：每会话只保留最新候选，状态就绪后复核。
            pendingCandidates.put(conversationId, message);
            return;
        }
        if (Boolean.TRUE.equals(mutedConversations.get(conversationId))) {
            return;
        }
        if (appForeground) {
            return;
        }
        showMessageNotification(conversationId, message);
    }

    /** 会话状态就绪后复核初始化期间暂存的最新候选；未知会话继续保留等待状态到达。 */
    private void flushPendingCandidates() {
        if (pendingCandidates.isEmpty()) {
            return;
        }
        java.util.Iterator<Map.Entry<String, JSONObject>> iterator = pendingCandidates.entrySet().iterator();
        while (iterator.hasNext()) {
            Map.Entry<String, JSONObject> entry = iterator.next();
            String conversationId = entry.getKey();
            if (!hasConversationState(conversationId)) {
                continue;
            }
            iterator.remove();
            if (Boolean.TRUE.equals(mutedConversations.get(conversationId))) {
                continue;
            }
            if (!appForeground) {
                showMessageNotification(conversationId, entry.getValue());
            }
        }
    }

    /** 会话状态（标题/免打扰）是否已知；未知不能按允许提醒处理。 */
    private boolean hasConversationState(String conversationId) {
        return mutedConversations.containsKey(conversationId);
    }

    /** 已展示通知的摘要更新（message.edited）：同 tag/ID 原地更新，不再播放提示音。 */
    private void handleEditedMessage(@Nullable JSONObject message) {
        if (message == null) {
            return;
        }
        String conversationId = message.optString("conversationId", "");
        String messageId = message.optString("messageId", "");
        if (conversationId.isEmpty() || messageId.isEmpty()) {
            return;
        }
        // 初始化期间暂存的候选同样按编辑更新，避免随后复核时仍展示旧内容。
        JSONObject candidate = pendingCandidates.get(conversationId);
        if (candidate != null && messageId.equals(candidate.optString("messageId", ""))) {
            try {
                candidate.put("content", message.optString("content", ""));
            } catch (Exception ignored) {
                pendingCandidates.remove(conversationId);
            }
        }
        if (!messageId.equals(notifiedMessageIds.get(conversationId))) {
            return;
        }
        String title = notifiedTitles.get(conversationId);
        if (title == null) {
            return;
        }
        String senderId = message.optString("operatorId", "");
        String preview = truncate(message.optString("content", "").replace('\n', ' ').trim());
        String content = conversationTitles.containsKey(conversationId)
            ? memberNames.getOrDefault(senderId, "成员") + "：" + preview
            : preview;
        updateMessageNotification(conversationId, title, content);
    }

    /** 当前通知对应消息被撤回：取消该通知，避免继续展示已撤回正文。 */
    private void handleRecalledMessage(@Nullable JSONObject message) {
        if (message == null) {
            return;
        }
        String conversationId = message.optString("conversationId", "");
        String messageId = message.optString("messageId", "");
        if (conversationId.isEmpty() || messageId.isEmpty()) {
            return;
        }
        // 候选被撤回后不再进入通知队列，避免初始化完成后弹出已撤回内容。
        JSONObject candidate = pendingCandidates.get(conversationId);
        if (candidate != null && messageId.equals(candidate.optString("messageId", ""))) {
            pendingCandidates.remove(conversationId);
        }
        if (!messageId.equals(notifiedMessageIds.get(conversationId))) {
            // 撤回的是更早的消息：保留该会话较新消息的通知。
            return;
        }
        cancelMessageNotification(conversationId);
    }

    private void showMessageNotification(String conversationId, JSONObject message) {
        if (!NotificationManagerCompat.from(this).areNotificationsEnabled()) {
            return;
        }
        String senderId = message.optString("senderId", "");
        String title = conversationTitles.get(conversationId);
        if (title == null || title.isEmpty()) {
            title = memberNames.getOrDefault(senderId, "新消息");
        }
        String content = conversationTitles.containsKey(conversationId)
            ? memberNames.getOrDefault(senderId, "成员") + "：" + previewOf(message)
            : previewOf(message);
        String messageId = message.optString("messageId", "");
        long seq = message.optLong("seq", 0L);
        boolean update = notifiedMessageIds.containsKey(conversationId);
        Notification notification = new NotificationCompat.Builder(this, CHANNEL_MESSAGES)
            .setSmallIcon(R.drawable.ic_stat_message)
            .setContentTitle(title)
            .setContentText(content)
            .setStyle(new NotificationCompat.BigTextStyle().bigText(content))
            .setContentIntent(buildOpenAppIntent(conversationId))
            .setAutoCancel(true)
            .setOnlyAlertOnce(update)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_MESSAGE)
            .build();
        NotificationManagerCompat.from(this).notify(
            messageTag(conversationId),
            messageNotificationId(conversationId),
            notification
        );
        notifiedMessageIds.put(conversationId, messageId);
        notifiedMessageSeqs.put(conversationId, seq);
        notifiedTitles.put(conversationId, title);
    }

    private void updateMessageNotification(String conversationId, String title, String content) {
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
            .setOnlyAlertOnce(true)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_MESSAGE)
            .build();
        NotificationManagerCompat.from(this).notify(
            messageTag(conversationId),
            messageNotificationId(conversationId),
            notification
        );
    }

    private void cancelMessageNotification(String conversationId) {
        try {
            NotificationManagerCompat.from(this).cancel(
                messageTag(conversationId),
                messageNotificationId(conversationId)
            );
        } catch (Exception ignored) {
            // 通知不存在或系统限制：忽略。
        }
        notifiedMessageIds.remove(conversationId);
        notifiedMessageSeqs.remove(conversationId);
        notifiedTitles.remove(conversationId);
    }

    private static String messageTag(String conversationId) {
        return MESSAGE_TAG_PREFIX + conversationId;
    }

    private static int messageNotificationId(String conversationId) {
        return NOTIFICATION_ID_MESSAGE + (conversationId.hashCode() & 0x00FFFFFF);
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

    /** 拉取会话列表：用于通知标题、免打扰状态与成员昵称（失败静默，不阻塞消息提醒）。 */
    private void fetchConversations() {
        final String base = apiBase;
        final String bearer = token;
        final int connectGeneration = generation;
        final Map<String, Long> versionsAtRequest = new HashMap<>(conversationVersions);
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
                // 网络读取与 JSON 解析留在 OkHttp 回调线程；只把解析结果交给 Handler 修改共享状态。
                JSONArray conversations;
                try (Response r = response) {
                    if (!r.isSuccessful() || r.body() == null) {
                        return;
                    }
                    conversations = new JSONArray(r.body().string());
                } catch (Exception ignored) {
                    return;
                }
                handler.post(() -> {
                    if (connectGeneration != generation || !bearer.equals(token)) {
                        // 旧连接/旧账号的迟到响应直接丢弃，避免影响新会话。
                        return;
                    }
                    applyFetchedConversations(conversations, versionsAtRequest);
                });
            }
        });
    }

    /** GET 结果按会话合并：请求后被事件更新过的会话跳过，其余会话用它完成初始化。 */
    private void applyFetchedConversations(JSONArray conversations, Map<String, Long> versionsAtRequest) {
        for (int i = 0; i < conversations.length(); i++) {
            JSONObject conversation = conversations.optJSONObject(i);
            if (conversation == null) {
                continue;
            }
            String conversationId = conversation.optString("conversationId", "");
            if (conversationId.isEmpty()) {
                continue;
            }
            Long versionAtRequest = versionsAtRequest.get(conversationId);
            Long versionNow = conversationVersions.get(conversationId);
            if (versionAtRequest != null || versionNow != null) {
                if (versionAtRequest == null || !versionAtRequest.equals(versionNow)) {
                    // 该会话在请求期间已收到更新：保留事件里的新状态。
                    continue;
                }
            }
            applyConversation(conversation);
        }
        flushPendingCandidates();
    }

    /** 应用事件中的会话变更；removedConversationIds 同时清理缓存、候选与通知。 */
    private void applyConversations(
        @Nullable JSONArray conversations,
        @Nullable JSONArray removedConversationIds
    ) {
        if (removedConversationIds != null) {
            for (int i = 0; i < removedConversationIds.length(); i++) {
                String conversationId = removedConversationIds.optString(i, "");
                if (conversationId.isEmpty()) {
                    continue;
                }
                conversationVersions.put(conversationId, ++conversationVersionCounter);
                conversationTitles.remove(conversationId);
                mutedConversations.remove(conversationId);
                pendingCandidates.remove(conversationId);
                cancelMessageNotification(conversationId);
            }
        }
        if (conversations != null) {
            for (int i = 0; i < conversations.length(); i++) {
                JSONObject conversation = conversations.optJSONObject(i);
                if (conversation == null) {
                    continue;
                }
                String conversationId = conversation.optString("conversationId", "");
                if (conversationId.isEmpty()) {
                    continue;
                }
                applyConversation(conversation);
                conversationVersions.put(conversationId, ++conversationVersionCounter);
            }
        }
        flushPendingCandidates();
    }

    /** 应用单个会话的标题、免打扰与成员昵称（调用方在 Handler 线程）。 */
    private void applyConversation(JSONObject conversation) {
        String conversationId = conversation.optString("conversationId", "");
        if (conversationId.isEmpty()) {
            return;
        }
        String title = conversation.optString("title", "");
        if (!title.isEmpty()) {
            conversationTitles.put(conversationId, title);
        }
        mutedConversations.put(conversationId, conversation.optBoolean("muted", false));
        JSONArray members = conversation.optJSONArray("members");
        if (members == null) {
            return;
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

    /** Web 端同步已确认的会话状态（本人修改免打扰等），统一投递到服务 Handler 线程处理。 */
    public static void updateConversationsJson(@Nullable JSONArray conversations) {
        ChatSocketService service = currentInstance;
        if (service == null || conversations == null) {
            return;
        }
        service.handler.post(() -> service.applyConversations(conversations, null));
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
        publishState();
        if (running) {
            updateForegroundNotification("正在重新连接");
        }
        handler.postDelayed(reconnectRunnable, base + jitter);
    }

    /** 终止当前原生连接并停止服务：用于认证失败、协议不兼容、被踢等不可重连场景。 */
    private void terminalStop(String reason) {
        rememberTerminalState(reason);
        terminalStopped = true;
        stopped = true;
        running = false;
        connected = false;
        handler.removeCallbacksAndMessages(null);
        closeSocket(reason);
        clearRuntimeState();
        publishState();
        stopForeground(STOP_FOREGROUND_REMOVE);
        stopSelf();
    }

    /** 清理随连接存在的会话缓存、去重状态与通知（账号/服务器切换、终止、销毁时调用）。 */
    private void clearRuntimeState() {
        for (String conversationId : new ArrayList<>(notifiedMessageIds.keySet())) {
            cancelMessageNotification(conversationId);
        }
        notifiedMessageIds.clear();
        notifiedMessageSeqs.clear();
        notifiedTitles.clear();
        conversationTitles.clear();
        mutedConversations.clear();
        memberNames.clear();
        pendingCandidates.clear();
        conversationVersions.clear();
        conversationVersionCounter = 0;
        // 待点击数据独立于连接缓存：只在消费或明确判定来源不匹配时清除，服务重建不得丢失。
    }

    /** 参数变化时使旧连接与回调失效，并清理旧会话状态，避免旧结果污染新会话。 */
    private void resetConnectionState() {
        closeSocket("session_reset");
        clearRuntimeState();
        reconnectAttempt = 0;
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
