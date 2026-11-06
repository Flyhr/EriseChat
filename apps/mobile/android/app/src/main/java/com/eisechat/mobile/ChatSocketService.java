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
import java.io.UnsupportedEncodingException;
import java.net.URI;
import java.util.HashMap;
import java.util.Map;
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
 * <p>约束：连接使用当前登录 token（仅内存，不打印日志）；后台连接占用服务端每账号 3 条连接上限之一。</p>
 */
public class ChatSocketService extends Service {
    public static final String EXTRA_WS_URL = "eisechat_ws_url";
    public static final String EXTRA_API_BASE = "eisechat_api_base";
    public static final String EXTRA_TOKEN = "eisechat_token";
    public static final String EXTRA_DEVICE_ID = "eisechat_device_id";
    public static final String EXTRA_USER_ID = "eisechat_user_id";

    private static final String CHANNEL_SERVICE = "background_service";
    private static final String CHANNEL_MESSAGES = "messages";
    private static final int NOTIFICATION_ID_SERVICE = 10101;
    private static final int NOTIFICATION_ID_MESSAGE = 10102;
    private static final int MAX_MESSAGE_ID_LENGTH = 64;
    private static final int MAX_PREVIEW_LENGTH = 120;

    private static final long[] RECONNECT_DELAYS_MS = {1_000L, 2_000L, 5_000L, 10_000L, 30_000L};

    /** 通知点击回调：由插件在 load 时注册，用于把 conversationId 转发给 WebView。 */
    public interface ClickReporter {
        void onNotificationClick(String conversationId);
    }

    private static volatile ClickReporter clickReporter;
    private static volatile boolean lifecycleInstalled;
    private static volatile boolean running;
    private static volatile boolean appForeground = true;
    private static volatile String foregroundConversationId;
    private static volatile String pendingConversationId;

    private final Handler handler = new Handler(Looper.getMainLooper());
    private final OkHttpClient httpClient = new OkHttpClient.Builder()
        .connectTimeout(10, TimeUnit.SECONDS)
        .readTimeout(0, TimeUnit.MILLISECONDS)
        .writeTimeout(10, TimeUnit.SECONDS)
        .pingInterval(25, TimeUnit.SECONDS)
        .build();

    private String wsUrl;
    private String apiBase;
    private String token;
    private String deviceId;
    private String userId;

