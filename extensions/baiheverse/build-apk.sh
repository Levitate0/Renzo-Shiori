#!/usr/bin/env bash
# Builds a sideloadable Baiheverse APK: Gradle compiles the Kotlin against the
# sidecar jar, d8 turns it into dex, aapt2 packs the manifest. Install it under
# Sources › Extensions › Sideload (or POST /api/extensions/sideload).
set -euo pipefail
cd "$(dirname "$0")"
SDK=${ANDROID_SDK:-/opt/zurg-stack/renzo-clients/android/sdk}
BT=$SDK/build-tools/36.0.0
PLATFORM=$SDK/platforms/android-34/android.jar
export JAVA_HOME=${JAVA_HOME:-/opt/jdk21} PATH=/opt/jdk21/bin:$PATH
GRADLEW=../../Mihon.ExtensionsBridge.Net/Android.Compatibility.Layer/gradlew

# The site's public ("anon") key, which it ships in its scripts to every
# browser. Fetched from those scripts when public-key.txt is missing; delete
# the file to pick up a new one if image downloads start failing with 401/403.
if [ ! -s public-key.txt ]; then
  UA="Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36"
  for js in $(curl -sSL -A "$UA" https://baiheverse.com/en | grep -oE '/_next/static/chunks/[^"]+\.js' | sort -u); do
    k=$(curl -sS -A "$UA" "https://baiheverse.com$js" | grep -oE 'eyJhbGci[A-Za-z0-9_.-]{40,}' | head -1 || true)
    [ -n "$k" ] && { printf '%s' "$k" > public-key.txt; break; }
  done
fi
KEY=${BAIHEVERSE_PUBLIC_KEY:-$(cat public-key.txt)}
OUT=build/apk
rm -rf "$OUT" && mkdir -p "$OUT/gen"
SRC=src/main/kotlin/eu/kanade/tachiyomi/extension/en/baiheverse/Baiheverse.kt
cp "$SRC" "$OUT/Baiheverse.kt.orig"
trap 'cp "$OUT/Baiheverse.kt.orig" "$SRC"' EXIT
sed -i "s|@PUBLIC_KEY@|$KEY|" "$SRC"
"$GRADLEW" -p . jar --no-daemon -q
cp "$OUT/Baiheverse.kt.orig" "$SRC"

# d8 can't read Kotlin 2.3's metadata and says so for every class; the
# metadata is only carried along, so those warnings are dropped.
{ "$BT/d8" --release --min-api 26 --lib "$PLATFORM" \
  --classpath ../../RenzoBackend/sidecar/AndroidCompat-1.0-all.jar \
  --output "$OUT/gen" build/libs/baiheverse.jar; } 2>&1 | { grep -vE 'kotlin metadata|Kotlin metadata|^\s+at |^com\.android\.tools\.r8' || true; }
"$BT/aapt2" link -o "$OUT/unsigned.apk" --manifest AndroidManifest.xml -I "$PLATFORM" \
  --min-sdk-version 26 --target-sdk-version 34
(cd "$OUT/gen" && zip -qj ../unsigned.apk classes.dex)
"$BT/zipalign" -f 4 "$OUT/unsigned.apk" "$OUT/aligned.apk"
KS=build/debug.keystore
[ -f "$KS" ] || keytool -genkeypair -keystore "$KS" -storepass android -keypass android -alias ext \
  -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=Renzo Shiori local extension" >/dev/null 2>&1
"$BT/apksigner" sign --ks "$KS" --ks-pass pass:android --out build/tachiyomi-en.baiheverse-v1.4.1.apk "$OUT/aligned.apk"
echo "built build/tachiyomi-en.baiheverse-v1.4.1.apk"
