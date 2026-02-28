using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TimeLimiter.Services;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TimeLimiter.HostedServices;

/// <summary>
/// Hosted service that subscribes to session manager events and enforces playback time limits.
/// </summary>
public class PlaytimeHostedService : IHostedService, IDisposable
{
    private readonly ISessionManager _sessionManager;
    private readonly IUserManager _userManager;
    private readonly PlaytimeTrackerService _tracker;
    private readonly ILogger<PlaytimeHostedService> _logger;
    private CancellationTokenSource? _timerCts;
    private Task? _timerTask;
    private bool _disposed;
    private readonly Dictionary<string, DateTimeOffset> _lastStopAttempt = new();
    private readonly object _stopThrottleLock = new();
    private const int StopThrottleSeconds = 5;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaytimeHostedService"/> class.
    /// </summary>
    public PlaytimeHostedService(
        ISessionManager sessionManager,
        IUserManager userManager,
        PlaytimeTrackerService tracker,
        ILogger<PlaytimeHostedService> logger)
    {
        _sessionManager = sessionManager;
        _userManager = userManager;
        _tracker = tracker;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;

        _timerCts = new CancellationTokenSource();
        _timerTask = RunWarningTimerAsync(_timerCts.Token);

        _logger.LogInformation("TimeLimiter: PlaytimeHostedService started.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;

        if (_timerCts is not null)
        {
            await _timerCts.CancelAsync().ConfigureAwait(false);
        }

        if (_timerTask is not null)
        {
            try
            {
                await _timerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        // Persist elapsed time for any sessions still active at shutdown.
        _tracker.FlushActiveSessions();
        _logger.LogInformation("TimeLimiter: PlaytimeHostedService stopped.");
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        var session = e.Session;
        if (session.UserId.Equals(Guid.Empty))
        {
            return;
        }

        var userId = session.UserId;

        if (IsAdmin(userId))
        {
            return;
        }

        if (_tracker.IsOverLimit(userId))
        {
            _logger.LogInformation(
                "TimeLimiter: user {UserId} is over limit; blocking session {SessionId}",
                userId, session.Id);

            _tracker.MarkSessionBlocked(session.Id);
            _ = StopSessionAsync(session.Id);
            return;
        }

        var isPaused = session.PlayState?.IsPaused ?? false;
        _tracker.StartSession(session.Id, userId, isPaused);
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        var session = e.Session;

        if (_tracker.IsSessionBlocked(session.Id))
        {
            // Throttle repeated stop attempts to avoid spamming the session with commands.
            lock (_stopThrottleLock)
            {
                var now = DateTimeOffset.UtcNow;
                if (_lastStopAttempt.TryGetValue(session.Id, out var last) &&
                    (now - last).TotalSeconds < StopThrottleSeconds)
                {
                    return;
                }

                _lastStopAttempt[session.Id] = now;
            }

            _logger.LogInformation(
                "TimeLimiter: re-stopping blocked session {SessionId} on progress event",
                session.Id);

            _ = StopSessionAsync(session.Id);
            return;
        }

        // Advance accumulated time for tracked sessions.
        // Time only grows when progress events arrive, so ghost sessions that have lost
        // connectivity naturally stop accumulating (same approach as Playback Reporting plugin).
        if (_tracker.IsTracking(session.Id))
        {
            var isPaused = session.PlayState?.IsPaused ?? false;
            _tracker.TickSession(session.Id, isPaused);
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        lock (_stopThrottleLock)
        {
            _lastStopAttempt.Remove(e.Session.Id);
        }

        _tracker.UnblockSession(e.Session.Id);
        _tracker.StopSession(e.Session.Id);
        _tracker.Save();
    }

    private async Task StopSessionAsync(string sessionId)
    {
        try
        {
            await _sessionManager.SendPlaystateCommand(
                null,
                sessionId,
                new PlaystateRequest { Command = PlaystateCommand.Stop },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TimeLimiter: failed to stop session {SessionId}", sessionId);
        }
    }

    private async Task RunWarningTimerAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            ReconcileSessions();
            _tracker.FlushActiveSessions();
            await CheckWarningsAsync(ct).ConfigureAwait(false);
        }
    }

    // Reconciles tracked sessions against Jellyfin's live session list:
    // - Prunes sessions that no longer exist in Jellyfin (client crash/disconnect).
    // - Starts tracking sessions that are playing but not yet tracked (missed PlaybackStart).
    private void ReconcileSessions()
    {
        var knownSessionIds = new HashSet<string>(_sessionManager.Sessions.Select(s => s.Id));

        // Prune sessions gone from Jellyfin entirely.
        foreach (var sessionId in _tracker.GetTrackedSessionIds())
        {
            if (!knownSessionIds.Contains(sessionId))
            {
                _logger.LogInformation(
                    "TimeLimiter: pruning orphaned session {SessionId} (no longer in Jellyfin)",
                    sessionId);
                _tracker.UnblockSession(sessionId);
                _tracker.StopSession(sessionId);
            }
        }

        // Start tracking sessions that are playing but not tracked (missed PlaybackStart).
        foreach (var session in _sessionManager.Sessions)
        {
            if (session.NowPlayingItem is null) continue;
            if (session.UserId.Equals(Guid.Empty)) continue;
            if (IsAdmin(session.UserId)) continue;
            if (_tracker.IsTracking(session.Id)) continue;
            if (_tracker.IsSessionBlocked(session.Id)) continue;

            if (_tracker.IsOverLimit(session.UserId))
            {
                _logger.LogInformation(
                    "TimeLimiter: timer found over-limit untracked session {SessionId}, blocking",
                    session.Id);
                _tracker.MarkSessionBlocked(session.Id);
                _ = StopSessionAsync(session.Id);
            }
            else
            {
                _logger.LogInformation(
                    "TimeLimiter: timer found untracked session {SessionId}, starting tracking",
                    session.Id);
                _tracker.StartSession(session.Id, session.UserId, session.PlayState?.IsPaused ?? false);
            }
        }
    }

    private async Task CheckWarningsAsync(CancellationToken ct)
    {
        foreach (var session in _sessionManager.Sessions)
        {
            if (session.NowPlayingItem is null)
            {
                continue;
            }

            if (session.UserId.Equals(Guid.Empty))
            {
                continue;
            }

            var userId = session.UserId;

            if (IsAdmin(userId))
            {
                continue;
            }

            if (!_tracker.IsNearLimit(userId))
            {
                continue;
            }

            if (_tracker.HasWarnedSession(session.Id))
            {
                continue;
            }

            try
            {
                await _sessionManager.SendMessageCommand(
                    null,
                    session.Id,
                    new MessageCommand
                    {
                        Header = "Time Limit Warning",
                        Text = "Less than 5 minutes of viewing time remaining today.",
                        TimeoutMs = 10000
                    },
                    ct).ConfigureAwait(false);

                _tracker.MarkWarnedSession(session.Id);
                _logger.LogInformation(
                    "TimeLimiter: sent warning to session {SessionId} for user {UserId}",
                    session.Id, userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TimeLimiter: failed to send warning to session {SessionId}", session.Id);
            }
        }
    }

    private bool IsAdmin(Guid userId)
    {
        try
        {
            var user = _userManager.GetUserById(userId);
            return user?.HasPermission(PermissionKind.IsAdministrator) ?? false;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes managed resources.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _timerCts?.Dispose();
        }

        _disposed = true;
    }
}
