package com.eisechat.mobile;

import android.Manifest;
import android.content.Context;
import android.content.pm.PackageManager;
import android.os.Build;

import androidx.core.content.ContextCompat;

import com.getcapacitor.JSObject;
import com.getcapacitor.PermissionState;
import com.getcapacitor.Plugin;
import com.getcapacitor.PluginCall;
import com.getcapacitor.PluginMethod;
import com.getcapacitor.annotation.CapacitorPlugin;
import com.getcapacitor.annotation.Permission;

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
        ChatSocketService.setClickReporter(conversationId -> {
            JSObject data = new JSObject();
            data.put("conversationId", conversationId);
            notifyListeners("notificationClick", data);
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
        startService(call, wsUrl, apiBase, token, deviceId, userId);
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
        startService(call, wsUrl, apiBase, token, deviceId, userId);
    }

    private void startService(PluginCall call, String wsUrl, String apiBase, String token, String deviceId, String userId) {
        Context context = getContext();
        if (context == null) {
            call.reject("应用上下文不可用");
            return;
        }
        ChatSocketService.start(context, wsUrl, apiBase, token, deviceId, userId);
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
        call.resolve(result);
    }

    @PluginMethod
    public void setState(PluginCall call) {
        Boolean foreground = call.getBoolean("foreground", true);
        ChatSocketService.setAppForeground(foreground == null || foreground);
        ChatSocketService.setForegroundConversation(call.getString("conversationId"));
        call.resolve();
    }

    @PluginMethod
    public void consumePendingClick(PluginCall call) {
        JSObject result = new JSObject();
        String conversationId = ChatSocketService.consumePendingClick();
        if (conversationId != null) {
            result.put("conversationId", conversationId);
        }
        call.resolve(result);
    }

    private boolean needsNotificationPermission() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) {
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
