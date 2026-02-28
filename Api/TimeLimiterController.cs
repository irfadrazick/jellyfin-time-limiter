using System;
using System.Collections.Generic;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.TimeLimiter.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TimeLimiter.Api;

/// <summary>
/// API controller for Time Limiter plugin operations.
/// </summary>
[ApiController]
[Route("TimeLimiter")]
[Authorize(Policy = "RequiresElevation")]
public class TimeLimiterController : ControllerBase
{
    private readonly PlaytimeTrackerService _tracker;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="TimeLimiterController"/> class.
    /// </summary>
    public TimeLimiterController(PlaytimeTrackerService tracker, IUserManager userManager)
    {
        _tracker = tracker;
        _userManager = userManager;
    }

    /// <summary>
    /// Gets usage status for all non-admin users.
    /// </summary>
    /// <returns>List of user usage records.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<UserStatusDto>> GetStatus()
    {
        var results = new List<UserStatusDto>();

        foreach (var user in _userManager.Users)
        {
            if (user.HasPermission(PermissionKind.IsAdministrator))
            {
                continue;
            }

            var limitSeconds = _tracker.GetLimitSeconds(user.Id);
            var usedSeconds = _tracker.GetTotalSecondsToday(user.Id);

            results.Add(new UserStatusDto
            {
                UserId = user.Id,
                UserName = user.Username,
                LimitSeconds = limitSeconds,
                UsedSeconds = usedSeconds,
                RemainingSeconds = limitSeconds > 0 ? Math.Max(0, limitSeconds - usedSeconds) : -1
            });
        }

        return Ok(results);
    }

    /// <summary>
    /// Resets today's playback record for a specific user.
    /// </summary>
    /// <param name="userId">The user ID to reset.</param>
    /// <returns>No content on success.</returns>
    [HttpPost("Reset/{userId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult ResetUser([FromRoute] Guid userId)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return NotFound();
        }

        _tracker.ResetUser(userId);
        return NoContent();
    }
}

/// <summary>
/// DTO for user playback status.
/// </summary>
public class UserStatusDto
{
    /// <summary>Gets or sets the user ID.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the username.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Gets or sets the daily limit in seconds (0 = unlimited).</summary>
    public int LimitSeconds { get; set; }

    /// <summary>Gets or sets total seconds used today.</summary>
    public long UsedSeconds { get; set; }

    /// <summary>Gets or sets seconds remaining today (-1 if unlimited).</summary>
    public long RemainingSeconds { get; set; }
}
