#!/usr/bin/env bash
# Builds the signed release APK. Generates the signing keystore on first run.
# Usage: SIGNING_DIR=/export/Main/Renzo-Apps/signing ./build-release.sh
set -euo pipefail

cd "$(dirname "$0")"

export ANDROID_HOME="${ANDROID_HOME:-/opt/android-sdk}"
GRADLE="${GRADLE:-/opt/gradle-8.7/bin/gradle}"
SIGNING_DIR="${SIGNING_DIR:-/export/Main/Renzo-Apps/signing}"

mkdir -p "$SIGNING_DIR"
KEYSTORE="$SIGNING_DIR/renzo.keystore"
PWFILE="$SIGNING_DIR/keystore-password.txt"

if [ ! -f "$KEYSTORE" ]; then
    PW=$(head -c 24 /dev/urandom | base64 | tr -d '/+=' | head -c 24)
    printf '%s' "$PW" > "$PWFILE"
    chmod 600 "$PWFILE"
    keytool -genkeypair -keystore "$KEYSTORE" -alias renzo \
        -keyalg RSA -keysize 2048 -validity 10000 \
        -storepass "$PW" -keypass "$PW" \
        -dname "CN=Renzo, O=Renzo, C=US"
    echo "Generated new keystore at $KEYSTORE"
fi

PW=$(cat "$PWFILE")
cat > key.properties <<EOF
keystoreFile=$KEYSTORE
keystorePassword=$PW
keyAlias=renzo
keyPassword=$PW
EOF
chmod 600 key.properties

"$GRADLE" --no-daemon assembleRelease

APK=app/build/outputs/apk/release/app-release.apk
ls -la "$APK"
echo "Built: $APK"
