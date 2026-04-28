#!/bin/sh
set -e

CONFIG_DIR="${ORASBACKUP_CONFIG_DIR:-/config}"
PROFILE="${ORASBACKUP_PROFILE:-default}"
PROFILE_FILE="$CONFIG_DIR/profiles/${PROFILE}.json"

# If no profile exists, auto-generate one from environment variables
if [ ! -f "$PROFILE_FILE" ]; then
  mkdir -p "$CONFIG_DIR/profiles"

  # Escape values for safe JSON embedding (prevent injection via env vars)
  json_escape() { printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g; s/`/\\`/g'; }

  SOURCE="${ORASBACKUP_SOURCE:-/data}"
  REGISTRY=$(json_escape "${ORASBACKUP_REGISTRY:?ORASBACKUP_REGISTRY is required}")
  INTERVAL="${ORASBACKUP_INTERVAL:-60}"  # minutes
  ENCRYPT="${ORASBACKUP_ENCRYPT:-true}"
  EXCLUDE="${ORASBACKUP_EXCLUDE:-**/.git,**/node_modules,**/bin,**/obj}"
  MAX_BACKUPS="${ORASBACKUP_MAX_BACKUPS:-50}"

  # Validate numeric/boolean values to prevent JSON injection
  case "$INTERVAL" in ''|*[!0-9]*) echo "ERROR: ORASBACKUP_INTERVAL must be numeric" >&2; exit 1;; esac
  case "$MAX_BACKUPS" in ''|*[!0-9]*) echo "ERROR: ORASBACKUP_MAX_BACKUPS must be numeric" >&2; exit 1;; esac
  case "$ENCRYPT" in true|false) ;; *) echo "ERROR: ORASBACKUP_ENCRYPT must be true or false" >&2; exit 1;; esac
  PROFILE_ESCAPED=$(json_escape "$PROFILE")

  # Convert comma-separated source paths and excludes to JSON arrays (with escaping)
  SOURCE_JSON=$(printf '%s' "$SOURCE" | tr ',' '\n' | while IFS= read -r p; do printf '"%s",' "$(json_escape "$p")"; done | sed 's/,$//')
  EXCLUDE_JSON=$(printf '%s' "$EXCLUDE" | tr ',' '\n' | while IFS= read -r p; do printf '"%s",' "$(json_escape "$p")"; done | sed 's/,$//')

  cat > "$PROFILE_FILE" <<EOF
{
  "name": "$PROFILE_ESCAPED",
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
    "maxBackups": $MAX_BACKUPS
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

# Registry auth is handled natively by HttpOrasClient via ORAS_PAT or ORAS_USERNAME/ORAS_PASSWORD env vars

# Fail fast if no encryption credentials provided (avoids hanging on Console.ReadKey in container)
if [ -z "$ORASBACKUP_PASSWORD" ] && [ -z "$ORASBACKUP_KEY_FILE" ]; then
  ENCRYPT="${ORASBACKUP_ENCRYPT:-true}"
  if [ -f "$PROFILE_FILE" ]; then
    # Check encryption.enabled: look for "enabled" within 2 lines after "encryption"
    ENCRYPT=$(sed -n '/"encryption"/,/}/p' "$PROFILE_FILE" 2>/dev/null | grep -o '"enabled"[[:space:]]*:[[:space:]]*[a-z]*' | grep -o 'true\|false' || echo "$ENCRYPT")
  fi
  if [ "$ENCRYPT" = "true" ]; then
    echo "ERROR: Encryption is enabled but neither ORASBACKUP_PASSWORD nor ORASBACKUP_KEY_FILE is set." >&2
    echo "Set one of these environment variables or disable encryption in the profile." >&2
    exit 1
  fi
fi

# Default command: daemon mode. Password passed via env var, not CLI arg (avoids ps exposure).
if [ $# -eq 0 ]; then
  exec dotnet /app/orasbackup.dll daemon \
    --profile "$PROFILE" \
    ${ORASBACKUP_KEY_FILE:+--key-file "$ORASBACKUP_KEY_FILE"} \
    --interval "${ORASBACKUP_INTERVAL:-60}"
else
  exec dotnet /app/orasbackup.dll "$@"
fi
