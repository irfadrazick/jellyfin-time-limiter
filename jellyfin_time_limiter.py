#! /usr/bin/env python3

import urllib3
import json
import sys
import os
import tempfile
from datetime import datetime

JELLYFIN_MAX_WATCH_TIME_MINUTES = int(
    os.getenv("JELLYFIN_MAX_WATCH_TIME_MINUTES", "90")
)
USER_NAME = os.getenv("JELLYFIN_USER_NAME")
JELLYFIN_BASE_URL = os.getenv("JELLYFIN_BASE_URL", "http://localhost:8096")
JELLYFIN_TOKEN = os.getenv("JELLYFIN_TOKEN")

# Ticks in Jellyfin are 100-nanosecond units (10,000,000 per second).
TICKS_PER_SECOND = 10_000_000

# Validate required environment variables

if not JELLYFIN_TOKEN:
    print("Error: JELLYFIN_TOKEN environment variable is required", file=sys.stderr)
    sys.exit(1)

if not USER_NAME:
    print("Error: JELLYFIN_USER_NAME environment variable is required", file=sys.stderr)
    sys.exit(1)

# Initialize HTTP client
urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)
http = urllib3.PoolManager(cert_reqs="CERT_NONE")

# Get script directory and file paths
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
LOG_FILE = os.path.join(SCRIPT_DIR, "jellyfin_time_limiter.log")
STATE_FILE = os.path.join(SCRIPT_DIR, "jellyfin_time_limiter_state.json")

# Cache last log message to avoid repeated file reads
_last_log_message = None


# Read last log message from file (cached after first read)
def _get_last_log_message():
    global _last_log_message
    if _last_log_message is not None:
        return _last_log_message

    if os.path.exists(LOG_FILE):
        try:
            with open(LOG_FILE, "r", encoding="utf-8") as f:
                lines = f.readlines()
                if lines:
                    last_line = lines[-1].strip()
                    # Extract message part (everything after timestamp)
                    if "] " in last_line:
                        _last_log_message = last_line.split("] ", 1)[1]
        except (IOError, IndexError):
            # Failure to read the log file is non-fatal; fall back to no cached message.
            # The first log entry will be written without deduplication check.
            pass

    return _last_log_message


# Log with timestamp to file (only if message changed from last log)
def log(message):
    global _last_log_message
    timestamp = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    log_line = f"[{timestamp}] {message}\n"

    # Get last log message (cached)
    last_message = _get_last_log_message()

    # Only write if message is different from last log
    if last_message != message:
        try:
            with open(LOG_FILE, "a", encoding="utf-8") as f:
                f.write(log_line)
            # Update cache after successful write
            _last_log_message = message
        except IOError:
            # Fallback to stderr if file write fails
            print(log_line, file=sys.stderr, end="")


# Make HTTP request to Jellyfin API
def make_request(method, endpoint, headers=None, body=None):
    url = f"{JELLYFIN_BASE_URL}{endpoint}"

    # Default headers with authorization
    default_headers = {
        "Authorization": f'MediaBrowser Client="Python", Token="{JELLYFIN_TOKEN}"',
    }

    # Merge with any additional headers
    if headers:
        default_headers.update(headers)

    # Encode body if provided
    encoded_body = None
    if body:
        if isinstance(body, dict):
            encoded_body = json.dumps(body).encode("utf-8")
            if "Content-Type" not in default_headers:
                default_headers["Content-Type"] = "application/json"
        else:
            encoded_body = body

    return http.request(method, url, headers=default_headers, body=encoded_body)


# Parse JSON response with error handling
def parse_json_response(response, context="response", fail_permissive=False):
    """
    Parse JSON from HTTP response with error handling.
    """
    try:
        return json.loads(response.data.decode("utf-8"))
    except json.JSONDecodeError as e:
        log(f"Error: Failed to parse {context} JSON response: {e}")
        log(f"Raw response data: {response.data.decode('utf-8', errors='replace')}")
        if fail_permissive:
            return None
        sys.exit(1)


# Load persisted watch-time state, resetting it when the day rolls over.
# State shape:
#   {
#     "date": "YYYY-MM-DD",            # local day the totals belong to
#     "total_seconds": float,         # accumulated watch time for that day
#     "sessions": {                   # per-session anchors from the last poll
#       "<session_id>": {"item_id": str, "position_ticks": int, "paused": bool}
#     }
#   }
def load_state(today_date):
    default_state = {"date": today_date, "total_seconds": 0.0, "sessions": {}}

    if not os.path.exists(STATE_FILE):
        return default_state

    try:
        with open(STATE_FILE, "r", encoding="utf-8") as f:
            state = json.load(f)
    except (IOError, json.JSONDecodeError) as e:
        # Corrupt/unreadable state should not lock anyone out; start fresh.
        log(f"Warning: could not read state file ({e}); starting a new day")
        return default_state

    # Reset accumulated time once the local date changes (daily midnight reset).
    if state.get("date") != today_date:
        return default_state

    state.setdefault("total_seconds", 0.0)
    state.setdefault("sessions", {})
    return state


