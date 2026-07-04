# Jellyfin Time Limiter

Automatically disable library access for Jellyfin users when their daily watch time exceeds a configured limit. The script monitors playback activity and enforces time limits by disabling library access when the threshold is exceeded.

## Features

- Monitors daily watch time for specified users
- Automatically disables library access when time limit is exceeded
- Automatically re-enables access when watch time is within limits
- Resets daily at midnight based on the local time of the system running this script
- Only updates policy when changes are needed (avoids unnecessary API calls)
- Configurable via environment variables

## Prerequisites

### Jellyfin

No plugins required — the script uses only the Jellyfin core API (`/Sessions`, `/Users`). The API token must belong to an administrator so it can read sessions and update user policy.

> Note: watch time is now measured by polling live playback sessions on each run, so the script must run on a schedule (e.g. cron every 5 minutes) for accurate accounting. Earlier versions depended on the Playback Reporting plugin; that dependency has been removed.

### Python Dependencies

The script requires Python 3 and the `urllib3` library:

```bash
pip3 install urllib3 --break-system-packages
```

Note: Use `--break-system-packages` with caution.

## Configuration

The script is configured via environment variables. All variables prefixed with `JELLYFIN_` are required unless otherwise noted.

### Required Environment Variables

- `JELLYFIN_TOKEN` - API token for authentication
  - Generate from: Dashboard → API Keys → New API Key
- `JELLYFIN_USER_NAME` - Username to monitor (e.g., `kodi`, `jellyfin`)

### Optional Environment Variables

- `JELLYFIN_BASE_URL` - Base URL of your Jellyfin server (default: `http://localhost:8096`)
  - Only required if your Jellyfin server is not running on localhost:8096
- `JELLYFIN_MAX_WATCH_TIME_MINUTES` - Maximum watch time in minutes before disabling access (default: `90`)

## Getting Your API Token

1. Log into Jellyfin web interface as an administrator
2. Go to Dashboard → API Keys
3. Click "New API Key"
4. Give it a name (e.g., "Time Limiter")
5. Copy the generated token
6. Set it as the `JELLYFIN_TOKEN` environment variable

## Installation

1. Clone or download this repository
2. Install Python dependencies:
   ```bash
   pip3 install urllib3 --break-system-packages
   ```
   Note: Use `--break-system-packages` with caution.
3. Make the script executable:
   ```bash
   chmod +x jellyfin_time_limiter.py
   ```

## Usage

### Manual Execution

Set the required environment variables and run the script:

```bash
export JELLYFIN_TOKEN="your-api-token-here"
export JELLYFIN_USER_NAME="kodi"
export JELLYFIN_BASE_URL="http://192.168.1.100:8096"  # Optional, defaults to http://localhost:8096
export JELLYFIN_MAX_WATCH_TIME_MINUTES=90  # Optional, defaults to 90

python3 jellyfin_time_limiter.py
```

### Automated Execution with Cron

For automated monitoring, set up a cron job to run the script every 5 minutes:

1. Edit your crontab:
   ```bash
   crontab -e
   ```

2. **Option A: With environment variables inline** (adjust paths and values as needed):
   ```bash
   */5 * * * * JELLYFIN_TOKEN="your-token" JELLYFIN_USER_NAME="kodi" JELLYFIN_BASE_URL="http://192.168.1.100:8096" JELLYFIN_MAX_WATCH_TIME_MINUTES=90 /usr/bin/python3 /opt/jellyfin-time-limiter/jellyfin_time_limiter.py
   ```

   Note: `JELLYFIN_BASE_URL` is optional if using localhost:8096

   Note: The script writes logs to `jellyfin_time_limiter.log` in the script directory. No redirection needed.

3. **Option B: Using a wrapper script** (recommended for cleaner configuration):

   Create `/opt/jellyfin-time-limiter/jellyfin-time-limiter-wrapper.sh`:
   ```bash
   #!/bin/bash
   export JELLYFIN_TOKEN="your-api-token-here"
   export JELLYFIN_USER_NAME="kodi"
   export JELLYFIN_BASE_URL="http://192.168.1.100:8096"  # Optional, defaults to http://localhost:8096
   export JELLYFIN_MAX_WATCH_TIME_MINUTES=90

   /usr/bin/python3 /opt/jellyfin-time-limiter/jellyfin_time_limiter.py
   ```

   Make it executable:
   ```bash
   chmod +x /opt/jellyfin-time-limiter/jellyfin-time-limiter-wrapper.sh
   ```

   Then in crontab:
   ```bash
   */5 * * * * /opt/jellyfin-time-limiter/jellyfin-time-limiter-wrapper.sh
   ```

   Note: The script writes logs to `jellyfin_time_limiter.log` in the script directory. No redirection needed.

### Systemd Service (Alternative)

You can also run this as a systemd service with a timer. Create `/etc/systemd/system/jellyfin-time-limiter.service`:

