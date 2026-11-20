using System;
using System.Collections.Generic;
using System.Net.Mime;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Comseg.Catchup;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Comseg.Api;

/// <summary>
/// Saving programmes from the IPTV archive into the recordings. Open to users
/// who may manage live TV recordings, the permission Jellyfin's own timer
/// endpoints ask for.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.LiveTvManagement)]
[Route("Comseg/Catchup")]
[Produces(MediaTypeNames.Application.Json)]
public class CatchupController : ControllerBase
{
    private readonly CatchupQueue _queue;

    /// <summary>Initializes a new instance of the <see cref="CatchupController"/> class.</summary>
    /// <param name="queue">The catchup queue.</param>
    public CatchupController(CatchupQueue queue)
    {
        _queue = queue;
    }

    /// <summary>Queues a programme to be saved from the archive.</summary>
    /// <param name="request">The programme, as the client's guide describes it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>202 with the job; 400, 404, 409 or 503 with the reason.</returns>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<CatchupJob>> Queue([FromBody] CatchupRequest request, CancellationToken cancellationToken)
    {
        var user = User.FindFirstValue(ClaimTypes.Name) ?? "a client";
        var (job, problem, status) = await _queue.EnqueueAsync(request, user, cancellationToken).ConfigureAwait(false);
        if (job is not null && problem is null)
        {
            return Accepted(job);
        }

        return StatusCode(status, new ProblemResponse { Message = problem ?? "refused", Job = job });
    }

    /// <summary>The catchup jobs of the last day, newest first.</summary>
    /// <returns>The jobs.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<CatchupJob>> List() => Ok(_queue.Snapshot());

    /// <summary>Cancels a job; a download under way is stopped and its partial file removed.</summary>
    /// <param name="id">The job.</param>
    /// <returns>204, or 404 when there is no such job still running.</returns>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Cancel([FromRoute] Guid id) => _queue.Cancel(id) ? NoContent() : NotFound();

    /// <summary>Why a request was refused.</summary>
    public sealed class ProblemResponse
    {
        /// <summary>Gets or sets the reason.</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>Gets or sets the job already queued for the same programme, if that was the reason.</summary>
        public CatchupJob? Job { get; set; }
    }
}