# Persist state atomically so a crash mid-write can't corrupt the running tally.
def save_state(state):
    try:
        fd, tmp_path = tempfile.mkstemp(dir=SCRIPT_DIR, prefix=".state-", suffix=".tmp")
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            json.dump(state, f)
        os.replace(tmp_path, STATE_FILE)
    except IOError as e:
        log(f"Warning: could not write state file ({e}); today's tally may not persist")


# Find target user ID (needed for the policy endpoints)
response = make_request("GET", "/Users")

if response.status != 200:
    log(f"Error fetching users: {response.status}")
    sys.exit(1)

target_user_id = None

users = parse_json_response(response, context="users")

for user in users:
    if user["Name"] == USER_NAME:
        target_user_id = user["Id"]
        break

if not target_user_id:
    log(f"Error: User '{USER_NAME}' not found")
    sys.exit(1)

# Accumulate today's watch time from live sessions (no plugin required).
# We compare the playhead position between cron runs and add how far it moved,
# capped by the wall-clock gap so seeks/fast-forwards can't inflate the tally.
today_date = datetime.now().strftime("%Y-%m-%d")
now_epoch = datetime.now().timestamp()

state = load_state(today_date)
prev_sessions = state.get("sessions", {})
prev_epoch = state.get("last_epoch", now_epoch)
wall_gap_seconds = max(0.0, now_epoch - prev_epoch)

response = make_request("GET", "/Sessions")

if response.status != 200:
    # Live session data is unavailable this run. Don't reset anchors and don't
    # accumulate; just evaluate against whatever total we already have. This is
    # fail-permissive for transient errors: temporary API hiccups won't wrongly
    # add or lose watch time, and the next run retries.
    log(f"Warning: could not fetch sessions: {response.status}")
    new_sessions = prev_sessions  # preserve anchors for the next successful run
else:
    sessions = parse_json_response(response, context="sessions", fail_permissive=True)
    if sessions is None:
        new_sessions = prev_sessions
    else:
        new_sessions = {}
        for session in sessions:
            # Only the monitored user, and only sessions actively playing an item.
            if session.get("UserId") != target_user_id:
                continue
            now_playing = session.get("NowPlayingItem")
            play_state = session.get("PlayState") or {}
            position_ticks = play_state.get("PositionTicks")
            if not now_playing or position_ticks is None:
                continue

            session_id = session.get("Id")
            item_id = now_playing.get("Id")
            paused = bool(play_state.get("IsPaused", False))

            prev = prev_sessions.get(session_id)
            # Only count an interval where the same item was playing and unpaused
            # at both ends, so paused/idle time contributes nothing.
            if (
                prev
                and prev.get("item_id") == item_id
                and not paused
                and not prev.get("paused", False)
            ):
                position_delta = (position_ticks - prev.get("position_ticks", 0)) / TICKS_PER_SECOND
                # Ignore rewinds (negative) and cap forward jumps at real elapsed time.
                watched = max(0.0, min(position_delta, wall_gap_seconds))
                state["total_seconds"] += watched

            new_sessions[session_id] = {
                "item_id": item_id,
                "position_ticks": position_ticks,
                "paused": paused,
            }

state["date"] = today_date
state["last_epoch"] = now_epoch
state["sessions"] = new_sessions
save_state(state)

total_watch_time_minutes = state["total_seconds"] / 60
ENABLE_ACCESS = total_watch_time_minutes < JELLYFIN_MAX_WATCH_TIME_MINUTES

# Get current user policy to check if update is needed
user_endpoint = f"/Users/{target_user_id}"
response = make_request("GET", user_endpoint)

if response.status != 200:
    log(f"Error fetching user: {response.status}")
    sys.exit(1)

user_data = parse_json_response(response, context="user")
current_policy = user_data.get("Policy", {})

# Check if policy already matches desired state
current_enable_all = current_policy.get("EnableAllFolders", True)
current_enabled_folders = current_policy.get("EnabledFolders", [])

# Determine if update is needed
needs_update = False
if ENABLE_ACCESS:
    # Want to enable: check if currently disabled
    if not current_enable_all:
        needs_update = True
else:
    # Want to disable: check if currently enabled
    if current_enable_all or current_enabled_folders:
        needs_update = True

# Only update if needed
if needs_update:
    policy = {
        "AuthenticationProviderId": "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider",
        "PasswordResetProviderId": "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider",
        "EnableAllFolders": ENABLE_ACCESS,  # True = enable all folders, False = disable all folders
    }

    # If disabling access, also set EnabledFolders to empty array
    if not ENABLE_ACCESS:
        policy["EnabledFolders"] = []

    # Update the policy
    policy_endpoint = f"/Users/{target_user_id}/Policy"
    response = make_request("POST", policy_endpoint, body=policy)

    if response.status != 204 and response.status != 200:
        log(f"Error updating policy: {response.status}")
        log(f"Response: {response.data.decode('utf-8')}")
        sys.exit(1)

# Single line log with all information
watch_time_str = f"{total_watch_time_minutes:.1f}"
action = "enabled" if ENABLE_ACCESS else "disabled"
change_status = "changed" if needs_update else "unchanged"
log(
    f"User '{USER_NAME}': {watch_time_str} min / {JELLYFIN_MAX_WATCH_TIME_MINUTES} min - Access {action} ({change_status})"
)
