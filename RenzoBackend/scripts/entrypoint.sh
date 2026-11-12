#!/bin/sh
set -e

PUID=${PUID:-99}
PGID=${PGID:-100}
USERNAME=renzo

# Resolve group name from PGID if it already exists
existing_group=$(getent group "$PGID" | cut -d: -f1)
if [ -z "$existing_group" ]; then
    echo "Creating group '$USERNAME' with GID $PGID"
    groupadd -g "$PGID" "$USERNAME"
    group_name="$USERNAME"
else
    echo "Group with GID $PGID already exists: $existing_group"
    group_name="$existing_group"
fi

# Resolve user name from PUID if it already exists
existing_user=$(getent passwd "$PUID" | cut -d: -f1)
if [ -z "$existing_user" ]; then
    echo "Creating user '$USERNAME' with UID $PUID"
    useradd -u "$PUID" -g "$PGID" -d /config --no-log-init -G audio,video "$USERNAME"
    user_name="$USERNAME"
else
    echo "User with UID $PUID already exists: $existing_user"
    user_name="$existing_user"
fi

# Make sure the app user can use /config.
# This used to be `chown -R` + `chmod -R 777` on every start: it rewrote every
# inode under /config (thumbs, extensions, logs - slow on a big install) and
# left the database (stored SMTP/SOCKS passwords included) and the JWT secret
# in appsettings.json readable and writable by every account on the host. All the app needs is to
# own its files, so:
#   - chown only what is not already PUID:PGID (a no-op on a normal restart; it
#     repairs files restored from a backup, or left by a run with other IDs).
#     -h changes a symlink itself rather than whatever it points at.
#   - make sure the owner can read/write every file and enter every directory,
#     and keep directories group-writable so other members of PGID (the host
#     user, another container) can still drop files in.
# Nothing is narrowed: an install that was already 777 keeps its modes, so a
# running install starts exactly as before. Fresh files follow UMASK below.
echo "Checking ownership and permissions on /config"
# A file that cannot be changed (a read-only mount inside /config) is reported
# rather than stopping the container; the app logs its own error if it matters.
find /config \( ! -user "$PUID" -o ! -group "$PGID" \) -exec chown -h "$PUID:$PGID" {} + \
    || echo "Warning: could not change the owner of some files in /config"
find /config \( -type d ! -perm -u=rwx,g=rwx \) -exec chmod u+rwx,g+rwx {} + \
    || echo "Warning: could not change the mode of some directories in /config"
find /config \( -type f ! -perm -u=rw \) -exec chmod u+rw {} + \
    || echo "Warning: could not change the mode of some files in /config"

# UMASK (optional, e.g. 002 for group-writable files). Inherited by the app and
# the OAuth proxy below through gosu/exec. Left alone when unset.
if [ -n "${UMASK:-}" ]; then
    if umask "$UMASK" 2>/dev/null; then
        echo "Using umask $UMASK"
    else
        echo "Warning: ignoring invalid UMASK '$UMASK'"
    fi
fi

# Detect architecture and add IKVM library path
ARCH=$(uname -m)
if [ "$ARCH" = "x86_64" ]; then
    IKVM_LIB_PATH="/app/ikvm/linux-x64/bin"
elif [ "$ARCH" = "aarch64" ]; then
    IKVM_LIB_PATH="/app/ikvm/linux-arm64/bin"
else
    echo "Warning: Unknown architecture $ARCH, IKVM libs may not be found"
    IKVM_LIB_PATH="/app/ikvm/linux-x64/bin"
fi

# Add IKVM library directories to LD_LIBRARY_PATH
export LD_LIBRARY_PATH="${IKVM_LIB_PATH}:${LD_LIBRARY_PATH}"

# Start the bundled OAuth proxy on loopback (container-hosted — replaces the old
# external auth service). It reads provider client credentials from env vars
# (ProviderCredentials__MyAnimeList__ClientId, __ClientSecret, __AniList__…, etc.)
# which the admin sets in docker-compose. Runs as the same unprivileged user.
if [ -f /app/oauthproxy/RenzoOAuthProxy.dll ]; then
    echo "Starting bundled Renzo OAuth proxy on http://127.0.0.1:5050"
    ASPNETCORE_URLS="http://127.0.0.1:5050" ASPNETCORE_ENVIRONMENT=Production \
        gosu "$user_name" dotnet /app/oauthproxy/RenzoOAuthProxy.dll &
else
    echo "Note: bundled OAuth proxy not found at /app/oauthproxy — scrobbler linking disabled"
fi

# Run the app as the correct user
exec gosu "$user_name" xvfb-run --auto-servernum /app/RenzoBackend
