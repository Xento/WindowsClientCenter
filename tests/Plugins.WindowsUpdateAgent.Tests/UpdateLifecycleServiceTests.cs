using System.Globalization;
using WindowsClientCenter.Plugins.WindowsUpdateAgent.Models.UsoStore;
using WindowsClientCenter.Plugins.WindowsUpdateAgent.Services.UsoStore;
using Xunit;

namespace WindowsClientCenter.Tests.Plugins.WindowsUpdateAgent;

public sealed class UpdateLifecycleServiceTests
{
    [Fact]
    public void Build_ResolvesTitleFromCompletedUpdatesTitle()
    {
        var records = Build(
            completedUpdates:
            [
                CompletedUpdate("Provider", "Update-1", title: "Security Update KB5000001")
            ]);

        var record = Assert.Single(records);
        Assert.Equal("Security Update KB5000001", record.Title);
        Assert.Equal("COMPLETEDUPDATES.Title", record.ResolvedTitleSource);
    }

    [Fact]
    public void Build_FallsBackToUpdateIdWhenNoTitleExists()
    {
        var records = Build(
            updateProperties:
            [
                UpdateProperty("Provider", "Update-2", "DiscoveryTime", UnixMilliseconds(new DateTimeOffset(2026, 4, 1, 10, 0, 0, TimeSpan.Zero)), 3)
            ]);

        var record = Assert.Single(records);
        Assert.Equal("Update-2", record.Title);
        Assert.Equal("UpdateId fallback", record.ResolvedTitleSource);
    }

    [Fact]
    public void Build_ConvertsType3InstallDeadlineToLocalDateTime()
    {
        var deadline = new DateTimeOffset(2026, 4, 10, 18, 30, 0, TimeSpan.Zero);

        var records = Build(
            updateProperties:
            [
                UpdateProperty("Provider", "Update-3", "InstallDeadline", UnixMilliseconds(deadline), 3)
            ]);

        var record = Assert.Single(records);
        Assert.Equal(deadline.ToLocalTime().DateTime, record.ProbableInstallDeadlineLocal);
        Assert.Equal("High (explicit per-update deadline)", record.DeadlineConfidenceText);
    }

    [Fact]
    public void Build_RecognizesExplicitRebootDeadline()
    {
        var deadline = new DateTimeOffset(2026, 4, 11, 8, 15, 0, TimeSpan.Zero);

        var records = Build(
            updateProperties:
            [
                UpdateProperty("Provider", "Update-4", "RebootDeadlineTime", UnixMilliseconds(deadline), 3)
            ]);

        var record = Assert.Single(records);
        Assert.Equal(deadline.ToLocalTime().DateTime, record.ProbableRebootDeadlineLocal);
        Assert.Contains("UPDATESPROP.RebootDeadlineTime", record.DeadlineExplanation);
    }

    [Fact]
    public void Build_TreatsGenericDeadlineAsRebootDeadlineWhenReadyToReboot()
    {
        var deadline = new DateTimeOffset(2026, 4, 12, 9, 0, 0, TimeSpan.Zero);

        var records = Build(
            updateProperties:
            [
                UpdateProperty("Provider", "Update-5", "Deadline", UnixMilliseconds(deadline), 3),
                UpdateProperty("Provider", "Update-5", "UpdateBlock", "ReadyToReboot", 4)
            ]);

        var record = Assert.Single(records);
        Assert.Null(record.ProbableInstallDeadlineLocal);
        Assert.Equal(deadline.ToLocalTime().DateTime, record.ProbableRebootDeadlineLocal);
        Assert.Contains("generic Deadline value is interpreted as a probable reboot deadline", record.DeadlineExplanation);
    }

    [Fact]
    public void Build_KeepsRawUpdatePropertiesJson()
    {
        var records = Build(
            updateProperties:
            [
                UpdateProperty("Provider", "Update-6", "LastUpdateBlock", "ReadyToReboot", 4)
            ]);

        var record = Assert.Single(records);
        Assert.Contains("\"Variable\": \"LastUpdateBlock\"", record.RawUpdatePropertiesJson);
        Assert.Contains("\"Value\": \"ReadyToReboot\"", record.RawUpdatePropertiesJson);
    }

