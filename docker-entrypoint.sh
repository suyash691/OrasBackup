#!/bin/sh
set -e

CONFIG_DIR="${ORASBACKUP_CONFIG_DIR:-/config}"
PROFILE="${ORASBACKUP_PROFILE:-default}"
PROFILE_FILE="$CONFIG_DIR/profiles/${PROFILE}.json"

# If no profile exists, auto-generate one from environment variables
if [ ! -f "$PROFILE_FILE" ]; then
  mkdir -p "$CONFIG_DIR/profiles"

  SOURCE="${ORASBACKUP_SOURCE:-/data}"
  REGISTRY="${ORASBACKUP_REGISTRY:?ORASBACKUP_REGISTRY is required}"
  INTERVAL="${ORASBACKUP_INTERVAL:-60}"
  ENCRYPT="${ORASBACKUP_ENCRYPT:-true}"
  EXCLUDE="${ORASBACKUP_EXCLUDE:-**/.git,**/node_modules,**/bin,**/obj}"
  MAX_BACKUPS="${ORASBACKUP_MAX_BACKUPS:-50}"
  COMPACT_AFTER="${ORASBACKUP_COMPACT_AFTER:-10}"

  # Convert comma-separated source paths and excludes to JSON arrays
  SOURCE_JSON=$(echo "$SOURCE" | tr ',' '\n' | sed 's/.*/"&"/' | paste -sd, -)
  EXCLUDE_JSON=$(echo "$EXCLUDE" | tr ',' '\n' | sed 's/.*/"&"/' | paste -sd, -)

  cat > "$PROFILE_FILE" <<EOF
{
  "name": "$PROFILE",
  "sourcePaths": [$SOURCE_JSON],
  "excludePatterns": [$EXCLUDE_JSON],
  "registry": "$REGISTRY",
  "schedule": {
    "enabled": true,
    "intervalMinutes": $INTERVAL
  },
  "encryption": {
    "enabled": $ENCRYPT,
    "pbkdf2Iterations": 600000
  },
  "retention": {
    "maxBackups": $MAX_BACKUPS,
    "compactAfter": $COMPACT_AFTER
  }
}
EOF
  echo "Generated profile: $PROFILE_FILE"
fi

# Point orasbackup state/profiles at the config volume
export HOME="$CONFIG_DIR"
mkdir -p "$CONFIG_DIR/.orasbackup/profiles" "$CONFIG_DIR/.orasbackup/state"

# Symlink so the CLI finds profiles at ~/.orasbackup/profiles
if [ ! -L "$CONFIG_DIR/.orasbackup/profiles/${PROFILE}.json" ] && [ -f "$PROFILE_FILE" ]; then
  ln -sf "$PROFILE_FILE" "$CONFIG_DIR/.orasbackup/profiles/${PROFILE}.json"
fi

# Registry login if credentials provided
if [ -n "$ORAS_PAT" ]; then
  # PAT auth (GitHub Container Registry, GitLab, etc.)
  REGISTRY_HOST=$(echo "$ORASBACKUP_REGISTRY" | cut -d'/' -f1)
  echo "$ORAS_PAT" | oras login "$REGISTRY_HOST" -u "${ORAS_USERNAME:-pat}" --password-stdin ${ORAS_INSECURE:+--insecure}
elif [ -n "$ORAS_USERNAME" ] && [ -n "$ORAS_PASSWORD" ]; then
  REGISTRY_HOST=$(echo "$ORASBACKUP_REGISTRY" | cut -d'/' -f1)
  echo "$ORAS_PASSWORD" | oras login "$REGISTRY_HOST" -u "$ORAS_USERNAME" --password-stdin ${ORAS_INSECURE:+--insecure}
fi

# Default command: daemon mode. Pass through any extra args.
if [ $# -eq 0 ]; then
  exec dotnet /app/orasbackup.dll daemon \
    --profile "$PROFILE" \
    ${ORASBACKUP_PASSWORD:+--password "$ORASBACKUP_PASSWORD"} \
    ${ORASBACKUP_KEY_FILE:+--key-file "$ORASBACKUP_KEY_FILE"} \
    --interval "${ORASBACKUP_INTERVAL:-60}"
else
  exec dotnet /app/orasbackup.dll "$@"
fi
