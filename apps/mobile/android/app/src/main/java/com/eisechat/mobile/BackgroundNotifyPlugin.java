package com.eisechat.mobile;

import android.Manifest;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.content.Context;
import android.content.pm.PackageManager;
import android.os.Build;

import androidx.core.app.NotificationManagerCompat;
import androidx.core.content.ContextCompat;

import com.getcapacitor.JSObject;
import com.getcapacitor.PermissionState;
import com.getcapacitor.Plugin;
import com.getcapacitor.PluginCall;
import com.getcapacitor.PluginMethod;
import com.getcapacitor.annotation.CapacitorPlugin;
import com.getcapacitor.annotation.Permission;

import org.json.JSONArray;
import org.json.JSONObject;

/**
 * 后台消息提醒的 Capacitor 插件：启动/停止前台服务、同步前台状态与当前会话、回传通知点击。
 * Web 侧入口见 apps/web/src/core/backgroundNotifications.ts。
 */
@CapacitorPlugin(
    name = "BackgroundNotify",
    permissions = {
        @Permission(alias = "notifications", strings = { Manifest.permission.POST_NOTIFICATIONS })
    }
)
public class BackgroundNotifyPlugin extends Plugin {
    @Override
    public void load() {
        ChatSocketService.installLifecycleCallbacks(getContext());
        ChatSocketService.setClickReporter((conversationId, userId, serverUrl) -> {
            JSObject data = new JSObject();
            data.put("conversationId", conversationId);
            if (userId != null && !userId.trim().isEmpty()) {
                data.put("userId", userId);
            }
            if (serverUrl != null && !serverUrl.trim().isEmpty()) {
                data.put("serverUrl", serverUrl);
            }
            notifyListeners("notificationClick", data);
        });
        ChatSocketService.setStateReporter((running, connected, stopReason) -> {
            JSObject data = new JSObject();
            data.put("running", running);
            data.put("connected", connected);
            if (stopReason != null) {
                data.put("stopReason", stopReason);
            }
            notifyListeners("stateChange", data);
        });
    }

    @PluginMethod
    public void start(PluginCall call) {
        String wsUrl = call.getString("wsUrl");
        String apiBase = call.getString("apiBase");
        String token = call.getString("token");
        String deviceId = call.getString("deviceId");
        String userId = call.getString("userId");
        if (wsUrl == null || apiBase == null || token == null || deviceId == null || userId == null) {
            call.reject("缺少连接参数");
            return;
        }
        if (needsNotificationPermission()) {
            requestPermissionForAlias("notifications", call, "notificationsPermissionCallback");
            return;
        }
        Context context = getContext();
        if (context != null) {
            ChatSocketService.ensureChannels(context);
        }
        String blocked = blockedNotificationReason();
        if (blocked != null) {
            JSObject result = new JSObject();
            result.put("started", false);
            result.put("permission", true);
            result.put("reason", blocked);
            call.resolve(result);
            return;
        }
        startService(call, wsUrl, apiBase, token, deviceId, userId, Boolean.TRUE.equals(call.getBoolean("userInitiated", false)));
    }

    @com.getcapacitor.annotation.PermissionCallback
    private void notificationsPermissionCallback(PluginCall call) {
        if (needsNotificationPermission()) {
            JSObject result = new JSObject();
            result.put("started", false);
            result.put("permission", false);
            call.resolve(result);
            return;
        }
        String wsUrl = call.getString("wsUrl");
        String apiBase = call.getString("apiBase");
        String token = call.getString("token");
        String deviceId = call.getString("deviceId");
        String userId = call.getString("userId");
        if (wsUrl == null || apiBase == null || token == null || deviceId == null || userId == null) {
            call.reject("缺少连接参数");
            return;
        }
        String blocked = blockedNotificationReason();
        if (blocked != null) {
            JSObject result = new JSObject();
            result.put("started", false);
            result.put("permission", true);
            result.put("reason", blocked);
            call.resolve(result);
            return;
        }
        startService(call, wsUrl, apiBase, token, deviceId, userId, Boolean.TRUE.equals(call.getBoolean("userInitiated", false)));
    }