    [Fact]
    public void Build_FormatsUpdatesPropOperationalValuesFromStoreSample()
    {
        var discoveryTime = new DateTimeOffset(2026, 4, 13, 7, 0, 0, TimeSpan.Zero);
        var attemptedTime = new DateTimeOffset(2026, 4, 13, 7, 5, 0, TimeSpan.Zero);
        var delayTime = new DateTimeOffset(2026, 4, 13, 7, 30, 0, TimeSpan.Zero);
        var deadline = new DateTimeOffset(2026, 4, 13, 8, 0, 0, TimeSpan.Zero);

        var records = Build(
            updateProperties:
            [
                UpdateProperty("WuProvider", "Update-7", "QueueNumber", "2", 2),
                UpdateProperty("WuProvider", "Update-7", "DiscoveryTime", UnixMilliseconds(discoveryTime), 3),
                UpdateProperty("WuProvider", "Update-7", "UpdateAttempted", UnixMilliseconds(attemptedTime), 3),
                UpdateProperty("WuProvider", "Update-7", "DownloadSize", "58453024", 3),
                UpdateProperty("WuProvider", "Update-7", "isIpu", "0", 0),
                UpdateProperty("WuProvider", "Update-7", "WorkBit", "0", 0),
                UpdateProperty("WuProvider", "Update-7", "UpdateBlock", "UserEngaged", 4),
                UpdateProperty("WuProvider", "Update-7", "LastUpdateBlock", "UserEngaged", 4),
                UpdateProperty("WuProvider", "Update-7", "UpdateActionDelayCount", "4", 2),
                UpdateProperty("WuProvider", "Update-7", "UpdateActionDelayTime", UnixMilliseconds(delayTime), 3),
                UpdateProperty("WuProvider", "Update-7", "Deadline", UnixMilliseconds(deadline), 3),
                UpdateProperty("WuProvider", "Update-7", "ActionTags", "USR@2#3b8,LGDL@15b#165,USR@165#3b8", 4)
            ]);

        var record = Assert.Single(records);
        var expectedSize = string.Create(CultureInfo.CurrentCulture, $"{58453024d / 1024d / 1024d:0.##} MB");
        Assert.Equal(2, record.QueueNumber);
        Assert.Equal("2", record.QueueNumberDisplay);
        Assert.Equal(expectedSize, record.DownloadSizeDisplay);
        Assert.Equal("No", record.IsIpuDisplay);
        Assert.Equal("No", record.WorkBitDisplay);
        Assert.Equal("User engaged, update action delayed", record.UpdateBlockSummary);
        Assert.Equal("4", record.UpdateActionDelayCount);
        Assert.Equal(delayTime.ToLocalTime().DateTime, record.UpdateActionDelayTimeLocal);
        Assert.Contains("User engaged, update action delayed", record.SchedulingSummary);
        Assert.Contains("4 deferral(s)", record.SchedulingSummary);
        Assert.Contains("5 minute(s) after discovery", record.SchedulingDetails);
        Assert.Contains("before the probable install deadline", record.SchedulingDetails);
        Assert.Equal("3 tag(s), LGDL: 1, USR: 2", record.ActionTagsSummary);
        Assert.Contains($"DownloadSize: {expectedSize}", record.ImportantUpdateProperties);
        Assert.Contains($"\"ParsedValue\": \"{expectedSize}\"", record.RawUpdatePropertiesJson);
    }

    private static IReadOnlyList<UpdateLifecycleRecord> Build(
        IReadOnlyList<UsoCompletedUpdateRecord>? completedUpdates = null,
        IReadOnlyList<UsoUpdatePropertyRecord>? updateProperties = null,
        IReadOnlyList<UsoActionRecord>? actionRecords = null)
    {
        var service = new UpdateLifecycleService(new TimestampParser());
        return service.Build(completedUpdates ?? [], updateProperties ?? [], actionRecords ?? []);
    }

    private static UsoCompletedUpdateRecord CompletedUpdate(
        string providerId,
        string updateId,
        string title = "",
        string metadata = "")
    {
        return new UsoCompletedUpdateRecord
        {
            ProviderId = providerId,
            UpdateId = updateId,
            TimeRaw = UnixMilliseconds(new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero)),
            Title = title,
            Description = string.Empty,
            MoreInfoUrl = string.Empty,
            HistoryCategory = string.Empty,
            Uninstall = null,
            WasRebootRequired = null,
            ForOs = null,
            Metadata = metadata
        };
    }

    private static UsoUpdatePropertyRecord UpdateProperty(string providerId, string updateId, string variable, string value, int type)
    {
        return new UsoUpdatePropertyRecord
        {
            ProviderId = providerId,
            UpdateId = updateId,
            Variable = variable,
            Value = value,
            Type = type
        };
    }

    private static string UnixMilliseconds(DateTimeOffset value)
    {
        return value.ToUnixTimeMilliseconds().ToString();
    }
}
