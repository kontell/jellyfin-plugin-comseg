using System;
using System.Text.Json.Serialization;
using System.Threading;

namespace Jellyfin.Plugin.Comseg.Catchup;

/// <summary>A programme to save from the archive, as a client's guide describes it.</summary>
public sealed class CatchupRequest
{
    /// <summary>Gets or sets the channel name the client shows.</summary>
    public string? ChannelName { get; set; }

    /// <summary>Gets or sets the channel number the client shows.</summary>
    public string? ChannelNumber { get; set; }

    /// <summary>Gets or sets when to start, UTC (the programme's start less any padding).</summary>
    public DateTime StartUtc { get; set; }

    /// <summary>Gets or sets when to stop, UTC.</summary>
    public DateTime EndUtc { get; set; }

    /// <summary>Gets or sets the programme's title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the programme's description.</summary>
    public string? Plot { get; set; }

    /// <summary>Gets or sets an http(s) URL of the programme's image, for the poster.</summary>
    public string? ImageUrl { get; set; }
}

/// <summary>Where a catchup job is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CatchupState>))]
public enum CatchupState
{
    /// <summary>Waiting its turn.</summary>
    Queued,

    /// <summary>Waiting for the IPTV connection: Jellyfin is recording or showing live TV.</summary>
    Waiting,

    /// <summary>Copying from the archive.</summary>
    Downloading,

    /// <summary>Writing the nfo and poster and waiting for the library.</summary>
    Finishing,

    /// <summary>Running the Comskip pass.</summary>
    Analysing,

    /// <summary>Saved.</summary>
    Done,

    /// <summary>Stopped by a problem.</summary>
    Failed,

    /// <summary>Cancelled by a client.</summary>
    Cancelled,
}

/// <summary>One catchup download.</summary>
public sealed class CatchupJob
{
    /// <summary>Initializes a new instance of the <see cref="CatchupJob"/> class.</summary>
    /// <param name="request">The request.</param>
    /// <param name="channel">The channel's name in the tuner playlist.</param>
    /// <param name="requestedBy">Who asked.</param>
    public CatchupJob(CatchupRequest request, string channel, string requestedBy)
    {
        Request = request;
        Channel = channel;
        RequestedBy = requestedBy;
        CreatedUtc = UpdatedUtc = DateTime.UtcNow;
    }

    /// <summary>Gets the job's id.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Gets the request.</summary>
    public CatchupRequest Request { get; }

    /// <summary>Gets the channel's name in the tuner playlist.</summary>
    public string Channel { get; }

    /// <summary>Gets who asked.</summary>
    public string RequestedBy { get; }

    /// <summary>Gets when the job was made.</summary>
    public DateTime CreatedUtc { get; }

    /// <summary>Gets or sets when the job last changed.</summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>Gets or sets where the job is.</summary>
    public CatchupState State { get; set; } = CatchupState.Queued;

    /// <summary>Gets or sets what the job is waiting for, or what went wrong, or the result.</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets how much of the download is done, 0-100.</summary>
    public int Percent { get; set; }

    /// <summary>Gets or sets the saved file.</summary>
    public string? Path { get; set; }

    /// <summary>Gets or sets the library item, once it exists.</summary>
    public Guid? ItemId { get; set; }

    /// <summary>Gets the job's cancellation.</summary>
    [JsonIgnore]
    public CancellationTokenSource Cancel { get; } = new();

    /// <summary>Gets a value indicating whether the job has ended.</summary>
    public bool Ended => State is CatchupState.Done or CatchupState.Failed or CatchupState.Cancelled;

    /// <summary>Moves the job on.</summary>
    /// <param name="state">The new state.</param>
    /// <param name="message">The message, or null to clear it.</param>
    public void Set(CatchupState state, string? message = null)
    {
        State = state;
        Message = message;
        UpdatedUtc = DateTime.UtcNow;
    }
}
