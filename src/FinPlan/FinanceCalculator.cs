using System.Globalization;
namespace FinPlan;

public static class FinanceCalculator
{
    public static decimal Money(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
    public static readonly Dictionary<string, (string Group, string Direction)> CashCategories = new()
    {
        ["customer_receipts"] = ("operating", "in"), ["supplier_payments"] = ("operating", "out"),
        ["operating_payments"] = ("operating", "out"), ["equipment_purchase"] = ("investing", "out"),
        ["loan_received"] = ("financing", "in"), ["loan_repaid"] = ("financing", "out")
    };
    public static readonly Dictionary<string, string> PnlCategories = new()
    {
        ["revenue"] = "Выручка по оказанным услугам", ["cost_of_sales"] = "Себестоимость оказанных услуг", ["operating_expenses"] = "Операционные расходы"
    };
    public static void Calculate(Dataset data)
    {
        var balances = data.Rates.Keys.ToDictionary(c => c, c => data.Accounts.Where(a => a.Currency == c).Sum(a => a.Opening));
        var paid = data.Obligations.ToDictionary(o => o.Id, _ => 0m);
        foreach (var t in data.Operations.Where(t => data.OpeningDate <= t.Date && t.Date < data.Cutoff))
        {
            balances[t.Currency] += t.Direction == "in" ? t.Amount : -t.Amount;
            if (t.Obligation is not null) paid[t.Obligation] += t.Amount;
        }
        foreach (var t in data.Corpus?.Cash ?? [])
            if (data.OpeningDate <= t.Date && t.Date < data.Cutoff) balances[t.Currency] += t.Direction == "in" ? t.Amount : -t.Amount;
        foreach (var o in data.Obligations)
        {
            o.Paid = paid[o.Id]; o.Outstanding = Money(o.Amount - o.Paid);
            if (o.Outstanding < 0) throw new InvalidDataException("Распределение превышает сумму обязательства.");
            o.Overdue = o.Outstanding > 0 && o.Due < data.Cutoff;
            o.Status = o.Outstanding == 0 ? "paid" : o.Overdue ? "overdue" : "in_progress";
            o.Expected = o.ScheduledDate < data.Cutoff ? data.Cutoff : o.ScheduledDate;
        }
        data.Calendar.Clear();
        for (var day = data.Cutoff; day <= data.End; day = day.AddDays(1))
        {
            var rows = new Dictionary<string, Balance>();
            foreach (var c in data.Rates.Keys)
            {
                var items = data.Obligations.Where(o => o.Currency == c && o.Expected == day).ToList();
                var incoming = items.Where(o => o.Direction == "in").Sum(o => o.Outstanding);
                var outgoing = items.Where(o => o.Direction == "out").Sum(o => o.Outstanding);
                foreach (var bucket in data.Corpus?.Buckets ?? [])
                    if (bucket.Currency == c && (bucket.ScheduledDate < data.Cutoff ? data.Cutoff : bucket.ScheduledDate) == day)
                    {
                        var remaining = bucket.Outstanding(data.OpeningDate, data.Cutoff);
                        if (bucket.Direction == "in") incoming += remaining; else outgoing += remaining;
                    }
                var opening = balances[c]; balances[c] += incoming - outgoing;
                rows[c] = new(opening, incoming, outgoing, balances[c]);
            }
            data.Calendar.Add(new(day, rows, Money(rows.Sum(kv => kv.Value.Closing * decimal.Parse(data.Rates[kv.Key], CultureInfo.InvariantCulture)))));
        }
        data.Reports = BuildReports(data);
    }
    public static Reports BuildReports(Dataset data)
    {
        var currencies = new Dictionary<string, Dictionary<string, decimal>>();
        var accrualIds = new HashSet<string>();
        foreach (var entry in data.Accruals)
            if (!accrualIds.Add(entry.Id)) throw new InvalidDataException("Повтор записи начисления.");
        foreach (var c in data.Rates.Keys)
        {
            var cash = CashCategories.Keys.ToDictionary(k => k, _ => 0m);
            foreach (var t in data.Operations.Where(t => t.Currency == c && data.OpeningDate <= t.Date && t.Date < data.Cutoff && t.CashCategory != "internal_transfer"))
            {
                if (!CashCategories.TryGetValue(t.CashCategory, out var definition) || definition.Direction != t.Direction) throw new InvalidDataException("Неизвестная статья ДДС или неверное направление.");
                cash[t.CashCategory] += t.Amount;
            }
            var pnl = PnlCategories.Keys.ToDictionary(k => k, _ => 0m);
            foreach (var t in data.Corpus?.Cash ?? [])
                if (t.Currency == c && data.OpeningDate <= t.Date && t.Date < data.Cutoff) cash[t.CashCategory] += t.Amount;
            foreach (var entry in data.Corpus?.Pnl ?? [])
                if (entry.Currency == c && data.OpeningDate <= entry.RecognizedDate && entry.RecognizedDate < data.Cutoff) pnl[entry.Category] += entry.Amount;
            foreach (var entry in data.Accruals.Where(a => a.Currency == c && data.OpeningDate <= a.RecognizedDate && a.RecognizedDate < data.Cutoff))
            {
                if (!pnl.ContainsKey(entry.Category)) throw new InvalidDataException("Неизвестная статья ОПиУ.");
                pnl[entry.Category] += entry.Amount;
            }
            var values = new Dictionary<string, decimal>(cash);
            foreach (var (key, value) in pnl) values[key] = value;
            values["opening"] = data.Accounts.Where(a => a.Currency == c).Sum(a => a.Opening);
            values["cash_in"] = cash.Where(kv => CashCategories[kv.Key].Direction == "in").Sum(kv => kv.Value);
            values["cash_out"] = cash.Where(kv => CashCategories[kv.Key].Direction == "out").Sum(kv => kv.Value);
            foreach (var group in new[] { "operating", "investing", "financing" })
                values[group + "_flow"] = cash.Where(kv => CashCategories[kv.Key].Group == group).Sum(kv => kv.Value * (CashCategories[kv.Key].Direction == "in" ? 1 : -1));
            values["net_flow"] = values["cash_in"] - values["cash_out"];
            values["closing"] = values["opening"] + values["net_flow"];
            values["gross_profit"] = pnl["revenue"] - pnl["cost_of_sales"];
            values["operating_profit"] = values["gross_profit"] - pnl["operating_expenses"];
            currencies[c] = values;
        }
        currencies["TOTAL"] = currencies.First().Value.Keys.ToDictionary(k => k, k => Money(data.Rates.Sum(kv => currencies[kv.Key][k] * decimal.Parse(kv.Value, CultureInfo.InvariantCulture))));
        return new(data.OpeningDate, data.Cutoff, currencies);
    }
}
