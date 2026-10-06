// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using JerryChart.Api;
using JerryChart.Data;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace JerryChart.Tests;

/// <summary>Verifies API response contracts using generated metadata without reflection fallback.</summary>
[TestClass]
public sealed class ApiJsonContextTests
{
    /// <summary>Verifies every statistics response and problem response has generated metadata.</summary>
    [TestMethod]
    public void SerializesEveryResponseWithGeneratedMetadata()
    {
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var activity = new WorkerActivity("archive", "archive", true, now, now, now, null);
        var status = new ProcessingStatus(now,
            new MonitorProcessingStatus(activity, now, new ArchiveReplayEstimate(120, now),
                new ArchiveReplayStatus(now.AddMinutes(-10), now.AddMinutes(-10), now.AddMinutes(-5), 7, now.AddMinutes(5)),
                new ArchiveReplayThroughput(240, 120, now.AddMinutes(-2), now)),
            new ParentUriProcessingStatus(activity, 1, 2, 3, 4, 5),
            new HandleRefreshProcessingStatus(activity with { Phase = "handle-refresh-waiting", State = "handle-refresh-waiting" },
                9, 2, now.AddMinutes(-1)));

        Assert.AreEqual("""{"totalReplies":3,"rightJerryReplies":2,"wrongJerryReplies":1}""",
            Serialize(new ReplySummary(3, 2, 1)));
        Assert.AreEqual("""{"updatedAt":null}""", Serialize(new StatisticsLastUpdated(null)));
        using JsonDocument document = JsonDocument.Parse(Serialize(status));
        Assert.AreEqual(120, document.RootElement.GetProperty("monitor")
            .GetProperty("archiveEstimate").GetProperty("remainingSeconds").GetDouble());
        Assert.AreEqual(1, document.RootElement.GetProperty("parentUriBackfill").GetProperty("pending").GetInt64());
        JsonElement replay = document.RootElement.GetProperty("monitor").GetProperty("archiveReplay");
        Assert.AreEqual(7, replay.GetProperty("consecutiveGenerationMismatches").GetInt32());
        Assert.AreEqual(now.AddMinutes(-5), replay.GetProperty("stalledSince").GetDateTimeOffset());
        Assert.AreEqual(now.AddMinutes(-10), replay.GetProperty("lastProgressAt").GetDateTimeOffset());
        Assert.AreEqual(now.AddMinutes(5), replay.GetProperty("nextRetryAt").GetDateTimeOffset());
        JsonElement throughput = document.RootElement.GetProperty("monitor").GetProperty("archiveThroughput");
        Assert.AreEqual(240L, throughput.GetProperty("deliveredEvents").GetInt64());
        Assert.AreEqual(120, throughput.GetProperty("windowSeconds").GetDouble());
        Assert.AreEqual(2, throughput.GetProperty("eventsPerSecond").GetDouble());
        Assert.AreEqual(500_000, throughput.GetProperty("microsecondsPerEvent").GetDouble());
        Assert.AreEqual(now.AddMinutes(-2), throughput.GetProperty("windowStartedAt").GetDateTimeOffset());
        Assert.AreEqual(now, throughput.GetProperty("measuredAt").GetDateTimeOffset());
        using JsonDocument legacyMonitor = JsonDocument.Parse(Serialize(new MonitorProcessingStatus(activity, null)));
        Assert.AreEqual(JsonValueKind.Null, legacyMonitor.RootElement.GetProperty("archiveThroughput").ValueKind);
        JsonElement handles = document.RootElement.GetProperty("handleRefresh");
        Assert.AreEqual(9, handles.GetProperty("pending").GetInt64());
        Assert.AreEqual(2, handles.GetProperty("due").GetInt64());
        Assert.AreEqual(now.AddMinutes(-1), handles.GetProperty("nextDueAt").GetDateTimeOffset());
        Assert.AreEqual("handle-refresh-waiting", handles.GetProperty("activity").GetProperty("phase").GetString());
        Assert.AreEqual(JsonValueKind.Null, handles.GetProperty("activity").GetProperty("finishedAt").ValueKind);

        IReadOnlyList<TopReplyAuthor> authors = [new("did:plc:example", null, 2)];
        IReadOnlyList<TopReplyPost> posts = [new("at://did:plc:example/app.bsky.feed.post/example", 2)];
        IReadOnlyList<MonthlyReplyCount> months = [new(new DateOnly(2026, 10, 1), 2)];
        Assert.Contains("\"handle\":null", Serialize(authors));
        IReadOnlyList<TopReplyAuthor> inactiveAuthors = [new("did:plc:example", null, 2, "deactivated")];
        Assert.Contains("\"accountStatus\":\"deactivated\"", Serialize(inactiveAuthors));
        Assert.Contains("\"replyCount\":2", Serialize(posts));
        Assert.AreEqual("""[{"month":"2026-10-01","replyCount":2}]""", Serialize(months));
        Assert.Contains("\"status\":500", Serialize(new ProblemDetails { Status = 500 }));
        Assert.Contains("\"errors\":", Serialize(new HttpValidationProblemDetails(
            new Dictionary<string, string[]> { ["input"] = ["Invalid input."] })));
    }

    private static string Serialize<T>(T value)
    {
        JsonTypeInfo? metadata = ApiJsonContext.Default.GetTypeInfo(typeof(T));
        Assert.IsNotNull(metadata, $"Missing generated metadata for {typeof(T)}.");

        return JsonSerializer.Serialize(value, (JsonTypeInfo<T>)metadata);
    }
}
