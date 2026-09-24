using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceAgent.Tools;

public sealed record DepartmentHours(string Open, string Close, int[] Days);

public sealed record KnowledgeArticle(string Id, string Title, string Text);

public sealed record DemoAccount(
    string Id, string Last4, string Zip, string FirstName, decimal AmountDue, DateOnly DueDate, int DaysPastDue);

/// <summary>The business the agent answers for. Loaded from data/demo-dealer.json, never hardcoded.</summary>
public sealed record DemoBusiness(
    string BusinessName,
    string TimeZone,
    int SlotMinutes,
    Dictionary<string, DepartmentHours> Departments,
    List<KnowledgeArticle> Knowledge,
    List<DemoAccount> Accounts)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static DemoBusiness Load(string path) =>
        JsonSerializer.Deserialize<DemoBusiness>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"Could not read business data from {path}.");

    public TimeZoneInfo Zone => ResolveZone(TimeZone);

    internal static TimeZoneInfo ResolveZone(string id)
    {
        foreach (var candidate in new[] { id, "Pacific Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Utc;
    }
}
