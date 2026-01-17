#! /usr/bin/env python3

import urllib3
import json
import sys
from datetime import datetime
import os

# Maximum watch time in minutes before disabling access
MAX_WATCH_TIME_MINUTES = 90

# Jellyfin server configuration
JELLYFIN_BASE_URL = os.getenv("JELLYFIN_BASE_URL")
JELLYFIN_TOKEN = os.getenv("JELLYFIN_TOKEN")

# Initialize HTTP client
urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)
http = urllib3.PoolManager(cert_reqs="CERT_NONE")


# Log with timestamp for cron
def log(message):
    timestamp = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    print(f"[{timestamp}] {message}")


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

kodi_user_id = None

users = json.loads(response.data.decode("utf-8"))

for user in users:
    if user["Name"] == "kodi":
        kodi_user_id = user["Id"]
        break

if not kodi_user_id:
    log("Error: Kodi user not found")
    sys.exit(1)

log(f"Kodi User ID: {kodi_user_id}")

# Check user activity from user_usage_stats plugin using custom query
# Query for today's playback activity only (resets at midnight)
stamp = int(datetime.now().timestamp() * 1000)
query_endpoint = f"/user_usage_stats/submit_custom_query?stamp={stamp}"

# Get today's date in YYYY-MM-DD format for explicit date comparison
today_date = datetime.now().strftime("%Y-%m-%d")
log(f"Querying playback activity for date: {today_date}")

# SQL query to get today's playback activity for kodi user
# Using explicit date string to avoid timezone issues with DATE('now')
sql_query = f"""SELECT ROWID, *
FROM PlaybackActivity
WHERE DATE(DateCreated) = '{today_date}'
AND UserId = '{kodi_user_id}'
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
    log("Proceeding with manual ENABLE_ACCESS setting...")
    ENABLE_ACCESS = False  # Default to disabled if we can't check
else:
    # Parse query results
    response_data = json.loads(response.data.decode("utf-8"))

    # Response format: {'colums': [...], 'results': [[...], [...]], 'message': ''}
    columns = response_data.get("colums", [])  # Note: typo in API response
    results = response_data.get("results", [])

    # Handle empty results (no activity today)
    if not results:
        log("No playback activity found for today")
        total_watch_time_seconds = 0
        total_watch_time_minutes = 0
        ENABLE_ACCESS = True  # No watch time means access should be enabled
        log(
            f"Watch time ({total_watch_time_minutes:.1f} min) is within limit ({MAX_WATCH_TIME_MINUTES} min). Access enabled."
        )
    elif not columns:
        log("Error: No columns found in response")
        log(f"Response structure: {list(response_data.keys())}")
        ENABLE_ACCESS = False  # Default to disabled if we can't parse
    else:
        # Find the index of PlayDuration column
        try:
            play_duration_index = columns.index("PlayDuration")
        except ValueError:
            log("Error: PlayDuration column not found in response")
            log(f"Available columns: {columns}")
            log(f"Response data: {response_data}")
            ENABLE_ACCESS = False
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
            log(
                f"Kodi user total watch time (today): {total_watch_time_minutes:.1f} minutes ({total_watch_time_seconds} seconds)"
            )

            # Disable access if watch time exceeds limit
            ENABLE_ACCESS = total_watch_time_minutes < MAX_WATCH_TIME_MINUTES

            if not ENABLE_ACCESS:
                log(
                    f"Watch time ({total_watch_time_minutes:.1f} min) exceeds limit ({MAX_WATCH_TIME_MINUTES} min). Disabling access."
                )
            else:
                log(
                    f"Watch time ({total_watch_time_minutes:.1f} min) is within limit ({MAX_WATCH_TIME_MINUTES} min). Access enabled."
                )

# Get current user policy to check if update is needed
user_endpoint = f"/Users/{kodi_user_id}"
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
    if not current_enable_all or (
        current_enabled_folders == [] and not current_enable_all
    ):
        needs_update = True
        log("Library access is currently disabled, enabling...")
    else:
        log("Library access is already enabled, no update needed.")
else:
    # Want to disable: check if currently enabled
    if current_enable_all or (current_enabled_folders != []):
        needs_update = True
        log("Library access is currently enabled, disabling...")
    else:
        log("Library access is already disabled, no update needed.")

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
    policy_endpoint = f"/Users/{kodi_user_id}/Policy"
    response = make_request("POST", policy_endpoint, body=policy)

    if response.status != 204 and response.status != 200:
        log(f"Error updating policy: {response.status}")
        log(f"Response: {response.data.decode('utf-8')}")
        sys.exit(1)

    if ENABLE_ACCESS:
        log("Successfully enabled library access for kodi user")
    else:
        log("Successfully disabled library access for kodi user")
else:
    # No update needed, but log the current state
    if ENABLE_ACCESS:
        log("Library access remains enabled")
    else:
        log("Library access remains disabled")
