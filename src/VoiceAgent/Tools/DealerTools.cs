using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VoiceAgent.Tools;

/// <summary>Grounds answers in the business's own articles (retrieval-augmented generation).</summary>
public sealed class SearchKnowledgeTool(KnowledgeIndex index) : IVoiceTool
{
    public string Name => "search_knowledge";
    public string Description =>
        "Search the business's own policies and FAQ. Call this before answering any question about hours, prices, " +
        "financing, trade-ins, payments, fees or warranty. Answer only from what it returns.";
    public JsonObject Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string" } },
        ["required"] = new JsonArray("query"),
    };

    public Task<JsonNode> InvokeAsync(JsonElement args, ToolContext context, CancellationToken ct)
    {
        var hits = index.Search(args.Str("query") ?? "");
        JsonNode result = hits.Count == 0
            ? new JsonObject { ["results"] = new JsonArray(), ["note"] = "nothing on file; offer to take a message" }
            : new JsonObject
            {
                ["results"] = new JsonArray(hits.Select(h => (JsonNode)new JsonObject
                {
                    ["id"] = h.Article.Id,
                    ["title"] = h.Article.Title,
                    ["text"] = h.Article.Text,
                    ["score"] = Math.Round(h.Score, 3),
                }).ToArray()),
            };
        return Task.FromResult(result);
    }
}

/// <summary>In-memory appointment book. The seam a real DMS or calendar integration replaces.</summary>
public sealed class AppointmentBook(DemoBusiness business, TimeProvider clock)
{
    private readonly ConcurrentDictionary<(string Department, DateTime Start), string> _booked = new();

    public DateTime LocalNow => TimeZoneInfo.ConvertTime(clock.GetUtcNow(), business.Zone).DateTime;

    public IReadOnlyList<DateTime> OpenSlots(string department, DateOnly date)
    {
        if (!business.Departments.TryGetValue(department, out var hours)) return [];
        if (!hours.Days.Contains((int)date.DayOfWeek)) return [];

        var open = date.ToDateTime(TimeOnly.Parse(hours.Open, CultureInfo.InvariantCulture));
        var close = date.ToDateTime(TimeOnly.Parse(hours.Close, CultureInfo.InvariantCulture));
        var slots = new List<DateTime>();
        for (var t = open; t.AddMinutes(business.SlotMinutes) <= close; t = t.AddMinutes(business.SlotMinutes))
            if (t > LocalNow.AddMinutes(60) && !_booked.ContainsKey((department, t)))
                slots.Add(t);
        return slots;
    }

    public bool TryBook(string department, DateTime start, string name, out string eventId)
    {
        eventId = $"apt_{Guid.NewGuid():N}"[..16];
        if (!OpenSlots(department, DateOnly.FromDateTime(start)).Contains(start)) return false;
        return _booked.TryAdd((department, start), name);
    }
}

