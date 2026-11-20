using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Comseg.Audio;
using Jellyfin.Plugin.Comseg.Edl;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Comseg.Tests;

public class AudioDecisionTests
{
    private static readonly Break[] StoredBreaks = [new(1000, 1200)];

    private static AudioEvidence Evidence(IEnumerable<AudioSpan> spans, IEnumerable<AudioEdge>? edges = null)
    {
        var evidence = new AudioEvidence();
        evidence.Add(spans, edges ?? []);
        return evidence;
    }

    private static AudioSpan Advert(double start, double end) => new(start, end, AudioKind.Advert);

    [Fact]
    public void WhereAMatchFellInTheStoredRecordingSaysWhatItIs()
    {
        // Inside a stored break, 3 s after its start: an advert, and the break started 3 s before it.
        var (span, edges) = AudioLabels.Label(500, 1003, 10, StoredBreaks);
        Assert.Equal(Advert(500, 510), span);
        Assert.Equal([new AudioEdge(497, true)], edges);

        // Across the stored break's start: the edge sits at the same place in the match.
        (span, edges) = AudioLabels.Label(500, 995, 20, StoredBreaks);
        Assert.Null(span);
        Assert.Equal([new AudioEdge(505, true)], edges);

        // Ending just before the stored break's end: an advert, and the break ends 4 s after it.
        (span, edges) = AudioLabels.Label(800, 1176, 20, StoredBreaks);
        Assert.Equal(Advert(800, 820), span);
        Assert.Equal([new AudioEdge(824, false)], edges);

        // Outside: programme audio.
        (span, edges) = AudioLabels.Label(100, 300, 30, StoredBreaks);
        Assert.Equal(new AudioSpan(100, 130, AudioKind.Programme), span);
        Assert.Empty(edges);
    }

    [Fact]
    public void AudioHeardAsBothIsTrustedAsNeither()
    {
        var evidence = Evidence([Advert(100, 130), Advert(125, 160), new AudioSpan(140, 150, AudioKind.Programme)]);
        Assert.Equal([Advert(100, 140), Advert(150, 160)], evidence.Adverts);
        Assert.Empty(evidence.Programme);
    }

    [Fact]
    public void OneAdvertAloneOpensNoBreakButTwoOrAnEdgeFirstDo()
    {
        Assert.Empty(ShadowDecision.Decide(null, null, Evidence([Advert(100, 130)]), 20));

        var two = ShadowDecision.Decide(null, null, Evidence([Advert(100, 130), Advert(170, 200)]), 20);
        Assert.Equal([new DecidedRow(new Break(100, 200), RowSource.Audio)], two);

        var edged = ShadowDecision.Decide(null, null, Evidence([Advert(100, 130)], [new AudioEdge(92, true), new AudioEdge(140, false)]), 20);
        Assert.Equal([new DecidedRow(new Break(92, 140), RowSource.Audio)], edged);
    }

    [Fact]
    public void AGrowingComskipEndReachesOnlyAsFarAsIsConfirmed()
    {
        // The end moved on by a minute since the analysis before: it is the file's end, not the break's.
        var previous = new[] { new Break(1000, 1060) };
        var latest = new[] { new Break(1000, 1120) };
        var rows = ShadowDecision.Decide(latest, previous, new AudioEvidence(), 20);
        Assert.Equal([new DecidedRow(new Break(1000, 1060), RowSource.Comskip)], rows);

        // Adverts heard later in it carry the end that far, still short of the file's end.
        rows = ShadowDecision.Decide(latest, previous, Evidence([Advert(1070, 1100)]), 20);
        Assert.Equal(new Break(1000, 1100), rows.Single().Break);

        // A settled end stands, and an implied edge near it replaces it.
        rows = ShadowDecision.Decide(latest, latest, Evidence([], [new AudioEdge(1112, false)]), 20);
        Assert.Equal(new Break(1000, 1112), rows.Single().Break);
    }

    [Fact]
    public void WhenTheChannelsAudioIsTrustedComskipAloneWaitsForTheFinalPass()
    {
        var latest = new[] { new Break(500, 600), new Break(1000, 1200) };
        var audio = Evidence([Advert(1010, 1040), Advert(1060, 1150)], [new AudioEdge(1000, true)]);
        var rows = ShadowDecision.Decide(latest, latest, audio, 20, trustAudio: true);

        // 500-600 has no audio evidence and is held back; 1000-1200 keeps the audio's start and takes Comskip's end, 50 s on.
        Assert.Equal([new DecidedRow(new Break(1000, 1200), RowSource.Audio | RowSource.Comskip)], rows);

        // An end the audio implies is not moved by Comskip.
        audio = Evidence([Advert(1010, 1040), Advert(1060, 1150)], [new AudioEdge(1000, true), new AudioEdge(1155, false)]);
        Assert.Equal(new Break(1000, 1155), ShadowDecision.Decide(latest, latest, audio, 20, trustAudio: true).Single().Break);
    }