```ini
[Unit]
Description=Jellyfin Time Limiter
After=network.target

[Service]
Type=oneshot
Environment="JELLYFIN_TOKEN=your-api-token-here"
Environment="JELLYFIN_USER_NAME=kodi"
Environment="JELLYFIN_BASE_URL=http://192.168.1.100:8096"
Environment="JELLYFIN_MAX_WATCH_TIME_MINUTES=90"
ExecStart=/usr/bin/python3 /opt/jellyfin-time-limiter/jellyfin_time_limiter.py
StandardOutput=journal
StandardError=journal
```

And `/etc/systemd/system/jellyfin-time-limiter.timer`:

```ini
[Unit]
Description=Run Jellyfin Time Limiter every 5 minutes

[Timer]
OnBootSec=5min
OnUnitActiveSec=5min

[Install]
WantedBy=timers.target
```

Enable and start:
```bash
sudo systemctl enable jellyfin-time-limiter.timer
sudo systemctl start jellyfin-time-limiter.timer
```

## How It Works

1. **Fetches User Information**: Retrieves the user ID for the specified username (needed to update the user policy).
2. **Loads Daily State**: Reads `jellyfin_time_limiter_state.json`. If the stored date is not today, the tally resets to zero (daily midnight reset, based on the local time of the system running the script).
3. **Polls Live Sessions**: Calls `GET /Sessions` and, for each active and unpaused session belonging to the monitored user, measures how far the playhead advanced since the previous run. The increment is `clamp(position_delta, 0, wall_clock_gap)` — so paused/idle time counts as nothing, rewinds are ignored, and seeks/fast-forwards can't add more than the real elapsed time.
4. **Accumulates Watch Time**: Adds those increments to today's running total and saves the updated state (written atomically).
5. **Compares Against Limit**: Checks if the accumulated total exceeds the configured limit.
6. **Updates Policy**:
   - If watch time exceeds limit: Disables library access (`EnableAllFolders = False`, `EnabledFolders = []`)
   - If watch time is within limit: Enables library access (`EnableAllFolders = True`)
   - Only updates if the current policy state differs from desired state

### State File

The script tracks watch time in `jellyfin_time_limiter_state.json` in the script directory. It holds the current day, the accumulated seconds, and a per-session anchor (last item and playhead position) used to compute the next increment. **Deleting this file resets today's tally to zero.** It is git-ignored.

## Error Handling Behavior

- **Sessions API Unavailable / Unparseable**: If `GET /Sessions` fails or returns malformed data, the script does **not** change the running total and does **not** discard its per-session anchors. It simply evaluates access against whatever total it already has and retries on the next run. Temporary errors therefore neither lose watch time nor wrongly lock users out.

- **No Active Playback**: When the user isn't playing anything, nothing is added — the total stays where it is (0 at the start of a day), so access remains enabled until the limit is reached.

- **Enforcement Requires the Scheduler**: Because accounting is incremental across runs, watch time is only counted while the script runs on its schedule. If the scheduler is stopped, no time accrues and no limit is enforced.

## Logging

The script logs actions to `jellyfin_time_limiter.log` in the script directory. Logs are deduplicated - only changes are logged to avoid log pollution. Example output:

```
[2026-01-18 00:40:31] User 'kodi': 128.1 min / 90 min - Access disabled (changed)
[2026-01-18 00:53:42] User 'kodi': 1.5 min / 90 min - Access enabled (changed)
```

The log format includes: username, watch time, limit, access status, and whether a change was made.

## Troubleshooting

### Watch time always reads 0 / limit never triggers
- Confirm the script is actually running on a schedule (cron/systemd timer). Accounting is incremental — a single manual run only captures the playback between the two most recent runs.
- Check that the schedule interval is short enough (5 minutes is recommended) so playback is sampled while it's happening.
- Make sure `jellyfin_time_limiter_state.json` is writable and not being deleted between runs.

### "User not found"
- Verify the `JELLYFIN_USER_NAME` matches exactly (case-sensitive)
- Check that the user exists in your Jellyfin server

### "Error fetching users" or "Could not fetch sessions"
- Verify `JELLYFIN_BASE_URL` is correct and accessible
- Check that `JELLYFIN_TOKEN` is valid and belongs to an administrator
- Ensure the Jellyfin server is running and accessible

### Watch time seems too low
- Time is only counted while the scheduler runs. Playback that starts and stops entirely between two runs may be under-counted by up to one interval.
- Watch time resets at midnight local time of the host running the script.

## Security Notes

- Store API tokens securely (consider using a secrets manager)
- Use HTTPS if your Jellyfin server supports it (update `JELLYFIN_BASE_URL`)
- The script uses `verify=False` for SSL certificates - consider using proper certificates in production
- Limit API token permissions if possible

## License

This script is provided as-is for personal use.
