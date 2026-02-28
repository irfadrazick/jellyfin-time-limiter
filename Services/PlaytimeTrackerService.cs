using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.TimeLimiter.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TimeLimiter.Services;

/// <summary>
/// Singleton service that tracks active playback sessions and persists daily totals.
/// Time is accumulated via PlaybackProgress ticks (like the Playback Reporting plugin)
/// rather than computing (now - startTime), so ghost sessions never accumulate time.
/// </summary>
public class PlaytimeTrackerService
{
    private const string DataFileName = "playtime.json";
    private const int WarnThresholdSeconds = 5 * 60; // 5 minutes
    private const int MaxTickGapSeconds = 120; // ignore gaps larger than this (e.g. after long pause or freeze)

    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<PlaytimeTrackerService> _logger;
    private readonly object _lock = new();
    private readonly object _saveLock = new();

    private PlaytimeData _data;
    private readonly Dictionary<string, ActiveSession> _activeSessions = new();
    private readonly HashSet<string> _warnedSessions = new();
    private readonly HashSet<string> _blockedSessions = new();

    private record ActiveSession(Guid UserId, DateTimeOffset LastTickTime, long AccumulatedSeconds, bool IsPaused);

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaytimeTrackerService"/> class.
    /// </summary>
    /// <param name="appPaths">Application paths.</param>
    /// <param name="logger">Logger instance.</param>
    public PlaytimeTrackerService(IApplicationPaths appPaths, ILogger<PlaytimeTrackerService> logger)
    {
        _appPaths = appPaths;
        _logger = logger;
        _data = new PlaytimeData();
        Load();
    }

    private string DataFilePath => Path.Combine(_appPaths.DataPath, "TimeLimiter", DataFileName);

    private static string TodayKey => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");

    /// <summary>
    /// Gets the number of completed (persisted) seconds for a user today.
    /// </summary>
    public long GetCompletedSecondsToday(Guid userId)
    {
        lock (_lock)
        {
            return GetCompletedSecondsTodayUnsafe(userId);
        }
    }

    /// <summary>
    /// Gets the total seconds today: completed + accumulated from all active sessions for this user.
    /// Active session time is event-driven (only grows when PlaybackProgress fires) so ghost
    /// sessions that have lost connectivity do not inflate this value.
    /// </summary>
    public long GetTotalSecondsToday(Guid userId)
    {
        lock (_lock)
        {
            var total = GetCompletedSecondsTodayUnsafe(userId);
            foreach (var (_, session) in _activeSessions)
            {
                if (session.UserId == userId)
                {
                    total += session.AccumulatedSeconds;
                }
            }

            return total;
        }
    }

