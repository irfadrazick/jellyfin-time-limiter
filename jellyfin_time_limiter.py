#! /usr/bin/env python3

import urllib3
import json
import sys
from datetime import datetime
import os

JELLYFIN_MAX_WATCH_TIME_MINUTES = int(
    os.getenv("JELLYFIN_MAX_WATCH_TIME_MINUTES", "90")
)
USER_NAME = os.getenv("JELLYFIN_USER_NAME")
JELLYFIN_BASE_URL = os.getenv("JELLYFIN_BASE_URL", "http://localhost:8096")
JELLYFIN_TOKEN = os.getenv("JELLYFIN_TOKEN")

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

# Get script directory and log file path
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
LOG_FILE = os.path.join(SCRIPT_DIR, "jellyfin_time_limiter.log")

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


# Get users
response = make_request("GET", "/Users")

if response.status != 200:
    log(f"Error fetching users: {response.status}")
    sys.exit(1)

target_user_id = None

users = json.loads(response.data.decode("utf-8"))

for user in users:
    if user["Name"] == USER_NAME:
        target_user_id = user["Id"]
        break

if not target_user_id:
    log(f"Error: User '{USER_NAME}' not found")
    sys.exit(1)

# Check user activity from user_usage_stats plugin using custom query
# Query for today's playback activity only (resets at midnight)
stamp = int(datetime.now().timestamp() * 1000)
query_endpoint = f"/user_usage_stats/submit_custom_query?stamp={stamp}"

# Get today's date in YYYY-MM-DD format for explicit date comparison
today_date = datetime.now().strftime("%Y-%m-%d")

# SQL query to get today's playback activity for target user
# Using explicit date string to avoid timezone issues with DATE('now')
sql_query = f"""SELECT ROWID, *
FROM PlaybackActivity
WHERE DATE(DateCreated) = '{today_date}'
AND UserId = '{target_user_id}'
ORDER BY rowid DESC"""

query_payload = {
    "CustomQueryString": sql_query,
    "ReplaceUserId": False,  # We're using UserId in the query, so no need to replace
}

response = make_request(
    "POST",
    query_endpoint,
    headers={"accept": "application/json"},
    body=query_payload,
)

if response.status != 200:
    log(f"Warning: Could not fetch activity data: {response.status}")
    log(f"Response: {response.data.decode('utf-8')}")
    log("API error - defaulting to enabled access (fail-permissive)")
    ENABLE_ACCESS = True  # Fail-permissive: enable access when API is unavailable
    total_watch_time_minutes = None
else:
    # Parse query results
    response_data = json.loads(response.data.decode("utf-8"))

    # Response format: {'colums': [...], 'results': [[...], [...]], 'message': ''}
    columns = response_data.get("colums", [])  # Note: typo in API response
    results = response_data.get("results", [])

    # Handle empty results (no activity today)
    if not results:
        total_watch_time_seconds = 0
        total_watch_time_minutes = 0
        ENABLE_ACCESS = True  # No watch time means access should be enabled
    elif not columns:
        log("Error: No columns found in response")
        log(f"Response structure: {list(response_data.keys())}")
        ENABLE_ACCESS = False  # Default to disabled if we can't parse
        total_watch_time_minutes = None
    else:
        # Find the index of PlayDuration column
        try:
            play_duration_index = columns.index("PlayDuration")
        except ValueError:
            log("Error: PlayDuration column not found in response")
            log(f"Available columns: {columns}")
            log(f"Response data: {response_data}")
            ENABLE_ACCESS = False
            total_watch_time_minutes = None
        else:
            # Sum up total watch time for today
            total_watch_time_seconds = 0
            for row in results:
                if len(row) > play_duration_index:
                    duration_str = row[play_duration_index]
                    try:
                        total_watch_time_seconds += float(duration_str)
                    except (ValueError, TypeError):
                        log(f"Warning: Could not parse duration value: {duration_str}")
                        continue

            total_watch_time_minutes = total_watch_time_seconds / 60

            # Disable access if watch time exceeds limit
            ENABLE_ACCESS = total_watch_time_minutes < JELLYFIN_MAX_WATCH_TIME_MINUTES

# Get current user policy to check if update is needed
user_endpoint = f"/Users/{target_user_id}"
response = make_request("GET", user_endpoint)

if response.status != 200:
    log(f"Error fetching user: {response.status}")
    sys.exit(1)

user_data = json.loads(response.data.decode("utf-8"))
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
    if current_enable_all or (current_enabled_folders != []):
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
watch_time_str = (
    f"{total_watch_time_minutes:.1f}" if total_watch_time_minutes is not None else "?"
)
action = "enabled" if ENABLE_ACCESS else "disabled"
change_status = "changed" if needs_update else "unchanged"
log(
    f"User '{USER_NAME}': {watch_time_str} min / {JELLYFIN_MAX_WATCH_TIME_MINUTES} min - Access {action} ({change_status})"
)
