using System;
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

        _tracker.StartSession(session.Id, userId);
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        var session = e.Session;
        if (!_tracker.IsSessionBlocked(session.Id))
        {
            return;
        }

        // Session was blocked but is still reporting progress — stop it again.
        _logger.LogInformation(
            "TimeLimiter: re-stopping blocked session {SessionId} on progress event",
            session.Id);

        _ = StopSessionAsync(session.Id);
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
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
            _tracker.FlushActiveSessions();
            await CheckWarningsAsync(ct).ConfigureAwait(false);
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
