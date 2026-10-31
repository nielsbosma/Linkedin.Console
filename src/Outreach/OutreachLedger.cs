using System.Text.Json;
using System.Text.Json.Serialization;
using Linkedin.Console.Infrastructure;
using Linkedin.Console.Storage;

namespace Linkedin.Console.Outreach;

public enum PersonState { Waiting, MarkedSent, Skipped, Never }

public sealed class OutreachPerson
{
    /// <summary>The handle in lower case, which is what identifies a person across links.</summary>
    public required string Key { get; set; }

    /// <summary>Keeps the handle's case, as the obfuscated <c>ACoAA…</c> ones need it.</summary>
    public required string Url { get; set; }

    public string? FirstName { get; set; }
    public string? Note { get; set; }
    public string? Followup { get; set; }
    public PersonState State { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset? OpenedAt { get; set; }

    /// <summary>Stays when a person later becomes <see cref="PersonState.Never"/>: the invitation still went out.</summary>
    public DateTimeOffset? MarkedSentAt { get; set; }
}

public sealed class OutreachData
{
    public int Version { get; set; } = 1;
    public DateTimeOffset? PausedUntil { get; set; }
    public List<OutreachPerson> People { get; set; } = [];

    public OutreachPerson? Find(string key) => People.FirstOrDefault(p => p.Key == key);
}

/// <summary>
/// Who is on the list and what has happened to them, in one JSON file. It holds names and messages,
/// so it is private like an export. The lock is taken per change and never while waiting for the
/// user, so <c>outreach add</c> still works during an <c>outreach next</c> session.
/// </summary>
public sealed class OutreachLedger(string dir)
{
    public const string FileName = "outreach.json";

    private static readonly JsonSerializerOptions Options = new(OutputHelper.JsonOptions)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private string FilePath => Path.Combine(dir, FileName);

    public async Task<OutreachData> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(FilePath)) return new OutreachData();

        using var _ = await FileLock.AcquireAsync(Path.Combine(dir, ".lock"), ct);
        return Load();
    }

    public async Task<T> UpdateAsync<T>(Func<OutreachData, T> change, CancellationToken ct)
    {
        ConfigStore.EnsurePrivateDir(dir);
        using var _ = await FileLock.AcquireAsync(Path.Combine(dir, ".lock"), ct);

        var data = Load();
        var result = change(data);
        ConfigStore.AtomicWrite(FilePath, JsonSerializer.Serialize(data, Options));
        return result;
    }

    private OutreachData Load()
    {
        if (!File.Exists(FilePath)) return new OutreachData();

        try
        {
            return JsonSerializer.Deserialize<OutreachData>(File.ReadAllText(FilePath), Options) ?? new OutreachData();
        }
        catch (JsonException ex)
        {
            // Never start over from an empty list: that would offer people who were already contacted.
            throw new LinkedInException(ErrorCode.Error, $"{FilePath} could not be read, so nothing was changed.", ex.Message);
        }
    }

    public static string StateName(PersonState state) => JsonNamingPolicy.CamelCase.ConvertName(state.ToString());
}

public sealed record CapStatus(int Today, int LastSevenDays, DateTimeOffset? NextWeeklySlot)
{
    public bool DayFull => Today >= OutreachCaps.Daily;
    public bool WeekFull => LastSevenDays >= OutreachCaps.Weekly;
}

/// <summary>
/// Set under the invitation limits people report, which are vendor figures, not LinkedIn's. A day is
/// the local calendar day, and a week is the seven days just gone. Fixed rather than options on
/// purpose: a limit that takes a flag to raise is raised on the first busy day.
/// </summary>
public static class OutreachCaps
{
    public const int Daily = 15;
    public const int Weekly = 80;

    public static CapStatus Count(OutreachData data, DateTimeOffset now)
    {
        var sent = data.People.Where(p => p.MarkedSentAt is not null).Select(p => p.MarkedSentAt!.Value).ToList();
        var today = now.ToLocalTime().Date;
        var week = sent.Where(at => at > now.AddDays(-7)).OrderBy(at => at).ToList();

        return new CapStatus(
            sent.Count(at => at.ToLocalTime().Date == today),
            week.Count,
            week.Count >= Weekly ? week[week.Count - Weekly].AddDays(7) : null);
    }
}