    private WebSocket webSocket;
    private boolean authenticated;
    private boolean stopped;
    private int reconnectAttempt;
    private final Map<String, String> conversationTitles = new HashMap<>();
    private final Map<String, Boolean> mutedConversations = new HashMap<>();
    private final Map<String, String> memberNames = new HashMap<>();
    private final Runnable reconnectRunnable = this::connect;

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
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            context.startForegroundService(intent);
        } else {
            context.startService(intent);
        }
    }

    /** 停止后台提醒服务（关闭常驻通知并断开连接）。 */
    public static void stop(Context context) {
        context.stopService(new Intent(context, ChatSocketService.class));
    }

    public static boolean isRunning() {
        return running;
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
        if (nextWsUrl == null || nextToken == null || nextDeviceId == null || nextUserId == null) {
            stopSelf();
            return START_NOT_STICKY;
        }
        boolean sameSession = nextWsUrl.equals(wsUrl)
            && nextToken.equals(token)
            && nextDeviceId.equals(deviceId)
            && nextUserId.equals(userId);
        wsUrl = nextWsUrl;
        token = nextToken;
        deviceId = nextDeviceId;
        userId = nextUserId;
        apiBase = nextApiBase;
        stopped = false;
        running = true;
        startForeground(NOTIFICATION_ID_SERVICE, buildServiceNotification());
        if (!sameSession || webSocket == null) {
            connect();
        }
        return START_STICKY;
    }

    @Override
    public void onDestroy() {
        stopped = true;
        running = false;
        handler.removeCallbacksAndMessages(null);
        closeSocket("service_destroyed");
        super.onDestroy();
    }

    /** Android 15+ dataSync 前台服务累计时长到上限时由系统回调：断开连接并结束服务。 */
    @Override
    public void onTimeout(int startId, int fgsType) {
        stopped = true;
        running = false;
        closeSocket("foreground_service_timeout");
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

    private Notification buildServiceNotification() {
        return new NotificationCompat.Builder(this, CHANNEL_SERVICE)
            .setSmallIcon(R.drawable.ic_stat_message)
            .setContentTitle("EiseChat")
            .setContentText("正在接收新消息")
            .setContentIntent(buildOpenAppIntent(null))
            .setOngoing(true)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .build();
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
        authenticated = false;
        final Runnable[] authTimeout = new Runnable[1];
        authTimeout[0] = () -> {
            if (!authenticated) {
                closeSocket("auth_timeout");
                scheduleReconnect();
            }
        };
        handler.postDelayed(authTimeout[0], 12_000L);

        Request request;
        try {
            request = new Request.Builder().url(wsUrl).build();
        } catch (IllegalArgumentException e) {
            closeSocket("invalid_url");
            return;
        }
        WebSocket socket = httpClient.newWebSocket(request, new WebSocketListener() {
            @Override
            public void onOpen(WebSocket socket, Response response) {
                handler.removeCallbacks(authTimeout[0]);
                sendAuthFrame(socket);
            }

            @Override
            public void onMessage(WebSocket socket, String text) {
                handler.removeCallbacks(authTimeout[0]);
                handleFrame(text);
            }

            @Override
            public void onFailure(WebSocket socket, Throwable t, @Nullable Response response) {
                handler.removeCallbacks(authTimeout[0]);
                webSocket = null;
                authenticated = false;
                scheduleReconnect();
            }

            @Override
            public void onClosed(WebSocket socket, int code, String reason) {
                handler.removeCallbacks(authTimeout[0]);
                webSocket = null;
                authenticated = false;
                if (!stopped) {
                    scheduleReconnect();
                }
            }
        });
        webSocket = socket;
    }

    private void sendAuthFrame(WebSocket socket) {
        JSONObject frame = new JSONObject();
        try {
            frame.put("type", "auth");
            frame.put("reqId", "native-auth-" + System.currentTimeMillis());
            frame.put("token", token);
            frame.put("deviceId", deviceId);
            frame.put("protocolVersion", 1);
        } catch (Exception ignored) {
            closeSocket("auth_frame_error");
            return;
        }
        socket.send(frame.toString());
    }

    private void sendPongFrame(String reqId) {
        WebSocket socket = webSocket;
        if (socket == null) {
            return;
        }
        JSONObject frame = new JSONObject();
        try {
            frame.put("type", "pong");
            if (reqId != null) {
                frame.put("reqId", reqId);
            }
        } catch (Exception ignored) {
            return;
        }
        socket.send(frame.toString());
    }

    private void handleFrame(String text) {
        JSONObject frame;
        try {
            frame = new JSONObject(text);
        } catch (Exception e) {
            return;
        }
        String type = frame.optString("type", "");
        switch (type) {
            case "hello":
                authenticated = true;
                reconnectAttempt = 0;
                fetchConversations();
                break;
            case "ping":
                sendPongFrame(frame.optString("reqId", null));
                break;
            case "message.new":
                handleIncomingMessage(frame.optJSONObject("message"));
                break;
            case "conv.updated":
                applyConversations(frame.optJSONArray("conversations"));
                break;
            case "kicked":
                // 账号被踢下线（超限/远程退出）：停止后台服务，前台 WebView 会收到同样的帧并处理登录态。
                stopped = true;
                running = false;
                closeSocket("kicked");
                stopForeground(STOP_FOREGROUND_REMOVE);
                stopSelf();
                break;
            default:
                break;
        }
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
        if (stopped) {
            return;
        }
        long delay = RECONNECT_DELAYS_MS[Math.min(reconnectAttempt, RECONNECT_DELAYS_MS.length - 1)];
        reconnectAttempt += 1;
        handler.removeCallbacks(reconnectRunnable);
        handler.postDelayed(reconnectRunnable, delay);
    }

    private void closeSocket(String reason) {
        WebSocket socket = webSocket;
        webSocket = null;
        authenticated = false;
        if (socket != null) {
            try {
                socket.close(1000, reason);
            } catch (Exception ignored) {
                // 关闭异常忽略。
            }
        }
    }
}