    private void startService(
        PluginCall call,
        String wsUrl,
        String apiBase,
        String token,
        String deviceId,
        String userId,
        boolean userInitiated
    ) {
        Context context = getContext();
        if (context == null) {
            call.reject("应用上下文不可用");
            return;
        }
        ChatSocketService.start(context, wsUrl, apiBase, token, deviceId, userId, userInitiated);
        JSObject result = new JSObject();
        result.put("started", true);
        result.put("permission", true);
        call.resolve(result);
    }

    @PluginMethod
    public void stop(PluginCall call) {
        Context context = getContext();
        if (context != null) {
            ChatSocketService.stop(context);
        }
        call.resolve();
    }

    @PluginMethod
    public void isRunning(PluginCall call) {
        JSObject result = new JSObject();
        result.put("running", ChatSocketService.isRunning());
        result.put("connected", ChatSocketService.isConnected());
        String stopReason = ChatSocketService.getLastStopReason();
        if (stopReason != null) {
            result.put("stopReason", stopReason);
        }
        call.resolve(result);
    }

    @PluginMethod
    public void setState(PluginCall call) {
        // 前后台由 Android 原生生命周期决定；Web 只更新当前会话，避免延迟调用把后台误标为前台。
        if (call.hasOption("foreground")) {
            Boolean foreground = call.getBoolean("foreground");
            if (foreground != null) {
                ChatSocketService.setAppForeground(foreground);
            }
        }
        ChatSocketService.setForegroundConversation(call.getString("conversationId"));
        call.resolve();
    }

    /** Web 端同步已确认的会话标题、成员与免打扰状态（本人修改免打扰后调用）。 */
    @PluginMethod
    public void updateConversations(PluginCall call) {
        JSONArray conversations = call.getArray("conversations");
        if (conversations != null) {
            ChatSocketService.updateConversationsJson(conversations);
        }
        call.resolve();
    }

    @PluginMethod
    public void checkAvailability(PluginCall call) {        Context context = getContext();
        if (context != null) {
            ChatSocketService.ensureChannels(context);
        }
        String blocked = blockedNotificationReason();
        JSObject result = new JSObject();
        result.put("available", blocked == null);
        if (blocked != null) {
            result.put("reason", blocked);
        }
        call.resolve(result);
    }

    @PluginMethod
    public void consumePendingClick(PluginCall call) {
        JSONObject pending = ChatSocketService.consumePendingClick();
        if (pending == null) {
            call.resolve(new JSObject());
            return;
        }
        JSObject result = new JSObject();
        result.put("conversationId", pending.optString("conversationId", ""));
        if (pending.has("userId")) {
            result.put("userId", pending.optString("userId", ""));
        }
        if (pending.has("serverUrl")) {
            result.put("serverUrl", pending.optString("serverUrl", ""));
        }
        call.resolve(result);
    }

    /** 系统通知开关或消息渠道被关闭时返回原因，避免「开关为开但收不到通知」。 */
    private String blockedNotificationReason() {
        Context context = getContext();
        if (context == null) {
            return "context_unavailable";
        }
        if (!NotificationManagerCompat.from(context).areNotificationsEnabled()) {
            return "notifications_disabled";
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationManager manager = context.getSystemService(NotificationManager.class);
            if (manager != null) {
                NotificationChannel channel =
                    manager.getNotificationChannel(ChatSocketService.CHANNEL_MESSAGES);
                if (channel != null && channel.getImportance() == NotificationManager.IMPORTANCE_NONE) {
                    return "channel_disabled";
                }
            }
        }
        return null;
    }

    private boolean needsNotificationPermission() {        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) {
            return false;
        }
        Context context = getContext();
        if (context == null) {
            return true;
        }
        if (ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS)
            == PackageManager.PERMISSION_GRANTED) {
            return false;
        }
        return getPermissionState("notifications") != PermissionState.GRANTED;
    }
}
