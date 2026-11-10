package com.erisechat.mobile;

import android.content.Intent;
import android.os.Bundle;

import com.getcapacitor.BridgeActivity;

public class MainActivity extends BridgeActivity {
    @Override
    public void onCreate(Bundle savedInstanceState) {
        // F-29：本地 APK 更新插件（下载 + sha256 校验 + 调起系统安装器）。
        registerPlugin(ApkUpdaterPlugin.class);
        // 后台消息提醒插件（前台服务 + 系统通知）。
        registerPlugin(BackgroundNotifyPlugin.class);
        super.onCreate(savedInstanceState);
        // 冷启动时若由通知带起，把会话 ID 交给 Web 侧跳转。
        ChatSocketService.handleLaunchIntent(getIntent());
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        ChatSocketService.handleLaunchIntent(intent);
    }
}
