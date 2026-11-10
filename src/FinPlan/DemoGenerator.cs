using System.Text.Json;
using static FinPlan.DocumentFiles;
namespace FinPlan;

public sealed class DemoGenerator(string root)
{
    public static Dataset Build(int seed)
    {
        var data = new Dataset { Seed = seed, End = new(2026, 12, 31) };
        string[] names = ["Северный Контур", "Вектор Снабжение", "Маяк Сервис", "Альта Проект", "Линия Ресурс", "Точка Логистик", "Спектр Материал", "Ритм Технологии", "Орион Комплект", "Грань Консалт", "Атлас Поставка", "Нева Инжиниринг", "Фокус Медиа", "Парус Торг", "Кедр Системы", "Меридиан Строй", "Сфера Дизайн", "Исток Склад", "Оптима Транспорт", "Горизонт Связь"];
        data.Counterparties = names.Select((name, i) => new Counterparty($"CP-{i + 1:000}", $"ООО «{name} Демо»", $"DEMO-REG-{i + 1:000}")).ToList();
        var currencies = data.Rates.Keys.ToArray();
        uint state = unchecked((uint)seed) ^ 0x9e3779b9u;
        if (state == 0) state = 1;
        int Next(int low, int high) { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return low + (int)(state % (high - low + 1)); }
        decimal Amount(string currency, bool document = false)
        {
            var tier = Next(0, 99);
            var rubles = tier < 45 ? Next(document ? 2000 : 250, 20000) : tier < 80 ? Next(20000, 250000) : tier < 95 ? Next(250000, 1800000) : Next(1800000, 8000000);
            return FinanceCalculator.Money((rubles + Next(0, 99) / 100m) / decimal.Parse(data.Rates[currency], System.Globalization.CultureInfo.InvariantCulture));
        }
        for (var i = 0; i < 20; i++)
        {
            var cp = data.Counterparties[i]; var c = currencies[i % 3];
            var direction = i < 6 ? "out" : i < 9 ? "in" : Next(0, 99) < 48 ? "in" : "out";
            var recognized = i < 6 ? new DateOnly(2026, 10, Next(2, 5)) : data.OpeningDate.AddDays(Next(2, 73));
            var terms = i < 6 ? Next(1, 5) : new[] { 0, 3, 7, 14, 21, 30, 45 }[Next(0, 6)];
            var due = recognized.AddDays(terms);
            if (due >= data.End) due = data.End.AddDays(-1);
            var scheduled = due.AddDays(Next(0, 99) < 30 ? Next(-2, 9) : 0);
            if (scheduled >= data.End) scheduled = data.End.AddDays(-1);
            data.Obligations.Add(new() { Id = $"O-{i + 1:000}", Contract = $"D-{i + 1:000}", Invoice = $"S-{i + 1:000}", Act = $"ACT-{i + 1:000}", CounterpartyId = cp.Id, Counterparty = cp.Name, Currency = c, Account = "A-" + c, Direction = direction, Amount = Amount(c, true), Due = due, ScheduledDate = scheduled, Expected = scheduled < data.Cutoff ? data.Cutoff : scheduled, RecognizedDate = recognized, PnlCategory = direction == "in" ? "revenue" : i % 3 == 0 ? "operating_expenses" : "cost_of_sales" });
        }
        (int First, int Last)[] periods = [(1, 3), (4, 7), (8, 10), (11, 14)];
        var payments = new Dictionary<(string Currency, int Period, int Slot), (Obligation Obligation, decimal Amount, DateOnly Date)>();
        for (var i = 0; i < 20; i++)
        {
            var o = data.Obligations[i];
            var percent = i % 5 == 1 ? 100 : i % 5 is 0 or 2 ? Next(10, 85) : 0;
            if (percent == 0) continue;
            var total = FinanceCalculator.Money(o.Amount * percent / 100m);
            var parts = percent < 100 && i % 2 == 0 ? new[] { FinanceCalculator.Money(total * .4m), total - FinanceCalculator.Money(total * .4m) } : new[] { total };
            foreach (var part in parts)
            {
                var date = new DateOnly(2026, 10, Next(2, 14));
                var period = Array.FindIndex(periods, p => p.First <= date.Day && date.Day <= p.Last);
                var slot = Enumerable.Range(0, 20).First(j => !payments.ContainsKey((o.Currency, period, j)));
                payments[(o.Currency, period, slot)] = (o, part, date);
            }
        }
        var descriptions = new Dictionary<string, string> { ["customer_receipts"] = "Аванс от покупателя", ["supplier_payments"] = "Аванс поставщику", ["operating_payments"] = "Аванс за текущие услуги", ["equipment_purchase"] = "Приобретение оборудования", ["loan_received"] = "Получение займа, основной долг", ["loan_repaid"] = "Погашение займа, основной долг" };
        for (var ci = 0; ci < 3; ci++)
        for (var pi = 0; pi < 4; pi++)
        for (var j = 0; j < 20; j++)
        {
            var c = currencies[ci]; var cp = data.Counterparties[Next(0, 19)];
            var direction = Next(0, 99) < 48 ? "in" : "out";
            var amount = Amount(c); string? obligation = null;
            var date = new DateOnly(2026, 10, Next(periods[pi].First, periods[pi].Last));
            string[] incoming = ["customer_receipts", "loan_received"], outgoing = ["supplier_payments", "operating_payments", "equipment_purchase", "loan_repaid"];
            var category = direction == "in" ? incoming[Next(0, 99) < 85 ? 0 : 1] : outgoing[Next(0, 3)];
            var purpose = $"{descriptions[category]}; реестр {pi + 1}/{j + 1}";
            if (payments.TryGetValue((c, pi, j), out var payment))
            {
                var o = payment.Obligation;
                obligation = o.Id; amount = payment.Amount; date = payment.Date; direction = o.Direction;
                cp = data.Counterparties.First(p => p.Id == o.CounterpartyId);
                category = direction == "in" ? "customer_receipts" : "supplier_payments";
                purpose = $"Оплата по счёту {o.Invoice}, договор {o.Contract}; платёж {pi + 1}/{j + 1}";
            }
            data.Operations.Add(new($"T-{c}-{pi + 1}-{j + 1:000}", date, "A-" + c, c, cp.Id, cp.Name, direction, amount, obligation, purpose, category));
        }
        // Choose an opening reserve that keeps the generated historical bank balance nonnegative.
        // Future deficits are calculated, not drawn or forced into the calendar.
        foreach (var c in currencies)
        {
            decimal running = 0, minimum = 0;
            foreach (var t in data.Operations.Where(t => t.Currency == c).OrderBy(t => t.Date).ThenBy(t => t.Id))
            { running += t.Direction == "in" ? t.Amount : -t.Amount; minimum = Math.Min(minimum, running); }
            var reserve = Amount(c);
            data.Accounts.Add(new("A-" + c, c, FinanceCalculator.Money(-minimum + reserve)));
        }
        string[] outgoingPlans = ["Аренда офиса", "Оплата поставки материалов", "Услуги логистики", "Оплата подрядчику", "Техническое обслуживание", "Подписка на ПО", "Рекламные услуги", "Расходы на связь"];
        string[] incomingPlans = ["Поступление по проекту", "Оплата услуг клиентом", "Аванс по заказу", "Окончательный расчёт за поставку", "Поступление по договору сопровождения"];
        for (var bi = 0; bi < 20; bi++)
        for (var j = 0; j < 20; j++)
        {
            var cp = data.Counterparties[Next(0, 19)]; var c = currencies[Next(0, 2)]; var direction = Next(0, 99) < 48 ? "in" : "out";
            var due = data.Cutoff.AddDays(Next(0, data.End.DayNumber - data.Cutoff.DayNumber - 1));
            var scheduled = due.AddDays(Next(0, 99) < 25 ? Next(-3, 12) : 0);
            if (scheduled < data.Cutoff) scheduled = data.Cutoff;
            if (scheduled >= data.End) scheduled = data.End.AddDays(-1);
            var purposes = direction == "in" ? incomingPlans : outgoingPlans;
            data.Obligations.Add(new() { Id = $"P-{bi + 1:00}-{j + 1:000}", CounterpartyId = cp.Id, Counterparty = cp.Name, Currency = c, Account = "A-" + c, Direction = direction, Amount = Amount(c), Due = due, Expected = scheduled, ScheduledDate = scheduled, Purpose = $"{purposes[Next(0, purposes.Length - 1)]}, пакет {bi + 1}, позиция {j + 1}", Kind = "manual_plan" });
        }
        data.Accruals = data.Obligations.Take(20).Select(o => new Accrual("ACCR-" + o.Act, o.RecognizedDate!.Value, o.PnlCategory!, o.Amount, o.Currency, o.Counterparty, o.Id, "documents/" + o.Act + ".pdf")).ToList();
        return data;
    }
    public Dataset Generate(string output, int seed)
    {
        var docs = Path.Combine(output, "documents"); Directory.CreateDirectory(docs);
        if (Directory.EnumerateFileSystemEntries(docs).Any()) throw new InvalidOperationException("Папка documents не пуста. Выберите новую папку --output.");
        var data = Build(seed);
        string Register(string name, string type, IEnumerable<string> ids, List<string>? key = null)
        {
            var path = "documents/" + name; var list = ids.Distinct().ToList();
            data.Documents.Add(new() { Path = path, Type = type, Obligations = list, DocumentKey = key ?? [] });
            foreach (var o in data.Obligations.Where(o => list.Contains(o.Id))) o.Sources.Add(path);
            return Path.Combine(output, path);
        }
        foreach (var o in data.Obligations.Take(20))
        {
            string[] lines = [data.Company, "СИНТЕТИЧЕСКИЙ ОБРАЗЕЦ • НЕ ДЛЯ ОПЛАТЫ", $"Контрагент: {o.Counterparty}", $"Регистрационный код: {o.CounterpartyId} (учебный)", $"Договор: {o.Contract} от 2026-10-01", $"Счёт на оплату: {o.Invoice} от 2026-10-02", $"Валютный счёт компании: {o.Account}", $"Направление: {(o.Direction == "out" ? "выплата" : "поступление")}", $"Сумма: {Number(o.Amount)} {o.Currency}", $"Оплатить не позднее: {Date(o.Due)}"];
            Docx(Register(o.Contract + ".docx", "contract", [o.Id], ["contract", o.CounterpartyId, o.Contract!, "2026-10-01"]), new[] { "ДОГОВОР ОКАЗАНИЯ УСЛУГ" }.Concat(lines).Concat(["1. Предмет: оказание услуг, потребляемых в текущем периоде.", "2. Цена фиксирована в указанной валюте, НДС не облагается (учебное допущение).", $"3. Срок оплаты — {o.Due.DayNumber - o.RecognizedDate!.Value.DayNumber} календарных дней после акта; частичная оплата допускается.", "4. Исполнение подтверждается актом, связанным с тем же счётом.", "Заказчик: __________________    Исполнитель: __________________"]));
            Xlsx(Register(o.Invoice + ".xlsx", "invoice", [o.Id]), [["СЧЁТ НА ОПЛАТУ", o.Invoice!], ["Дата", "2026-10-02"], ["Компания", data.Company], ["Контрагент", o.Counterparty], ["Контрагент ID", o.CounterpartyId], ["Договор", o.Contract!], ["Валюта", o.Currency], ["Счёт компании", o.Account], ["Срок оплаты", Date(o.Due)], ["Направление", o.Direction], ["№", "Наименование", "Ед.", "Количество", "Цена", "Сумма"], ["1", "Оказанные и потреблённые услуги", "усл.", "1", Number(o.Amount), Number(o.Amount)], ["ИТОГО", Number(o.Amount)], ["НДС", "Без НДС, учебный образец"], ["Подпись", "________________"]]);
            Pdf(Register(o.Act + ".pdf", "act", [o.Id]), new[] { $"АКТ СДАЧИ-ПРИЁМКИ {o.Act} от {Date(o.RecognizedDate!.Value)}" }.Concat(lines).Concat([$"Статья ОПиУ: {FinanceCalculator.PnlCategories[o.PnlCategory!]}", "Услуги оказаны и потреблены в дату акта.", "Позиция 1. Оказанные и потреблённые услуги — 1 усл.", $"Итого: {Number(o.Amount)} {o.Currency}. Без НДС.", "Работы выполнены. Претензий по объёму нет.", "Акт подтверждает исполнение; не создаёт дополнительный платёж.", "Сдал: __________________       Принял: __________________"]));
        }
        foreach (var o in data.Obligations.Take(4))
            Pdf(Register(o.Contract + ".pdf", "contract_alternative", [o.Id], ["contract", o.CounterpartyId, o.Contract!, "2026-10-01"]), ["ДОГОВОР — альтернативное представление", data.Company, $"Договор: {o.Contract} от 2026-10-01", $"Контрагент: {o.Counterparty}", $"Контрагент ID: {o.CounterpartyId}", $"Сумма: {Number(o.Amount)} {o.Currency}", $"Оплатить не позднее: {Date(o.Due)}", $"Направление: {o.Direction}", "СИНТЕТИЧЕСКИЙ ОБРАЗЕЦ", "Тот же договор, что в DOCX. Не новое обязательство."]);
        (int First, int Last)[] periods = [(1, 3), (4, 7), (8, 10), (11, 14)];
        foreach (var c in data.Rates.Keys)
        for (var pi = 0; pi < 4; pi++)
        {
            var (first, last) = periods[pi]; var firstDate = new DateOnly(2026, 10, first); var lastDate = new DateOnly(2026, 10, last);
            var original = data.Operations.Where(t => t.Currency == c && firstDate <= t.Date && t.Date <= lastDate).ToList();
            var opening = data.Accounts.First(a => a.Currency == c).Opening + data.Operations.Where(t => t.Currency == c && t.Date < firstDate).Sum(t => t.Direction == "in" ? t.Amount : -t.Amount);
            var incoming = original.Where(t => t.Direction == "in").Sum(t => t.Amount); var outgoing = original.Where(t => t.Direction == "out").Sum(t => t.Amount);
            List<string[]> rows = [["БАНКОВСКАЯ ВЫПИСКА — УЧЕБНЫЙ ОБРАЗЕЦ"], ["Банк", "ДЕМО-БАНК (вымышленный)"], ["Компания", data.Company], ["Счёт", "A-" + c], ["Валюта", c], ["Период", Date(firstDate), Date(lastDate)], ["Входящий остаток", Number(opening)], ["Оборот по поступлениям", Number(incoming)], ["Оборот по списаниям", Number(outgoing)], ["Исходящий остаток", Number(opening + incoming - outgoing)], ["Примечание", "Добавлен повтор строки для проверки импорта; контрольные итоги без повтора"], DuplicateChecker.BankHeader];
            var items = original.OrderBy(t => t.Date).ThenBy(t => t.Id).Append(original[3]).ToList();
            rows.AddRange(items.Select(t => new[] { t.Id, Date(t.Date), t.Account, t.Currency, t.CounterpartyId, t.Counterparty, t.Direction, Number(t.Amount), t.Obligation ?? "", t.Purpose, t.CashCategory }));
            var ext = pi % 2 == 0 ? "csv" : "xlsx";
            var path = Register($"bank_{c}_{first:00}-{last:00}.{ext}", "bank_statement", items.Where(t => t.Obligation is not null).Select(t => t.Obligation!));
            if (ext == "csv") Csv(path, rows); else Xlsx(path, rows);
        }
        for (var bi = 0; bi < 20; bi++)
        {
            var items = data.Obligations.Skip(20 + bi * 20).Take(20).ToList(); items.Add(items[5]);
            List<string[]> rows = [["ПЛАНОВЫЕ ОПЕРАЦИИ — УЧЕБНЫЙ ОБРАЗЕЦ"], ["Компания", data.Company], ["Период", Date(data.Cutoff), Date(data.End)], DuplicateChecker.PlanHeader];
            rows.AddRange(items.Select(o => new[] { o.Id, o.CounterpartyId, o.Counterparty, o.Account, o.Currency, o.Direction, Number(o.Amount), Date(o.Due), Date(o.Expected), o.Purpose! }));
            Xlsx(Register($"plan_{bi + 1:00}.xlsx", "plan", items.Select(o => o.Id)), rows);
        }
        (string Original, string Copy)[] copies = [("ACT-001.pdf", "ACT-001_copy.pdf"), ("ACT-002.pdf", "ACT-002_copy.pdf"), ("bank_RUB_01-03.csv", "bank_RUB_01-03_copy.csv"), ("plan_01.xlsx", "plan_01_copy.xlsx")];
        foreach (var (original, copy) in copies)
        {
            var source = data.Documents.First(d => d.Path == "documents/" + original);
            File.Copy(Path.Combine(docs, original), Register(copy, source.Type, source.Obligations, source.DocumentKey));
        }
        var (operations, plans, report) = DuplicateChecker.Analyze(output, data.Documents);
        foreach (var o in plans) o.Sources = data.Documents.Where(d => d.Obligations.Contains(o.Id)).Select(d => d.Path).ToList();
        data.Obligations = data.Obligations.Take(20).Concat(plans).ToList(); data.Operations = operations; data.Duplicates = report;
        FinanceCalculator.Calculate(data);
        JsonFiles.Write(Path.Combine(output, "dataset.json"), data); JsonFiles.Write(Path.Combine(output, "duplicates.json"), report); JsonFiles.Write(Path.Combine(output, "reports.json"), data.Reports);
        List<string[]> calendar = [["Дата", "Валюта", "На начало", "Поступления", "Выплаты", "На конец"]];
        foreach (var day in data.Calendar) foreach (var (c, v) in day.Currencies) calendar.Add([Date(day.Date), c, Number(v.Opening), Number(v.In), Number(v.Out), Number(v.Closing)]);
        Csv(Path.Combine(output, "calendar.csv"), calendar);
        RenderViewer(output, data);
        return data;
    }
    public void RenderViewer(string output, Dataset data)
        => File.WriteAllText(Path.Combine(output, "calendar_view.html"), RenderHtml(data));
    public string RenderHtml(Dataset data)
    {
        var template = File.ReadAllText(Path.Combine(root, "demo", "viewer.html"));
        var json = JsonSerializer.Serialize(data, JsonFiles.Options).Replace("<", "\\u003c");
        return template.Replace("__DATA__", json).Replace("__FINANCE__", File.ReadAllText(Path.Combine(root, "demo", "finance.js"))).Replace("__DOCUMENT_TOOLS__", File.ReadAllText(Path.Combine(root, "demo", "document-tools.js")));
    }
}