    /// <summary>
    /// Resolves the effective daily limit in seconds for a user.
    /// Returns 0 if unlimited.
    /// </summary>
    public int GetLimitSeconds(Guid userId)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return 0;
        }

        int limitMinutes = config.DefaultDailyLimitMinutes;

        foreach (var entry in config.UserLimits)
        {
            if (entry.UserId == userId)
            {
                if (entry.DailyLimitMinutes == -1)
                {
                    // Use global default
                    limitMinutes = config.DefaultDailyLimitMinutes;
                }
                else
                {
                    limitMinutes = entry.DailyLimitMinutes;
                }

                break;
            }
        }

        return limitMinutes * 60;
    }

    /// <summary>
    /// Returns true if the user's completed (persisted) seconds today meet or exceed the limit.
    /// </summary>
    public bool IsOverLimit(Guid userId)
    {
        var limitSeconds = GetLimitSeconds(userId);
        if (limitSeconds <= 0)
        {
            return false;
        }

        return GetCompletedSecondsToday(userId) >= limitSeconds;
    }

    /// <summary>
    /// Returns true if the user is within the 5-minute warning threshold based on total seconds (completed + active).
    /// </summary>
    public bool IsNearLimit(Guid userId)
    {
        var limitSeconds = GetLimitSeconds(userId);
        if (limitSeconds <= 0)
        {
            return false;
        }

        var total = GetTotalSecondsToday(userId);
        var remaining = limitSeconds - total;
        return remaining > 0 && remaining < WarnThresholdSeconds;
    }

    /// <summary>
    /// Returns the IDs of all currently tracked active sessions.
    /// </summary>
    public IReadOnlyCollection<string> GetTrackedSessionIds()
    {
        lock (_lock)
        {
            return new List<string>(_activeSessions.Keys);
        }
    }

    /// <summary>
    /// Returns true if this session is currently being tracked.
    /// </summary>
    public bool IsTracking(string sessionId)
    {
        lock (_lock)
        {
            return _activeSessions.ContainsKey(sessionId);
        }
    }

    /// <summary>
    /// Records the start of a playback session.
    /// </summary>
    public void StartSession(string sessionId, Guid userId, bool isPaused = false)
    {
        lock (_lock)
        {
            _activeSessions[sessionId] = new ActiveSession(userId, DateTimeOffset.UtcNow, 0, isPaused);
            _logger.LogInformation("TimeLimiter: started session {SessionId} for user {UserId}", sessionId, userId);
        }
    }

    /// <summary>
    /// Advances the accumulated time for a session based on elapsed wall-clock time since the last tick.
    /// Paused intervals are excluded. Gaps larger than <see cref="MaxTickGapSeconds"/> are ignored
    /// (e.g. after a server freeze or reconnect).
    /// </summary>
    public void TickSession(string sessionId, bool isPaused)
    {
        lock (_lock)
        {
            if (!_activeSessions.TryGetValue(sessionId, out var session))
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var delta = (long)(now - session.LastTickTime).TotalSeconds;

            long newAccumulated = session.AccumulatedSeconds;

            // Only count time when both the previous and current state are "playing"
            if (!isPaused && !session.IsPaused && delta > 0 && delta <= MaxTickGapSeconds)
            {
                newAccumulated += delta;
            }

            _activeSessions[sessionId] = session with { LastTickTime = now, AccumulatedSeconds = newAccumulated, IsPaused = isPaused };
        }
    }

    /// <summary>
    /// Records the stop of a playback session. Adds any remaining accumulated seconds to the
    /// persisted total. Returns accumulated seconds, or 0 if the session was not tracked.
    /// </summary>
    public long StopSession(string sessionId)
    {
        lock (_lock)
        {
            if (!_activeSessions.TryGetValue(sessionId, out var session))
            {
                return 0;
            }

            _activeSessions.Remove(sessionId);
            _warnedSessions.Remove(sessionId);

            var accumulated = session.AccumulatedSeconds;
            if (accumulated > 0)
            {
                AddCompletedSecondsUnsafe(session.UserId, accumulated);
            }

            _logger.LogInformation(
                "TimeLimiter: stopped session {SessionId} for user {UserId}, accumulated {Accumulated}s",
                sessionId, session.UserId, accumulated);

            return accumulated;
        }
    }

    /// <summary>
    /// Persists accumulated time for all active sessions and resets each session's accumulator to zero.
    /// Safe for ghost sessions: if no progress events have arrived, accumulated is 0 and nothing is written.
    /// </summary>
    public void FlushActiveSessions()
    {
        lock (_lock)
        {
            foreach (var sessionId in new List<string>(_activeSessions.Keys))
            {
                var session = _activeSessions[sessionId];
                if (session.AccumulatedSeconds > 0)
                {
                    AddCompletedSecondsUnsafe(session.UserId, session.AccumulatedSeconds);
                    _activeSessions[sessionId] = session with { AccumulatedSeconds = 0 };
                }
            }
        }

        Save();
    }

    /// <summary>
    /// Returns true if this session has already received a warning message.
    /// </summary>
    public bool HasWarnedSession(string sessionId)
    {
        lock (_lock)
        {
            return _warnedSessions.Contains(sessionId);
        }
    }

    /// <summary>
    /// Marks this session as having received a warning message.
    /// </summary>
    public void MarkWarnedSession(string sessionId)
    {
        lock (_lock)
        {
            _warnedSessions.Add(sessionId);
        }
    }

    /// <summary>
    /// Marks this session as one we have issued a stop command to (over-limit enforcement).
    /// </summary>
    public void MarkSessionBlocked(string sessionId)
    {
        lock (_lock)
        {
            _blockedSessions.Add(sessionId);
        }
    }

    /// <summary>
    /// Returns true if we have already issued a stop command for this session.
    /// </summary>
    public bool IsSessionBlocked(string sessionId)
    {
        lock (_lock)
        {
            return _blockedSessions.Contains(sessionId);
        }
    }

    /// <summary>
    /// Removes this session from the blocked set (called when playback fully stops).
    /// </summary>
    public void UnblockSession(string sessionId)
    {
        lock (_lock)
        {
            _blockedSessions.Remove(sessionId);
        }
    }

    /// <summary>
    /// Resets today's completed playback record for the specified user.
    /// </summary>
    public void ResetUser(Guid userId)
    {
        lock (_lock)
        {
            var key = userId.ToString();
            if (_data.Records.TryGetValue(key, out var dates))
            {
                dates.Remove(TodayKey);
            }

            // Also reset any in-progress accumulated time for this user's active sessions.
            foreach (var sessionId in new List<string>(_activeSessions.Keys))
            {
                var session = _activeSessions[sessionId];
                if (session.UserId == userId)
                {
                    _activeSessions[sessionId] = session with { AccumulatedSeconds = 0 };
                }
            }

            _logger.LogInformation("TimeLimiter: reset today's record for user {UserId}", userId);
        }

        Save();
    }

    /// <summary>
    /// Persists the current playback data to disk.
    /// </summary>
    public void Save()
    {
        string json;
        lock (_lock)
        {
            json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
        }

        lock (_saveLock)
        {
            try
            {
                var path = DataFilePath;
                var dir = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(dir);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TimeLimiter: failed to save playtime data");
            }
        }
    }

    /// <summary>
    /// Loads persisted playback data from disk.
    /// </summary>
    public void Load()
    {
        try
        {
            if (!File.Exists(DataFilePath))
            {
                return;
            }

            var json = File.ReadAllText(DataFilePath);
            var loaded = JsonSerializer.Deserialize<PlaytimeData>(json);
            if (loaded is not null)
            {
                loaded.Records ??= new Dictionary<string, Dictionary<string, long>>();
                lock (_lock)
                {
                    _data = loaded;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TimeLimiter: failed to load playtime data");
        }
    }

    // Must be called under _lock
    private long GetCompletedSecondsTodayUnsafe(Guid userId)
    {
        var key = userId.ToString();
        if (_data.Records.TryGetValue(key, out var dates) &&
            dates.TryGetValue(TodayKey, out var seconds))
        {
            return seconds;
        }

        return 0;
    }

    // Must be called under _lock. Adds seconds to today's completed record.
    private void AddCompletedSecondsUnsafe(Guid userId, long seconds)
    {
        if (seconds <= 0)
        {
            return;
        }

        var key = userId.ToString();
        if (!_data.Records.TryGetValue(key, out var dates))
        {
            dates = new Dictionary<string, long>();
            _data.Records[key] = dates;
        }

        var dayKey = TodayKey;
        dates.TryGetValue(dayKey, out var existing);
        dates[dayKey] = existing + seconds;
    }
}
