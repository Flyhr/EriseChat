package com.eisechat.mobile;

import android.content.Intent;
import android.net.Uri;

import androidx.core.content.FileProvider;

import com.getcapacitor.JSObject;
import com.getcapacitor.Plugin;
import com.getcapacitor.PluginCall;
import com.getcapacitor.PluginMethod;
import com.getcapacitor.annotation.CapacitorPlugin;

import java.io.BufferedInputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.security.MessageDigest;
import java.util.Locale;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * F-29：APK 更新插件。下载安装包到 cacheDir，校验大小与 sha256 后交给系统安装器。
 * 不引入第三方依赖（HttpURLConnection + MessageDigest），与 Web 端 core/updateAndroid.ts 配合。
 */
@CapacitorPlugin(name = "ApkUpdater")
public class ApkUpdaterPlugin extends Plugin {

    private static final int CONNECT_TIMEOUT_MS = 15_000;
    private static final int READ_TIMEOUT_MS = 30_000;
    private static final long MAX_APK_BYTES = 512L * 1024 * 1024;

    private final ExecutorService executor = Executors.newSingleThreadExecutor();

    @PluginMethod
    public void downloadAndInstall(PluginCall call) {
        String url = call.getString("url");
        String sha256 = call.getString("sha256");
        Long size = call.getLong("size");
        if (url == null || url.trim().isEmpty()) {
            call.reject("更新地址为空", "INVALID_URL");
            return;
        }
        if (sha256 == null || sha256.trim().isEmpty()) {
            call.reject("缺少安装包校验值", "INVALID_SHA256");
            return;
        }
        final String downloadUrl = url.trim();
        final String expectedSha256 = sha256.trim().toLowerCase(Locale.ROOT);
        final long expectedSize = size == null ? 0L : size;
        executor.execute(() -> runDownload(call, downloadUrl, expectedSha256, expectedSize));
    }

    private void runDownload(PluginCall call, String downloadUrl, String expectedSha256, long expectedSize) {
        File apkFile = null;
        HttpURLConnection connection = null;
        try {
            URL parsed = new URL(downloadUrl);
            String scheme = parsed.getProtocol();
            if (!"http".equalsIgnoreCase(scheme) && !"https".equalsIgnoreCase(scheme)) {
                throw new IllegalArgumentException("更新地址必须是 http(s)");
            }
            File directory = new File(getContext().getCacheDir(), "updates");
            if (!directory.exists() && !directory.mkdirs()) {
                throw new IllegalStateException("无法创建更新缓存目录");
            }
            apkFile = new File(directory, "update.apk");
            connection = (HttpURLConnection) parsed.openConnection();
            connection.setConnectTimeout(CONNECT_TIMEOUT_MS);
            connection.setReadTimeout(READ_TIMEOUT_MS);
            connection.setInstanceFollowRedirects(true);
            connection.connect();
            int status = connection.getResponseCode();
            if (status < 200 || status >= 300) {
                throw new IllegalStateException("下载失败：服务器返回 " + status);
            }
            long total = expectedSize > 0 ? expectedSize : connection.getContentLengthLong();
            MessageDigest digest = MessageDigest.getInstance("SHA-256");
            byte[] buffer = new byte[64 * 1024];
            long downloaded = 0;
            long lastPercent = -1;
            try (InputStream input = new BufferedInputStream(connection.getInputStream());
                 FileOutputStream output = new FileOutputStream(apkFile, false)) {
                int read;
                while ((read = input.read(buffer)) != -1) {
                    downloaded += read;
                    if (downloaded > MAX_APK_BYTES) {
                        throw new IllegalStateException("安装包超过大小上限");
                    }
                    output.write(buffer, 0, read);
                    digest.update(buffer, 0, read);
                    if (total > 0) {
                        long percent = Math.min(100, downloaded * 100 / total);
                        if (percent != lastPercent) {
                            lastPercent = percent;
                            notifyProgress(downloaded, total, percent);
                        }
                    }
                }
            }
            if (expectedSize > 0 && downloaded != expectedSize) {
                throw new IllegalStateException("安装包大小不匹配，请重试");
            }
            String actualSha256 = toHex(digest.digest());
            if (!actualSha256.equals(expectedSha256)) {
                throw new IllegalStateException("安装包校验失败（sha256 不一致），请重试");
            }
            startInstaller(apkFile);
            call.resolve();
        } catch (Exception error) {
            if (apkFile != null && apkFile.exists() && !apkFile.delete()) {
                // 删除失败不影响错误返回。
            }
            call.reject(error.getMessage() == null ? "下载更新失败" : error.getMessage(), "UPDATE_FAILED", error);
        } finally {
            if (connection != null) {
                connection.disconnect();
            }
        }
    }

    private void notifyProgress(long downloaded, long total, long percent) {
        JSObject progress = new JSObject();
        progress.put("downloaded", downloaded);
        progress.put("total", total);
        progress.put("percent", percent);
        notifyListeners("progress", progress);
    }

    private void startInstaller(File apkFile) {
        // authorities 与 AndroidManifest 的 ${applicationId}.fileprovider 保持一致。
        Uri apkUri = FileProvider.getUriForFile(getContext(), getContext().getPackageName() + ".fileprovider", apkFile);
        Intent intent = new Intent(Intent.ACTION_VIEW);
        intent.setDataAndType(apkUri, "application/vnd.android.package-archive");
        intent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        getActivity().runOnUiThread(() -> {
            try {
                getActivity().startActivity(intent);
            } catch (Exception error) {
                notifyListeners("installerError", null);
            }
        });
    }

    private static String toHex(byte[] bytes) {
        StringBuilder builder = new StringBuilder(bytes.length * 2);
        for (byte value : bytes) {
            builder.append(String.format(Locale.ROOT, "%02x", value));
        }
        return builder.toString();
    }
}
