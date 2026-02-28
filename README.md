# Jellyfin Time Limiter Plugin

A Jellyfin plugin that enforces daily playback time limits per user. Once a user reaches their limit, new playback is blocked for the rest of the day. Any session already playing when the limit is reached is allowed to finish.

## Features

- **Per-user daily limits** with a configurable global default
- **5-minute warning** — a notification is sent to the client before the limit is reached
- **Admin exempt** — administrator accounts are never tracked or blocked
- **Crash-safe** — active session time is flushed to disk every 60 seconds and on shutdown, so time is not lost on server restart
- **Reset** — admins can reset any user's usage for the day from the config page

## Installation

See [BUILD.md](BUILD.md) for prerequisites and full build instructions.

**Quick steps:**

1. Build the plugin:
   ```sh
   dotnet build -c Release
   ```

2. Copy to your Jellyfin server and restart:
   ```sh
   scp bin/Release/net9.0/Jellyfin.Plugin.TimeLimiter.dll root@jellyfin:/var/lib/jellyfin/plugins/TimeLimiter_1.0.0.0/
   ssh root@jellyfin "chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/TimeLimiter_1.0.0.0 && systemctl restart jellyfin"
   ```

3. Verify: open the Jellyfin Dashboard → **Plugins** — "Time Limiter" should appear under the General category.

## Configuration

Open **Time Limiter** from the Jellyfin sidebar (or Dashboard → Plugins → Time Limiter → Settings).

### Global Daily Limit

Sets the default limit for all non-admin users. Enter a value in minutes (`0` = no limit).

### Per-User Limits

Each non-admin user appears in the table with the following columns:

| Column | Description |
|---|---|
| **Limit** | Per-user override in minutes. `-1` = use global limit, `0` = no limit for this user. |
| **Used Today** | Total playback time recorded today (UTC). |
| **Remaining** | Time left before the limit is reached. Shows "Unlimited" if no limit is set. |
| **Reset** | Clears today's usage for that user immediately. |

Click **Refresh** to update the Used Today and Remaining columns without reloading the page. Click **Save** to apply any limit changes.

## How It Works

- Limits reset at **midnight UTC** each day.
- A user who is over their limit cannot start new playback. Any video they attempt to play will be stopped automatically.
- A **warning message** appears on the client when less than 5 minutes of viewing time remain.
- Admins are always exempt — their playback is never tracked or interrupted.
