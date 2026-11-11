using System.Globalization;
using System.Text.Json;

namespace FinPlan;

public sealed record CorpusPage(List<Obligation> Obligations, List<Operation> Operations, List<Accrual> Accruals, long? NextCursor, long? Total);

public static class CorpusApi
{
    public sealed record RecordQuery(string Kind = "obligations", long Cursor = 0, int Limit = 50, string Currency = "TOTAL", string Status = "all", string Day = "all", string Query = "", string? Cutoff = null, List<Operation>? Edits = null);
    public static void Map(WebApplication app, DemoStore store, string token)
    {
        IResult Records(string id, RecordQuery p)
        {
            try
            {
                var snapshot = store.Load(id);
                if (p.Edits is { Count: > 0 }) ApplyEdits(snapshot.Data, p.Edits);
                var date = p.Cutoff is null ? snapshot.Data.Cutoff : DateOnly.ParseExact(p.Cutoff, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (date < snapshot.Data.OpeningDate || date > snapshot.Data.End || p.Kind is not ("obligations" or "cash" or "pnl") || p.Status is not ("all" or "paid" or "overdue" or "in_progress") || p.Query.Length > 200) return Results.BadRequest();
                return Results.Json(Page(snapshot, p.Kind, Math.Max(0, p.Cursor), Math.Clamp(p.Limit, 1, 100), p.Currency, p.Status, p.Day, p.Query, date), JsonFiles.Options);
            }
            catch (Exception e) when (e is IOException or JsonException or FormatException or InvalidOperationException) { return Results.BadRequest(new { error = e.Message }); }
        }
        app.MapGet("/api/datasets/{id}/records", (string id, string? kind, long? cursor, int? limit, string? currency, string? status, string? day, string? query, string? cutoff) =>
            Records(id, new(kind ?? "obligations", cursor ?? 0, limit ?? 50, currency ?? "TOTAL", status ?? "all", day ?? "all", query ?? "", cutoff)));
        app.MapPost("/api/datasets/{id}/records", async (string id, HttpContext context) =>
        {
            if (!DocumentApi.Authorized(context, token)) return Results.StatusCode(403);
            if (context.Request.ContentLength is > 100_000) return Results.BadRequest();
            try
            {
                var request = await JsonSerializer.DeserializeAsync<RecordQuery>(context.Request.Body, JsonFiles.Options, context.RequestAborted);
                return request is null ? Results.BadRequest() : Records(id, request);
            }
            catch (JsonException) { return Results.BadRequest(); }
        });
        app.MapGet("/api/datasets/{id}/obligations/{obligation}", (string id, string obligation, string? cutoff) =>
        {
            try
            {
                var snapshot = store.Load(id); var data = snapshot.Data;
                var date = cutoff is null ? data.Cutoff : DateOnly.ParseExact(cutoff, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (!obligation.StartsWith("BO-", StringComparison.Ordinal) || !long.TryParse(obligation.AsSpan(3), out var number)) return Results.NotFound();
                var part = Index(snapshot).Parts.SingleOrDefault(p => p.FirstId <= number && number <= p.LastId);
                if (part is null) return Results.NotFound();
                var (rows, bank, _) = ReadPart(snapshot, part, date);
                var o = rows.SingleOrDefault(o => o.Id == obligation);
                return o is null ? Results.NotFound() : Results.Json(new { obligation = o, payments = bank.Where(t => t.Obligation == obligation && t.Date < date) }, JsonFiles.Options);
            }
            catch (Exception e) when (e is IOException or JsonException or FormatException) { return Results.BadRequest(new { error = e.Message }); }
        });
    }
    public static CorpusIndex Index(DemoSnapshot snapshot) => JsonSerializer.Deserialize<CorpusIndex>(File.ReadAllText(Path.Combine(snapshot.Directory, CorpusAccounting.CacheFile)), JsonFiles.Options) ?? throw new InvalidDataException("Нет индекса корпуса.");

    private static void ApplyEdits(Dataset data, List<Operation> edits)
    {
        if (edits.Count > 100) throw new InvalidDataException("Слишком много демонстрационных оплат.");
        var ids = data.Operations.Select(t => t.Id).ToHashSet();
        foreach (var t in edits)
        {
            var o = data.Obligations.SingleOrDefault(o => o.Id == t.Obligation);
            if (o is null || !t.Id.StartsWith("DEMO-PAY-", StringComparison.Ordinal) || !ids.Add(t.Id) || t.Amount <= 0 || decimal.Round(t.Amount, 2) != t.Amount || t.Date < data.OpeningDate || t.Date >= data.End || t.Currency != o.Currency || t.Account != o.Account || t.Direction != o.Direction || t.CounterpartyId != o.CounterpartyId || t.CashCategory != (o.Direction == "in" ? "customer_receipts" : "supplier_payments")) throw new InvalidDataException("Неверная демонстрационная оплата.");
            data.Operations.Add(t with { Source = "", Row = null });
            if (data.Operations.Where(p => p.Obligation == o.Id).Sum(p => p.Amount) > o.Amount) throw new InvalidDataException("Оплаты превышают сумму обязательства.");
        }
    }

    public static CorpusPage Page(DemoSnapshot snapshot, string kind, long cursor, int limit, string currency, string status, string day, string query, DateOnly cutoff)
    {
        var data = snapshot.Data; data.Cutoff = cutoff; FinanceCalculator.Calculate(data);
        var index = data.Corpus is null ? new CorpusIndex() : Index(snapshot);
        // An exact internal ID jumps directly to its part, including in a 50 GB dataset.
        if (cursor == 0 && kind == "obligations" && query.StartsWith("BO-", StringComparison.OrdinalIgnoreCase) && long.TryParse(query.AsSpan(3), out var target))
        {
            var part = index.Parts.FirstOrDefault(p => p.FirstId <= target && target <= p.LastId);
            if (part is null) return new([], [], [], null, 0);
            cursor = data.Obligations.Count + index.Parts.TakeWhile(p => p != part).Sum(p => p.Count);
        }
        var result = new CorpusPage([], [], [], null, null); long position = 0; var scannedParts = 0; var full = false;
        bool Currency(string c) => currency == "TOTAL" || c == currency;
        bool Match(string text) => text.Contains(query, StringComparison.OrdinalIgnoreCase);
        bool Obligation(Obligation o) => Currency(o.Currency) && (status == "all" || o.Status == status) && (day == "all" || o.Status != "paid" && o.Expected.ToString("yyyy-MM-dd") == day) && Match(string.Join(' ', o.Id, o.Counterparty, o.Invoice, o.Contract, o.Purpose));
        bool Cash(Operation t) => Currency(t.Currency) && data.OpeningDate <= t.Date && t.Date < cutoff && t.CashCategory != "internal_transfer" && Match(t.Id + " " + t.Counterparty + " " + t.Purpose);
        bool Pnl(Accrual a) => Currency(a.Currency) && data.OpeningDate <= a.RecognizedDate && a.RecognizedDate < cutoff && Match(a.Id + " " + a.Counterparty + " " + a.Obligation);
        int Count() => result.Obligations.Count + result.Operations.Count + result.Accruals.Count;
        void Rows(List<Obligation> os, List<Operation> ts, List<Accrual> entries)
        {
            var count = kind == "obligations" ? os.Count : kind == "cash" ? ts.Count : entries.Count;
            for (var i = 0; i < count; i++, position++)
            {
                if (position < cursor) continue;
                if (Count() == limit) { full = true; return; }
                if (kind == "obligations" && Obligation(os[i])) result.Obligations.Add(os[i]);
                else if (kind == "cash" && Cash(ts[i])) result.Operations.Add(ts[i]);
                else if (kind == "pnl" && Pnl(entries[i])) result.Accruals.Add(entries[i]);
            }
        }
        Rows(data.Obligations, data.Operations, data.Accruals);
        foreach (var part in index.Parts)
        {
            if (full) break;
            var count = kind == "obligations" || kind == "pnl" ? part.Count : part.Files.Single(f => f.Kind == "bank").Rows;
            if (position + count <= cursor) { position += count; continue; }
            if (++scannedParts > 4) { full = true; break; }
            var (os, ts, entries) = ReadPart(snapshot, part, cutoff); Rows(os, ts, entries);
        }
        long? total = null;
        if (query.Length == 0)
        {
            if (kind == "obligations")
            {
                total = data.Obligations.LongCount(Obligation);
                foreach (var b in index.Summary.Buckets.Where(b => Currency(b.Currency)))
                {
                    var paid = b.Completed.Where(p => p.Key < cutoff).Sum(p => p.Value);
                    var expected = b.ScheduledDate < cutoff ? cutoff : b.ScheduledDate;
                    if (day != "all" && expected.ToString("yyyy-MM-dd") != day) continue;
                    total += status == "paid" ? day == "all" ? paid : 0 : status == "overdue" ? b.Due < cutoff ? b.Count - paid : 0 : status == "in_progress" ? b.Due >= cutoff ? b.Count - paid : 0 : day == "all" ? b.Count : b.Count - paid;
                }
            }
            else total = kind == "cash" ? data.Operations.LongCount(Cash) + index.Summary.Cash.Where(t => Currency(t.Currency) && data.OpeningDate <= t.Date && t.Date < cutoff).Sum(t => t.Count)
                : data.Accruals.LongCount(Pnl) + index.Summary.Pnl.Where(a => Currency(a.Currency) && data.OpeningDate <= a.RecognizedDate && a.RecognizedDate < cutoff).Sum(a => a.Count);
        }
        return result with { NextCursor = full ? position : null, Total = total };
    }

    public static (List<Obligation>, List<Operation>, List<Accrual>) ReadPart(DemoSnapshot snapshot, CorpusPart part, DateOnly cutoff)
    {
        var cp = snapshot.Data.Corpus!.Counterparties.ToDictionary(c => c.Id, c => c.Name);
        string PathFor(string kind) => Path.Combine(snapshot.Directory, part.Files.Single(f => f.Kind == kind).Path);
        var bankSource = part.Files.Single(f => f.Kind == "bank").Path; var bank = new List<Operation>();
        var rows = BulkRegister.Read(PathFor("bank")); var legacy = rows.First().SequenceEqual(DuplicateChecker.BankHeader); var shift = legacy ? 1 : 0; var rowNumber = 1;
        decimal Amount(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
        DateOnly Date(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var r in rows.Skip(1)) bank.Add(new(r[0], Date(r[1]), r[2], r[3], r[4], cp[r[4]], r[5 + shift], Amount(r[6 + shift]), r[7 + shift], r[8 + shift], r[9 + shift]) { Source = bankSource, Row = ++rowNumber });
        var paid = bank.Where(t => snapshot.Data.OpeningDate <= t.Date && t.Date < cutoff).GroupBy(t => t.Obligation!).ToDictionary(g => g.Key, g => g.Sum(t => t.Amount));
        var invoices = BulkRegister.Read(PathFor("invoices")).Skip(1).ToDictionary(r => r[0], r => r[11]);
        var acts = BulkRegister.Read(PathFor("acts")).Skip(1).ToDictionary(r => r[7]);
        var contractPath = part.Files.Single(f => f.Kind == "contracts").Path;
        var contractSources = BulkRegister.Read(PathFor("contracts")).Skip(1).ToDictionary(r => r[0], _ => contractPath);
        string ContractSource(string id)
        {
            if (contractSources.TryGetValue(id, out var source)) return source;
            foreach (var previous in Index(snapshot).Parts.Where(p => p.Number < part.Number).Reverse())
            {
                var file = previous.Files.Single(f => f.Kind == "contracts");
                foreach (var row in BulkRegister.Read(Path.Combine(snapshot.Directory, file.Path)).Skip(1)) contractSources.TryAdd(row[0], file.Path);
                if (contractSources.TryGetValue(id, out source)) return source;
            }
            throw new InvalidDataException("Не найден источник договора корпуса.");
        }
        var obligations = new List<Obligation>(); var accruals = new List<Accrual>();
        foreach (var r in BulkRegister.Read(PathFor("obligations")).Skip(1))
        {
            var amount = Amount(r[11]); var p = paid.GetValueOrDefault(r[0]); var due = Date(r[14]); var scheduled = Date(r[15]); var a = acts[r[0]];
            obligations.Add(new() { Id = r[0], Invoice = r[2], Contract = r[6], Act = r[4], InvoiceId = r[1], ContractId = r[5], ActId = r[3], CounterpartyId = r[7], Counterparty = cp[r[7]], Currency = r[8], Account = r[9], Direction = r[10], Amount = amount, Paid = p, Outstanding = amount - p, Due = due, ScheduledDate = scheduled, Expected = scheduled < cutoff ? cutoff : scheduled, Status = p == amount ? "paid" : due < cutoff ? "overdue" : "in_progress", Overdue = p < amount && due < cutoff, RecognizedDate = Date(a[8]), PnlCategory = a[9], Purpose = invoices[r[1]], Sources = part.Files.Select(f => f.Kind == "contracts" ? ContractSource(r[5]) : f.Path).ToList(), Kind = "corpus" });
            accruals.Add(new(a[0] + " / " + a[1], Date(a[8]), a[9], Amount(a[11]), a[10], cp[a[6]], a[7], part.Files.Single(f => f.Kind == "acts").Path));
        }
        return (obligations, bank, accruals);
    }
}
