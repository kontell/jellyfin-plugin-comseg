using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Comseg.Comskip;
using Jellyfin.Plugin.Comseg.Live;
using Jellyfin.Plugin.Comseg.Segments;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Comseg.Api;

/// <summary>Admin endpoints behind the configuration page.</summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Comseg")]
[Produces(MediaTypeNames.Application.Json)]
public class ComsegController : ControllerBase
{
    private readonly RecordingWatcher _watcher;
    private readonly ComskipRunner _runner;
    private readonly CompletedPass _pass;
    private readonly SegmentStore _store;
    private readonly DetectionRegistry _registry;
    private readonly ILibraryManager _library;
    private readonly ILogger<ComsegController> _logger;

    /// <summary>Initializes a new instance of the <see cref="ComsegController"/> class.</summary>
    /// <param name="watcher">The watcher.</param>
    /// <param name="runner">The Comskip runner.</param>
    /// <param name="pass">The full-pass runner.</param>
    /// <param name="store">The segment store.</param>
    /// <param name="registry">The detection registry.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public ComsegController(
        RecordingWatcher watcher,
        ComskipRunner runner,
        CompletedPass pass,
        SegmentStore store,
        DetectionRegistry registry,
        ILibraryManager library,
        ILogger<ComsegController> logger)
    {
        _watcher = watcher;
        _runner = runner;
        _pass = pass;
        _store = store;
        _registry = registry;
        _library = library;
        _logger = logger;
    }

    /// <summary>The live jobs and the configured binary.</summary>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<StatusResponse> GetStatus()
    {
        var jobs = _watcher.Snapshot().Select(j => new JobStatus
        {
            Path = j.Path,
            ItemId = j.Item?.Id,
            Name = j.Item?.Name,
            StartedUtc = j.StartedUtc,
            Analyses = j.Tracker.Passes,
            Confirmed = j.Tracker.Confirmed.Count,
            Published = j.Published,
            ProcessRunning = j.Process is not null && !j.Process.HasExited,
            CopyHeight = j.CopyHeight,
            Finalizing = j.Finalizing,
            Finished = j.Finished,
            LastError = Problems(j),
        }).ToList();
        return new StatusResponse { ComskipPath = ComsegPlugin.Config.ComskipPath, ProviderId = Providers.ComsegSegmentProvider.ProviderId, Jobs = jobs };
    }

    /// <summary>Runs the configured Comskip with --help to prove the path works, and says which ini runs start from.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The banner, or the error, and the ini.</returns>
    [HttpPost("Check")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<CheckResponse>> Check(CancellationToken cancellationToken)
    {
        var config = ComsegPlugin.Config;
        var (ok, text) = await _runner.ProbeAsync(config.ComskipPath, cancellationToken).ConfigureAwait(false);
        return new CheckResponse { Ok = ok, Text = text, Ini = ComskipIni.Describe(config.IniPath) };
    }

    /// <summary>Runs a full pass over a finished recording in the background, replacing its rows.</summary>
    /// <param name="itemId">The item.</param>
    /// <returns>202 when queued.</returns>
    [HttpPost("Detect/{itemId}")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult Detect([FromRoute] Guid itemId)
    {
        var item = _library.GetItemById(itemId);
        if (item is null || string.IsNullOrEmpty(item.Path))
        {
            return NotFound();
        }

        if (!_registry.TryEnter(item.Id))
        {
            return Conflict("a detection is already running for this item");
        }

        // A finished live job may still be keeping its breaks in place; this
        // pass replaces them.
        _watcher.StandDown(item.Id);
        var config = ComsegPlugin.Config;
        _ = Task.Run(async () =>
        {
            try
            {
                var breaks = await _pass.RunAsync(item.Path, config, CancellationToken.None).ConfigureAwait(false);
                if (breaks is not null && await _store.PublishAsync(item, breaks, "on demand", final: true).ConfigureAwait(false) == 0)
                {
                    _logger.LogInformation("Comseg: the on-demand pass over {Name} agrees with the stored breaks", item.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Comseg: on-demand detection failed for {Name}", item.Name);
            }
            finally
            {
                _registry.Exit(item.Id);
            }
        });
        return Accepted();
    }

    /// <summary>A job's problems for the status page: storing the breaks first, then Comskip.</summary>
    private static string? Problems(LiveJob job) => (job.StoreError, job.LastError) switch
    {
        (null, var comskip) => comskip,
        (var store, null) => store,
        (var store, var comskip) => store + "; " + comskip,
    };

    /// <summary>The status response.</summary>
    public sealed class StatusResponse
    {
        /// <summary>Gets or sets the configured binary.</summary>
        public string ComskipPath { get; set; } = string.Empty;

        /// <summary>Gets or sets the provider id Jellyfin filters by.</summary>
        public string ProviderId { get; set; } = string.Empty;

        /// <summary>Gets or sets the live jobs.</summary>
        public IReadOnlyList<JobStatus> Jobs { get; set; } = Array.Empty<JobStatus>();
    }

    /// <summary>One live job.</summary>
    public sealed class JobStatus
    {
        /// <summary>Gets or sets the recording file.</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>Gets or sets the item id, once known.</summary>
        public Guid? ItemId { get; set; }

        /// <summary>Gets or sets the item name, once known.</summary>
        public string? Name { get; set; }

        /// <summary>Gets or sets when the job started.</summary>
        public DateTime StartedUtc { get; set; }

        /// <summary>Gets or sets how many analyses were read.</summary>
        public int Analyses { get; set; }

        /// <summary>Gets or sets how many breaks the analyses agree on so far.</summary>
        public int Confirmed { get; set; }

        /// <summary>Gets or sets how many breaks are stored in Jellyfin (needs the recording's library item).</summary>
        public int Published { get; set; }

        /// <summary>Gets or sets a value indicating whether Comskip is running.</summary>
        public bool ProcessRunning { get; set; }

        /// <summary>Gets or sets the height of the scaled copy the live Comskip reads, or null when it reads the recording.</summary>
        public int? CopyHeight { get; set; }

        /// <summary>Gets or sets a value indicating whether the recording ended and its final pass is running.</summary>
        public bool Finalizing { get; set; }

        /// <summary>Gets or sets a value indicating whether the final pass is over and the job only keeps the breaks in place until Jellyfin's refresh of the closed file.</summary>
        public bool Finished { get; set; }

        /// <summary>Gets or sets the last problem: storing the breaks, then Comskip.</summary>
        public string? LastError { get; set; }
    }

    /// <summary>The check response.</summary>
    public sealed class CheckResponse
    {
        /// <summary>Gets or sets a value indicating whether Comskip ran.</summary>
        public bool Ok { get; set; }

        /// <summary>Gets or sets the banner or error.</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>Gets or sets which ini runs start from.</summary>
        public string Ini { get; set; } = string.Empty;
    }
}
