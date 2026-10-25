#!/usr/bin/env bash
# 构建 Android release APK（M5-03）：Web 构建 → cap sync → Gradle assembleRelease → 复制到 data/downloads。
# 用法：bash infra/build-apk.sh [版本号]（默认 1.0.0；版本号需与 android/app/build.gradle 的 versionName 一致）
# 前置：JDK 17+（推荐 21）、Android SDK（platforms;android-36 + build-tools;36.0.0）、
#       infra/keys/keystore.properties + eisechat-release.jks（不入库，见 docs/mobile.md）。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${1:-1.0.0}"
APK_NAME="EiseChat-${VERSION}.apk"

# JDK / Android SDK：优先沿用环境变量，未设置时用本机默认路径（不写入系统变量）。
export JAVA_HOME="${JAVA_HOME:-D:/softWare/jdk21}"
export PATH="${JAVA_HOME}/bin:${PATH}"

if [ ! -x "${JAVA_HOME}/bin/java" ] && [ ! -x "${JAVA_HOME}/bin/java.exe" ]; then
  echo "ERROR: 找不到 JDK，请设置 JAVA_HOME（例如 D:/softWare/jdk21）" >&2
  exit 1
fi
# Android SDK 路径以 apps/mobile/android/local.properties 为准（该文件不入库，见 docs/mobile.md）。
LOCAL_PROPERTIES="${ROOT}/apps/mobile/android/local.properties"
if [ ! -f "${LOCAL_PROPERTIES}" ]; then
  echo "ERROR: 缺少 ${LOCAL_PROPERTIES}；请写入 sdk.dir=<Android SDK 绝对路径>（参考 docs/mobile.md）" >&2
  exit 1
fi

echo "== [1/4] 构建 Web 前端 =="
cd "${ROOT}/apps/web"
npm run build

echo "== [2/4] 同步到 Capacitor Android 工程 =="
cd "${ROOT}/apps/mobile"
npx cap sync android

echo "== [3/4] Gradle assembleRelease =="
cd "${ROOT}/apps/mobile/android"
./gradlew assembleRelease

echo "== [4/4] 复制安装包 =="
SOURCE_APK="${ROOT}/apps/mobile/android/app/build/outputs/apk/release/app-release.apk"
if [ ! -f "${SOURCE_APK}" ]; then
  echo "ERROR: 未找到 APK 产物：${SOURCE_APK}" >&2
  exit 1
fi
mkdir -p "${ROOT}/data/downloads"
cp -f "${SOURCE_APK}" "${ROOT}/data/downloads/${APK_NAME}"
echo "APK: ${ROOT}/data/downloads/${APK_NAME}"
ls -l "${ROOT}/data/downloads/${APK_NAME}"
if command -v sha256sum >/dev/null 2>&1; then
  sha256sum "${ROOT}/data/downloads/${APK_NAME}"
fi
