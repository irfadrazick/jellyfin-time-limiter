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
/// </summary>
public class PlaytimeTrackerService
{
    private const string DataFileName = "playtime.json";
    private const int WarnThresholdSeconds = 5 * 60; // 5 minutes

    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<PlaytimeTrackerService> _logger;
    private readonly object _lock = new();
    private readonly object _saveLock = new();

    private PlaytimeData _data;
    private readonly Dictionary<string, (Guid UserId, DateTimeOffset StartTime)> _activeSessions = new();
    private readonly HashSet<string> _warnedSessions = new();
    private readonly HashSet<string> _blockedSessions = new();

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
            var key = userId.ToString();
            if (_data.Records.TryGetValue(key, out var dates) &&
                dates.TryGetValue(TodayKey, out var seconds))
            {
                return seconds;
            }

            return 0;
        }
    }

    /// <summary>
    /// Gets the total seconds today: completed + elapsed from all active sessions for this user.
    /// </summary>
    public long GetTotalSecondsToday(Guid userId)
    {
        lock (_lock)
        {
            var total = GetCompletedSecondsTodayUnsafe(userId);
            var now = DateTimeOffset.UtcNow;
            var startOfToday = now.Date; // midnight UTC
            foreach (var (_, session) in _activeSessions)
            {
                if (session.UserId == userId)
                {
                    var effectiveStart = session.StartTime < startOfToday ? startOfToday : session.StartTime;
                    total += (long)(now - effectiveStart).TotalSeconds;
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
    public void StartSession(string sessionId, Guid userId)
    {
        lock (_lock)
        {
            _activeSessions[sessionId] = (userId, DateTimeOffset.UtcNow);
            _logger.LogInformation("TimeLimiter: started session {SessionId} for user {UserId}", sessionId, userId);
        }
    }

    /// <summary>
    /// Records the stop of a playback session. Returns elapsed seconds, adds to persisted total.
    /// Returns 0 if the session was not tracked.
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

            var now = DateTimeOffset.UtcNow;
            var elapsed = (long)(now - session.StartTime).TotalSeconds;
            if (elapsed < 0)
            {
                elapsed = 0;
            }

            AddElapsedTimeUnsafe(session.UserId, session.StartTime, now);
            _logger.LogInformation(
                "TimeLimiter: stopped session {SessionId} for user {UserId}, elapsed {Elapsed}s",
                sessionId, session.UserId, elapsed);

            return elapsed;
        }
    }

    /// <summary>
    /// Persists elapsed time for all active sessions and resets each session's start time to now.
    /// This prevents losing time on server restart or crash (called on shutdown and every 60s).
    /// </summary>
    public void FlushActiveSessions()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var sessionId in new List<string>(_activeSessions.Keys))
            {
                var session = _activeSessions[sessionId];
                var elapsed = (long)(now - session.StartTime).TotalSeconds;
                if (elapsed > 0)
                {
                    AddElapsedTimeUnsafe(session.UserId, session.StartTime, now);
                    // Reset start time to now so elapsed doesn't get double-counted
                    _activeSessions[sessionId] = (session.UserId, now);
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

    // Must be called under _lock. Splits elapsed time at UTC day boundaries so sessions
    // spanning midnight are credited to the correct day.
    private void AddElapsedTimeUnsafe(Guid userId, DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from)
        {
            return;
        }

        var key = userId.ToString();
        if (!_data.Records.TryGetValue(key, out var dates))
        {
            dates = new Dictionary<string, long>();
            _data.Records[key] = dates;
        }

        var current = from;
        while (current < to)
        {
            var nextMidnight = new DateTimeOffset(current.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
            var segmentEnd = to < nextMidnight ? to : nextMidnight;
            var seconds = (long)(segmentEnd - current).TotalSeconds;
            if (seconds > 0)
            {
                var dayKey = current.UtcDateTime.ToString("yyyy-MM-dd");
                dates.TryGetValue(dayKey, out var existing);
                dates[dayKey] = existing + seconds;
            }

            current = segmentEnd;
        }
    }
}
