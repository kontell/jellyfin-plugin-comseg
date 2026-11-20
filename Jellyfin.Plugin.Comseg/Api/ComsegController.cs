using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Comseg.Comskip;
using Jellyfin.Plugin.Comseg.Live;
using Jellyfin.Plugin.Comseg.Profiles;
using Jellyfin.Plugin.Comseg.Segments;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

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
    private readonly OnDemandDetection _detection;
    private readonly ILibraryManager _library;
    private readonly RecordingRecords _records;

    /// <summary>Initializes a new instance of the <see cref="ComsegController"/> class.</summary>
    /// <param name="watcher">The watcher.</param>
    /// <param name="runner">The Comskip runner.</param>
    /// <param name="detection">The on-demand full pass.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="records">The recording records.</param>
    public ComsegController(
        RecordingWatcher watcher,
        ComskipRunner runner,
        OnDemandDetection detection,
        ILibraryManager library,
        RecordingRecords records)
    {
        _watcher = watcher;
        _runner = runner;
        _detection = detection;
        _library = library;
        _records = records;
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
            CopyHeight = j.Copy.Height,
            Channel = j.Settings.Record?.ChannelName,
            Profile = j.Settings.ProfileName,
            LiveDetection = j.AnalysesLive,
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
        var profiles = config.Profiles ?? [];
        var problems = ProfileRules.Check(profiles);
        var summary = profiles.Length == 0
            ? "Profiles: none; every recording runs with the settings above."
            : problems.Count == 0
                ? "Profiles: " + profiles.Length + ", no problems."
                : "Profiles: " + string.Join("; ", problems) + ".";
        return new CheckResponse { Ok = ok, Text = text, Ini = ComskipIni.Describe(config.IniPath), Profiles = summary };
    }

    /// <summary>A recording's channel and the settings its passes run with.</summary>
    /// <param name="itemId">The item.</param>
    /// <returns>The settings.</returns>
    [HttpGet("Recording/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<RecordingResponse> GetRecording([FromRoute] Guid itemId)
    {
        var item = _library.GetItemById(itemId);
        if (item is null || string.IsNullOrEmpty(item.Path))
        {
            return NotFound();
        }

        return Describe(item.Id, item.Path);
    }

    /// <summary>
    /// Sets the channel a recording came from, for one the plugin did not see
    /// record (an older recording, or one in an extra folder); an empty name
    /// forgets it. Later passes use the matching profile; Detect runs one now.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="request">The channel.</param>
    /// <returns>The settings the recording now runs with.</returns>
    [HttpPost("Recording/{itemId}/Channel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public ActionResult<RecordingResponse> SetChannel([FromRoute] Guid itemId, [FromBody] ChannelRequest request)
    {
        var item = _library.GetItemById(itemId);
        if (item is null || string.IsNullOrEmpty(item.Path))
        {
            return NotFound();
        }

        var old = _records.Get(item.Path);
        var written = string.IsNullOrWhiteSpace(request.ChannelName)
            ? _records.Delete(item.Path)
            : _records.Save(new RecordingRecord(item.Path, request.ChannelName.Trim(), null, old?.Programme ?? item.Name, old?.StartUtc, "admin"));
        if (!written)
        {
            return Problem("the recording's record could not be written; see the server log", statusCode: StatusCodes.Status500InternalServerError);
        }

        return Describe(item.Id, item.Path);
    }

    private RecordingResponse Describe(Guid itemId, string path)
    {
        var settings = ProfileRules.Resolve(ComsegPlugin.Config, _records.Get(path));
        return new RecordingResponse
        {
            ItemId = itemId,
            Path = path,
            Channel = settings.Record?.ChannelName,
            ChannelSource = settings.Record?.Source,
            Profile = settings.ProfileName,
            IniKeys = settings.Keys,
            AnalysisHeight = ScaledCopy.Height(settings.Config),
            LiveDetection = settings.LiveDetection,
            KeepFrames = settings.KeepFrames,
            Problems = settings.Problems,
        };
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

        if (_detection.TryRun(item, "on-demand", CancellationToken.None) is null)
        {
            return Conflict("a detection is already running for this item");
        }

        return Accepted();
    }

    /// <summary>A job's problems for the status page: storing the breaks first, then Comskip, then the scaled copy.</summary>
    private static string? Problems(LiveJob job)
    {
        var problems = new[] { job.StoreError, job.LastError, job.Copy.Problem }.Where(p => p is not null).ToList();
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

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

        /// <summary>Gets or sets the recording's channel, as its timer named it.</summary>
        public string? Channel { get; set; }

        /// <summary>Gets or sets the channel profile the recording runs with, or null for the plugin's own settings.</summary>
        public string? Profile { get; set; }

        /// <summary>Gets or sets a value indicating whether a live Comskip analyses the recording; its profile can turn that off.</summary>
        public bool LiveDetection { get; set; }
    }

    /// <summary>A recording's channel and settings.</summary>
    public sealed class RecordingResponse
    {
        /// <summary>Gets or sets the item.</summary>
        public Guid ItemId { get; set; }

        /// <summary>Gets or sets the recording file.</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>Gets or sets the channel, or null when the plugin does not know it.</summary>
        public string? Channel { get; set; }

        /// <summary>Gets or sets who set the channel: "timer", "catchup" or "admin".</summary>
        public string? ChannelSource { get; set; }

        /// <summary>Gets or sets the profile that applies, or null for the plugin's own settings.</summary>
        public string? Profile { get; set; }

        /// <summary>Gets or sets the profile's ini keys that are used.</summary>
        public IReadOnlyDictionary<string, string> IniKeys { get; set; } = new Dictionary<string, string>();

        /// <summary>Gets or sets the analysis height: 0 analyses the recording as it is.</summary>
        public int AnalysisHeight { get; set; }

        /// <summary>Gets or sets a value indicating whether the recording would be analysed while it records.</summary>
        public bool LiveDetection { get; set; }

        /// <summary>Gets or sets a value indicating whether Comskip's per-frame data is kept after a full pass.</summary>
        public bool KeepFrames { get; set; }

        /// <summary>Gets or sets the profile's keys that were left out, and why.</summary>
        public IReadOnlyList<string> Problems { get; set; } = Array.Empty<string>();
    }

    /// <summary>The channel to give a recording.</summary>
    public sealed class ChannelRequest
    {
        /// <summary>Gets or sets the channel's name in Jellyfin; empty forgets the recording's channel.</summary>
        public string? ChannelName { get; set; }
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

        /// <summary>Gets or sets what is wrong with the channel profiles, or how many there are.</summary>
        public string Profiles { get; set; } = string.Empty;
    }
}