    [Fact]
    public void ProgrammeAudioCutsARow()
    {
        var latest = new[] { new Break(1000, 1300) };
        var rows = ShadowDecision.Decide(latest, latest, Evidence([new AudioSpan(1180, 1200, AudioKind.Programme)]), 20);
        Assert.Equal([new Break(1000, 1180), new Break(1200, 1300)], rows.Select(r => r.Break));
    }

    [Fact]
    public void TheListenerHearsAStoredAdvertAsTheRecordingGrows()
    {
        // A stored recording whose break, 1000-1200 s, holds an advert from 1010 s.
        var storedItems = AudioMatchTests.Noise(Fingerprint.ItemAt(1500), 1);
        var stored = new StoredRecording(new StoredRecordingInfo("/s.ts", "ITV 1", null, DateTime.UtcNow, 1500, StoredBreaks), storedItems);

        // The recording airs that advert, 30 s of it, from 400 s.
        var live = AudioMatchTests.Noise(Fingerprint.ItemAt(900), 2);
        AudioMatchTests.Blur(storedItems.AsSpan(Fingerprint.ItemAt(1010), Fingerprint.ItemAt(30)), 2, 3).CopyTo(live, Fingerprint.ItemAt(400));

        var listener = new AudioListener([stored]);
        var heard = 0;
        foreach (var t in new[] { 300.0, 415, 430, 600 })
        {
            var to = Fingerprint.ItemAt(t);
            listener.Hear(heard, live.AsSpan(heard, to - heard));
            heard = to;
            listener.Search([]);
        }

        var advert = Assert.Single(listener.Evidence.Adverts);
        Assert.InRange(advert.Start, 399, 401);
        Assert.InRange(advert.End, 429, 431);
        // 10 s into the stored break: the recording's break started 10 s before it.
        Assert.Contains(listener.Evidence.Edges, e => e.IsStart && Math.Abs(e.At - 390) < 1.5);
    }

    [Fact]
    public void OneAdvertMatchSplitByProgrammeAudioIsStillOneMatch()
    {
        var evidence = Evidence([Advert(100, 160), new AudioSpan(125, 135, AudioKind.Programme)]);
        Assert.Equal(2, evidence.Adverts.Count);
        Assert.Equal(1, evidence.AdvertMatchesIn(100, 160));
        Assert.Empty(ShadowDecision.Decide(null, null, evidence, 20));
    }

    [Fact]
    public void ABreakNewInThisAnalysisKeepsItsEnd()
    {
        // Nothing in the analysis before matches it: it is not a growing one.
        var rows = ShadowDecision.Decide([new Break(1000, 1120)], [new Break(400, 500)], new AudioEvidence(), 20);
        Assert.Equal(new Break(1000, 1120), rows.Single().Break);
        rows = ShadowDecision.Decide([new Break(1000, 1120)], [], new AudioEvidence(), 20);
        Assert.Equal(new Break(1000, 1120), rows.Single().Break);
    }

    [Fact]
    public void ChannelsKeepApartAndOldAudioIsNotUsed()
    {
        Assert.NotEqual(AudioStore.Key("央视一套"), AudioStore.Key("央视二套"));
        Assert.Equal(AudioStore.Key("RTÉ One"), AudioStore.Key(" RTÉ One "));
        Assert.StartsWith("rte-one-", AudioStore.Key("RTÉ One"), StringComparison.Ordinal);

        var root = Path.Combine(Path.GetTempPath(), "comseg-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new AudioStore(new FakePaths(root), NullLogger<AudioStore>.Instance);
            var items = AudioMatchTests.Noise(100, 1);
            store.Add(new StoredRecordingInfo("/a.ts", "ITV 1", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, 12, []), items);
            store.Add(new StoredRecordingInfo("/b.ts", "ITV 1", DateTime.UtcNow.AddDays(-2), DateTime.UtcNow, 12, []), items);
            Assert.Equal(2, store.Count("ITV 1"));

            // b ages past 45 days with nothing new stored to prune it: it is not loaded.
            var bJson = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Single(f => File.ReadAllText(f).Contains("/b.ts", StringComparison.Ordinal));
            File.WriteAllText(bJson, File.ReadAllText(bJson).Replace(DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), DateTime.UtcNow.AddDays(-50).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
            Assert.Equal(["/a.ts"], store.Load("ITV 1").Select(r => r.Info.Path));
            Assert.Equal(1, store.Count("ITV 1"));
            Assert.Empty(store.Load("ITV 4"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
