# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

Single-file Python script (`jellyfin_time_limiter.py`) that enforces a daily watch-time limit on a Jellyfin user by toggling their library access. It is designed to be run on a schedule (cron / systemd timer, every ~5 min), not as a daemon — each invocation runs once and exits.

## Running

```bash
JELLYFIN_TOKEN="..." JELLYFIN_USER_NAME="kodi" python3 jellyfin_time_limiter.py
```

Required env vars: `JELLYFIN_TOKEN`, `JELLYFIN_USER_NAME`.
Optional: `JELLYFIN_BASE_URL` (default `http://localhost:8096`), `JELLYFIN_MAX_WATCH_TIME_MINUTES` (default `90`).

Dependency: `urllib3` only (`pip3 install urllib3 --break-system-packages`). No test suite, no build, no linter configured.

## Jellyfin dependency

Uses **Jellyfin core only** — no plugins required. Watch time is derived from the core `GET /Sessions` endpoint (a live snapshot of what's playing). The script does its own day-by-day accounting in a state file, so it is not coupled to any plugin's schema/API.

> History note: earlier versions read watch time from the **Playback Reporting plugin** via `POST /user_usage_stats/submit_custom_query` (raw SQL over the plugin's `PlaybackActivity` table). That dependency was removed because plugin changes could break the script.

## How it works (single linear flow, top to bottom in the file)

1. `GET /Users` → resolve username to user ID (needed for the policy endpoints).
2. Load the state file (resets to zero when the stored local date != today → daily midnight reset, tied to the host's local time).
3. `GET /Sessions`. For each active, unpaused session of the target user, add how far the playhead advanced since the previous run:
   `watched = clamp(position_delta, 0, wall_clock_gap)`. Position is `PlayState.PositionTicks` (100-ns ticks; `TICKS_PER_SECOND = 10_000_000`). Capping at the wall-clock gap stops seeks/fast-forwards from inflating the tally; `max(0, …)` ignores rewinds; paused/idle adds ~0 because position doesn't move.
4. Persist the updated total and per-session anchors (atomically), then compute `ENABLE_ACCESS = total_minutes < limit`.
5. `GET /Users/{id}` to read current `Policy`, and only `POST /Users/{id}/Policy` if the desired state differs (avoids redundant API writes). Disabling sets `EnableAllFolders=False` and `EnabledFolders=[]`; enabling sets `EnableAllFolders=True`.

## State file (`jellyfin_time_limiter_state.json`, in the script dir)

`{ date, total_seconds, last_epoch, sessions: { <sessionId>: {item_id, position_ticks, paused} } }`. It is **load-bearing**: deleting it resets today's tally to zero. A run only counts an interval when the same `item_id` was playing and unpaused at *both* ends, so the per-session anchors must survive between runs.

## Key behaviors to preserve when editing

- **Sampling model**: accumulation is incremental across cron runs, not a single query. Enforcement only happens while the scheduler runs, and a session ending between polls loses at most ~one interval of watch time. The cron interval is therefore part of the behavior.
- **Fail-permissive on transient errors**: if `GET /Sessions` fails or its JSON can't be parsed, the script does **not** reset the per-session anchors and does **not** accumulate — it just evaluates against the existing total. So a temporary hiccup neither loses time nor locks the user out; the next run retries.
- **Log deduplication**: `log()` writes to `jellyfin_time_limiter.log` (in the script dir) only when the message differs from the last line. The final line includes the running minutes, so it logs on each run while time is accruing and dedupes while idle.
- TLS verification is disabled (`cert_reqs="CERT_NONE"`) to support self-signed Jellyfin servers.

`*.log` and the state file (`jellyfin_time_limiter_state.json`, `.state-*.tmp`) are gitignored.
