# Jellyfin Time Limiter

Automatically disable library access for Jellyfin users when their daily watch time exceeds a configured limit. The script monitors playback activity and enforces time limits by disabling library access when the threshold is exceeded.

## Features

- Monitors daily watch time for specified users
- Automatically disables library access when time limit is exceeded
- Automatically re-enables access when watch time is within limits
- Resets daily at midnight (local time)
- Only updates policy when changes are needed (avoids unnecessary API calls)
- Configurable via environment variables

## Prerequisites

### Required Jellyfin Plugin

**Playback Reporting Plugin** must be installed and enabled in your Jellyfin server. This plugin provides the playback activity data that the script uses to calculate watch time.

1. Go to Dashboard → Plugins
2. Install "Playback Reporting" plugin if not already installed
3. Ensure the plugin is enabled

### Python Dependencies

The script requires Python 3 and the `urllib3` library:

```bash
pip install urllib3
```

## Configuration

The script is configured via environment variables. All variables prefixed with `JELLYFIN_` are required unless otherwise noted.

### Required Environment Variables

- `JELLYFIN_BASE_URL` - Base URL of your Jellyfin server (e.g., `http://192.168.1.100:8096`)
- `JELLYFIN_TOKEN` - API token for authentication
  - Generate from: Dashboard → API Keys → New API Key
- `JELLYFIN_USER_NAME` - Username to monitor (e.g., `kodi`, `jellyfin`)

### Optional Environment Variables

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
   pip install urllib3
   ```
3. Make the script executable:
   ```bash
   chmod +x jellyfin_time_limiter.py
   ```

## Usage

### Manual Execution

Set the required environment variables and run the script:

```bash
export JELLYFIN_BASE_URL="http://192.168.1.100:8096"
export JELLYFIN_TOKEN="your-api-token-here"
export JELLYFIN_USER_NAME="kodi"
export JELLYFIN_MAX_WATCH_TIME_MINUTES=90  # Optional, defaults to 90

python3 jellyfin_time_limiter.py
```

### Automated Execution with Cron

For automated monitoring, set up a cron job to run the script every 5 minutes:

1. Edit your crontab:
   ```bash
   crontab -e
   ```

2. Add the following line (adjust paths as needed):
   ```bash
   */5 * * * * /usr/bin/python3 /path/to/jellyfin_time_limiter.py >> /var/log/jellyfin-time-limiter.log 2>&1
   ```

3. Or with environment variables inline:
   ```bash
   */5 * * * * JELLYFIN_BASE_URL="http://192.168.1.100:8096" JELLYFIN_TOKEN="your-token" JELLYFIN_USER_NAME="kodi" JELLYFIN_MAX_WATCH_TIME_MINUTES=90 /usr/bin/python3 /path/to/jellyfin_time_limiter.py >> /var/log/jellyfin-time-limiter.log 2>&1
   ```

4. Or create a wrapper script with environment variables:

   Create `/path/to/jellyfin-time-limiter-wrapper.sh`:
   ```bash
   #!/bin/bash
   export JELLYFIN_BASE_URL="http://192.168.1.100:8096"
   export JELLYFIN_TOKEN="your-api-token-here"
   export JELLYFIN_USER_NAME="kodi"
   export JELLYFIN_MAX_WATCH_TIME_MINUTES=90

   /usr/bin/python3 /path/to/jellyfin_time_limiter.py
   ```

   Make it executable:
   ```bash
   chmod +x /path/to/jellyfin-time-limiter-wrapper.sh
   ```

   Then in crontab:
   ```bash
   */5 * * * * /path/to/jellyfin-time-limiter-wrapper.sh >> /var/log/jellyfin-time-limiter.log 2>&1
   ```

### Systemd Service (Alternative)

You can also run this as a systemd service with a timer. Create `/etc/systemd/system/jellyfin-time-limiter.service`:

```ini
[Unit]
Description=Jellyfin Time Limiter
After=network.target

[Service]
Type=oneshot
Environment="JELLYFIN_BASE_URL=http://192.168.1.100:8096"
Environment="JELLYFIN_TOKEN=your-api-token-here"
Environment="JELLYFIN_USER_NAME=kodi"
Environment="JELLYFIN_MAX_WATCH_TIME_MINUTES=90"
ExecStart=/usr/bin/python3 /path/to/jellyfin_time_limiter.py
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

1. **Fetches User Information**: Retrieves the user ID for the specified username
2. **Queries Playback Activity**: Uses the Playback Reporting plugin's custom query API to get today's playback activity
3. **Calculates Watch Time**: Sums up all `PlayDuration` values from today's records
4. **Compares Against Limit**: Checks if total watch time exceeds the configured limit
5. **Updates Policy**:
   - If watch time exceeds limit: Disables library access (`EnableAllFolders = False`, `EnabledFolders = []`)
   - If watch time is within limit: Enables library access (`EnableAllFolders = True`)
   - Only updates if the current policy state differs from desired state

## Logging

The script logs all actions with timestamps. Example output:

```
[2026-01-18 00:40:31] User 'kodi' ID: 25223fdf00a9490fa539c319cf324b02
[2026-01-18 00:40:31] Querying playback activity for date: 2026-01-18
[2026-01-18 00:40:31] User 'kodi' total watch time (today): 128.1 minutes (7687.0 seconds)
[2026-01-18 00:40:31] Watch time (128.1 min) exceeds limit (90 min). Disabling access.
[2026-01-18 00:40:31] Library access is currently enabled, disabling...
[2026-01-18 00:40:31] Successfully disabled library access for user 'kodi'
```

## Troubleshooting

### "PlayDuration column not found in response"
- Ensure the Playback Reporting plugin is installed and enabled
- Check that the plugin has recorded some playback activity

### "User not found"
- Verify the `JELLYFIN_USER_NAME` matches exactly (case-sensitive)
- Check that the user exists in your Jellyfin server

### "Error fetching users" or "Error fetching activity data"
- Verify `JELLYFIN_BASE_URL` is correct and accessible
- Check that `JELLYFIN_TOKEN` is valid and has appropriate permissions
- Ensure the Jellyfin server is running and accessible

### No activity found for today
- This is normal if the user hasn't watched anything today
- The script will enable access (0 minutes < limit)
- Watch time resets at midnight local time

## Security Notes

- Store API tokens securely (consider using a secrets manager)
- Use HTTPS if your Jellyfin server supports it (update `JELLYFIN_BASE_URL`)
- The script uses `verify=False` for SSL certificates - consider using proper certificates in production
- Limit API token permissions if possible

## License

This script is provided as-is for personal use.
