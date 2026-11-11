using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FinPlan;

public static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new MoneyConverter() }
    };
    public static void Write<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Options));
    private sealed class MoneyConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.TokenType == JsonTokenType.String ? decimal.Parse(reader.GetString()!, CultureInfo.InvariantCulture) : reader.GetDecimal();
        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString("F2", CultureInfo.InvariantCulture));
    }
}
public sealed record Counterparty(string Id, string Name, string Registration);
public sealed record Account(string Id, string Currency, decimal Opening);
public sealed class Obligation
{
    public string Id { get; set; } = "";
    public string? Contract { get; set; }
    public string? Invoice { get; set; }
    public string? Act { get; set; }
    public string? ContractId { get; set; }
    public string? InvoiceId { get; set; }
    public string? ActId { get; set; }
    public string CounterpartyId { get; set; } = "";
    public string Counterparty { get; set; } = "";
    public string Currency { get; set; } = "";
    public string Account { get; set; } = "";
    public string Direction { get; set; } = "";
    public decimal Amount { get; set; }
    public DateOnly Due { get; set; }
    public DateOnly Expected { get; set; }
    public DateOnly ScheduledDate { get; set; }
    public DateOnly? RecognizedDate { get; set; }
    public string? PnlCategory { get; set; }
    public List<string> Sources { get; set; } = [];
    public string Kind { get; set; } = "document";
    public string? Purpose { get; set; }
    public decimal Paid { get; set; }
    public decimal Outstanding { get; set; }
    public string Status { get; set; } = "in_progress";
    public bool Overdue { get; set; }
}
public sealed record Operation(string Id, DateOnly Date, string Account, string Currency, string CounterpartyId, string Counterparty, string Direction, decimal Amount, string? Obligation, string Purpose, string CashCategory)
{
    public string Source { get; set; } = "";
    public int? Row { get; set; }
}
public sealed class Document
{
    public FinancialDocument? Extraction { get; set; }
    public string Path { get; set; } = "";
    public string Type { get; set; } = "";
    public List<string> Obligations { get; set; } = [];
    public List<string> DocumentKey { get; set; } = [];
    public string Sha256 { get; set; } = "";
    public int? OperationRows { get; set; }
}
public sealed record Accrual(string Id, DateOnly RecognizedDate, string Category, decimal Amount, string Currency, string Counterparty, string Obligation, string Source);
public sealed record Balance(decimal Opening, decimal In, decimal Out, decimal Closing);
public sealed record CalendarDay(DateOnly Date, Dictionary<string, Balance> Currencies, decimal TotalRub);
public sealed class DuplicateEntry
{
    public string? Id { get; set; }
    public string Source { get; set; } = "";
    public int? Row { get; set; }
    public string OriginalSource { get; set; } = "";
    public int? OriginalRow { get; set; }
    public string Reason { get; set; } = "";
}
public sealed class DuplicateReport
{
    public List<DuplicateEntry> Documents { get; set; } = [];
    public List<DuplicateEntry> Operations { get; set; } = [];
    public List<DuplicateEntry> Plans { get; set; } = [];
    public List<DuplicateEntry> Conflicts { get; set; } = [];
    public Dictionary<string, int> Summary { get; set; } = [];
}
public sealed record Reports(DateOnly Start, DateOnly EndExclusive, Dictionary<string, Dictionary<string, decimal>> Currencies);
public sealed class Dataset
{
    public CorpusSummary? Corpus { get; set; }
    public IngestionSummary? Ingestion { get; set; }
    public VolumeSummary? Volume { get; set; }
    public int Version { get; set; } = 6;
    public int Seed { get; set; }
    public string Company { get; set; } = "ООО «Демо Финансы» (вымышленная)";
    public DateOnly OpeningDate { get; set; } = new(2026, 10, 1);
    public DateOnly Cutoff { get; set; } = new(2026, 10, 15);
    public DateOnly End { get; set; } = new(2026, 12, 31);
    public DateOnly RatesDate { get; set; } = new(2026, 10, 15);
    public Dictionary<string, string> Rates { get; set; } = new() { ["RUB"] = "1", ["CHF"] = "110", ["GBP"] = "125" };
    public string RateNote { get; set; } = "Условные фиксированные курсы, не рыночные. RUB за единицу валюты.";
    public List<Counterparty> Counterparties { get; set; } = [];
    public List<Account> Accounts { get; set; } = [];
    public List<Obligation> Obligations { get; set; } = [];
    public List<Operation> Operations { get; set; } = [];
    public List<Accrual> Accruals { get; set; } = [];
    public List<CalendarDay> Calendar { get; set; } = [];
    public List<Document> Documents { get; set; } = [];
    public DuplicateReport Duplicates { get; set; } = new();
    public Reports? Reports { get; set; }
}
