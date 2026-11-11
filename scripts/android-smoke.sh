#!/usr/bin/env bash
set -euo pipefail
apk="${1:?Pass the APK path}"
package=com.hamresan.app
adb wait-for-device
adb logcat -c
adb install -r "$apk"
adb shell monkey -p "$package" -c android.intent.category.LAUNCHER 1
sleep 15
if ! adb shell pidof "$package" | tr -d '\r' | grep -q '[0-9]'; then
  echo '::error::App process is not running after launch.'
  adb logcat -b crash -d -v brief > android-crash.log || true
  adb logcat -d -v brief | grep -E -i 'fatal exception|AndroidRuntime|hamresan|mono|assembl' | tail -150 >> android-crash.log || true
  tail -120 android-crash.log | while IFS= read -r line; do
    line=${line//'%'/'%25'}
    echo "::error::$line"
  done
  exit 1
fi
# The process being alive is not enough if the launcher failed to reach our activity.
if ! adb shell dumpsys activity activities | grep -iE 'mResumedActivity|topResumedActivity' | grep -q "$package"; then
  echo '::error::The app is not the resumed activity after launch.'
  adb shell dumpsys activity activities | grep -iE 'mResumedActivity|topResumedActivity' || true
  exit 1
fi
echo 'App is running and its activity is resumed.'