public sealed class CheckAvailabilityTool(AppointmentBook book) : IVoiceTool
{
    public string Name => "check_availability";
    public string Description => "List open appointment times for the sales or service department on a date (YYYY-MM-DD).";
    public JsonObject Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["department"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("sales", "service") },
            ["date"] = new JsonObject { ["type"] = "string", ["description"] = "YYYY-MM-DD in the business's time zone" },
        },
        ["required"] = new JsonArray("department", "date"),
    };

    public Task<JsonNode> InvokeAsync(JsonElement args, ToolContext context, CancellationToken ct)
    {
        if (!DateOnly.TryParseExact(args.Str("date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return Task.FromResult<JsonNode>(ToolRegistry.Error("date must be YYYY-MM-DD"));

        var slots = book.OpenSlots(args.Str("department")!.ToLowerInvariant(), date);
        return Task.FromResult<JsonNode>(new JsonObject
        {
            ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["open_slots"] = new JsonArray(slots.Take(8).Select(s => (JsonNode)s.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture)).ToArray()),
            ["more_available"] = slots.Count > 8,
        });
    }
}

public sealed class BookAppointmentTool(AppointmentBook book) : IVoiceTool
{
    public string Name => "book_appointment";
    public string Description =>
        "Book an appointment. Only call after the caller has agreed to a specific open time, you have read " +
        "their name and callback number back to them, and they have said yes. The tool checks the read-back " +
        "and refuses without it.";
    public JsonObject Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["department"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("sales", "service") },
            ["start"] = new JsonObject { ["type"] = "string", ["description"] = "YYYY-MM-DDTHH:mm local time, from check_availability" },
            ["name"] = new JsonObject { ["type"] = "string" },
            ["phone"] = new JsonObject { ["type"] = "string" },
            ["reason"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray("department", "start", "name", "phone"),
    };

    public Task<JsonNode> InvokeAsync(JsonElement args, ToolContext context, CancellationToken ct)
    {
        if (!DateTime.TryParseExact(args.Str("start"), "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
            return Task.FromResult<JsonNode>(ToolRegistry.Error("start must be YYYY-MM-DDTHH:mm"));

        string department = args.Str("department")!.ToLowerInvariant();
        string name = args.Str("name")!;

        // Enforced here, not in the prompt: the number was read back and the caller said yes.
        var readBack = Compliance.ReadBackGate.Check(context.Transcript, args.Str("phone")!);
        if (!readBack.Allowed)
            return Task.FromResult<JsonNode>(ToolRegistry.Error(readBack.Reason!));

        if (!book.TryBook(department, start, name, out var eventId))
            return Task.FromResult<JsonNode>(ToolRegistry.Error("that time is not open; call check_availability and offer another"));

        context.CallerName = name;
        context.Outcomes.Add($"booked {department} {start:yyyy-MM-dd HH:mm}");
        return Task.FromResult<JsonNode>(new JsonObject
        {
            ["success"] = true,
            ["eventId"] = eventId,
            ["department"] = department,
            ["start"] = start.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
        });
    }
}

/// <summary>
/// Identity check before any account detail is spoken. Account data never reaches the model
/// until this succeeds, and three misses lock verification for the rest of the call.
/// </summary>
public sealed class VerifyAccountTool(DemoBusiness business) : IVoiceTool
{
    public const int MaxAttempts = 3;

    public string Name => "verify_account";
    public string Description =>
        "Verify the caller before discussing any loan account: the last 4 digits of the account number and the " +
        "billing ZIP code. Never read account details before this returns verified=true.";
    public JsonObject Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["last4"] = new JsonObject { ["type"] = "string" },
            ["zip"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray("last4", "zip"),
    };

    public Task<JsonNode> InvokeAsync(JsonElement args, ToolContext context, CancellationToken ct)
    {
        if (context.FailedVerifications >= MaxAttempts)
            return Task.FromResult<JsonNode>(ToolRegistry.Error("verification locked for this call; offer a transfer to the servicing team"));

        string last4 = new((args.Str("last4") ?? "").Where(char.IsDigit).ToArray());
        string zip = new((args.Str("zip") ?? "").Where(char.IsDigit).ToArray());
        var account = business.Accounts.FirstOrDefault(a => a.Last4 == last4 && a.Zip == zip);
        if (account is null)
        {
            context.FailedVerifications++;
            return Task.FromResult<JsonNode>(new JsonObject
            {
                ["verified"] = false,
                ["attempts_remaining"] = MaxAttempts - context.FailedVerifications,
            });
        }

        context.VerifiedAccounts.Add(account.Id);
        context.CallerName ??= account.FirstName;
        return Task.FromResult<JsonNode>(new JsonObject
        {
            ["verified"] = true,
            ["account_id"] = account.Id,
            ["first_name"] = account.FirstName,
            ["amount_due"] = account.AmountDue,
            ["due_date"] = account.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["days_past_due"] = account.DaysPastDue,
        });
    }
}

public sealed class RecordPromiseToPayTool(DemoBusiness business, AppointmentBook book) : IVoiceTool
{
    public const int MaxDaysOut = 14;

    public string Name => "record_promise_to_pay";
    public string Description =>
        "Record the caller's commitment to pay an amount by a date. Requires a verified account in this call. " +
        $"The date must be within {MaxDaysOut} days.";
    public JsonObject Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["account_id"] = new JsonObject { ["type"] = "string" },
            ["amount"] = new JsonObject { ["type"] = "number" },
            ["date"] = new JsonObject { ["type"] = "string", ["description"] = "YYYY-MM-DD" },
        },
        ["required"] = new JsonArray("account_id", "amount", "date"),
    };

    public Task<JsonNode> InvokeAsync(JsonElement args, ToolContext context, CancellationToken ct)
    {
        string accountId = args.Str("account_id")!;
        if (!context.VerifiedAccounts.Contains(accountId))
            return Task.FromResult<JsonNode>(ToolRegistry.Error("account not verified on this call; call verify_account first"));

        var account = business.Accounts.First(a => a.Id == accountId);
        var amount = args.Dec("amount");
        if (amount is null || amount <= 0 || amount > account.AmountDue)
            return Task.FromResult<JsonNode>(ToolRegistry.Error($"amount must be between 0.01 and {account.AmountDue}"));

        if (!DateOnly.TryParseExact(args.Str("date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return Task.FromResult<JsonNode>(ToolRegistry.Error("date must be YYYY-MM-DD"));

        var today = DateOnly.FromDateTime(book.LocalNow);
        if (date < today || date > today.AddDays(MaxDaysOut))
            return Task.FromResult<JsonNode>(ToolRegistry.Error($"date must be between today and {MaxDaysOut} days out"));

        context.Outcomes.Add($"promise to pay {amount:0.00} by {date:yyyy-MM-dd} on {accountId}");
        return Task.FromResult<JsonNode>(new JsonObject
        {
            ["success"] = true,
            ["confirmation"] = $"PTP-{accountId[^5..]}-{date:MMdd}",
            ["amount"] = amount,
            ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        });
    }
}
