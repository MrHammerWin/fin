using System.Globalization;
using System.Text.RegularExpressions;

namespace FinPlan;

public sealed record ExtractedField(string Name, string Value, string Location);
public sealed class FinancialDocument
{
    public int ReaderVersion { get; set; }
    public string Type { get; set; } = "";
    public string Format { get; set; } = "";
    public Dictionary<string, string> Values { get; set; } = [];
    public List<ExtractedField> Fields { get; set; } = [];
    public List<string[]> Rows { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public bool CanApply { get; set; } = true;
    public int OcrPages { get; set; }
    public int TableStartRow { get; set; } = 1;
}

/// <summary>Financial fields from the project's explicit templates, without a synthetic model fallback.</summary>
public static partial class FinancialTemplateReader
{
    public const int Version = 1;
    public static FinancialDocument Read(string path)
    {
        var format = DocumentContentReader.Format(path);
        var content = format == "pdf"
            ? new PdfDocumentReader(null).ReadAsync(path, Path.GetDirectoryName(path)!, new("off"), null, default).GetAwaiter().GetResult().Content
            : DocumentContentReader.Read(path);
        return Parse(content, format);
    }
    public static async Task<FinancialDocument> ReadAsync(string path, string work, RecognitionTools tools, RecognitionOptions options, Action<string>? progress, CancellationToken cancellation)
    {
        if (DocumentContentReader.Format(path) != "pdf") return Read(path);
        var (content, pages) = await new PdfDocumentReader(tools).ReadAsync(path, work, options, progress, cancellation);
        try { var result = Parse(content, "pdf"); result.OcrPages = pages; return result; }
        catch (InvalidDataException error) when (pages > 0)
        {
            return new() { ReaderVersion = Version, Type = "unrecognized", Format = "pdf", OcrPages = pages, CanApply = false,
                Rows = content.Sections.SelectMany(s => s.Rows).ToList(),
                Warnings = [..content.Warnings, error.Message, "Данные не будут включены в расчёт. Исправьте исходный документ или загрузите PDF с читаемым текстовым слоем."] };
        }
    }
    private static string Text(string value) => Regex.Replace(value.Trim(), @"\s+", " ").Replace('\u00a0', ' ');
    private static decimal Money(string value, bool signed = false)
    {
        if (!Regex.IsMatch(value, signed ? @"^-?\d+(?:\.\d{1,2})?$" : @"^\d+(?:\.\d{1,2})?$", RegexOptions.CultureInvariant) || !decimal.TryParse(value, NumberStyles.AllowDecimalPoint | (signed ? NumberStyles.AllowLeadingSign : NumberStyles.None), CultureInfo.InvariantCulture, out var amount))
            throw new InvalidDataException("Неверная сумма в шаблоне. Нужны неотрицательное число и точка перед копейками.");
        return amount;
    }
    private static string Date(string value)
    {
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            if (!DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var excelDate) || excelDate.TimeOfDay != TimeSpan.Zero)
                throw new InvalidDataException("Неверная дата в шаблоне. Нужен формат ГГГГ-ММ-ДД или ячейка даты Excel без времени.");
            date = DateOnly.FromDateTime(excelDate);
        }
        return DocumentFiles.Date(date);
    }
    public static FinancialDocument Parse(DocumentContent content, string format)
    {
        var result = new FinancialDocument { ReaderVersion = Version, Format = format, Warnings = [.. content.Warnings] };
        var sections = content.Sections.Where(s => s.Rows.Any(r => r.Any(v => !string.IsNullOrWhiteSpace(v)))).ToList();
        var tables = sections.SelectMany(s => s.Rows.Select((r, i) => (Section: s, Row: r.Select(Text).ToArray(), Index: i)))
            .Where(x => x.Row.SequenceEqual(DuplicateChecker.BankHeader) || x.Row.SequenceEqual(DuplicateChecker.PlanHeader)).ToList();
        if (tables.Count > 0)
        {
            if (tables.Count != 1 || sections.Count != 1 || format is not ("xlsx" or "csv")) throw new InvalidDataException("Ожидается одна таблица выписки или плана в XLSX/CSV по шаблону проекта.");
            var table = tables[0]; var bank = table.Row.SequenceEqual(DuplicateChecker.BankHeader);
            result.Type = bank ? "bank_statement" : "plan"; result.TableStartRow = table.Index + 1;
            result.Rows = table.Section.Rows.Skip(table.Index).Select(row => row.Select(Text).ToArray()).Where(row => row.Any(v => v != "")).ToList();
            for (var i = 1; i < result.Rows.Count; i++)
            {
                var row = result.Rows[i];
                if (row.Length != table.Row.Length) throw new InvalidDataException($"Неверное число колонок в строке {table.Index + i + 1}.");
                row[bank ? 7 : 6] = DocumentFiles.Number(Money(row[bank ? 7 : 6])); row[bank ? 1 : 7] = Date(row[bank ? 1 : 7]); if (!bank) row[8] = Date(row[8]);
                ValidateCodes(row[bank ? 3 : 4], row[bank ? 2 : 3], row[bank ? 6 : 5]);
                Identifier(row[0]); Identifier(row[bank ? 4 : 1]); if (bank && row[8] != "") Identifier(row[8]);
                if (row[0] == "" || row[bank ? 4 : 1] == "" || row[bank ? 5 : 2] == "") throw new InvalidDataException("В строке отсутствуют ID операции или реквизиты контрагента.");
            }
            foreach (var (row, i) in table.Section.Rows.Take(table.Index).Select((r, i) => (r, i)))
            {
                if (row.Length >= 2 && TableKeys.TryGetValue(Text(row[0]), out var name)) Add(name, Text(row[1]), $"{table.Section.Name}, строка {i + 1}");
                if (row.Length >= 3 && Text(row[0]) == "Период") { Add("period_start", Date(row[1]), $"{table.Section.Name}, строка {i + 1}"); Add("period_end", Date(row[2]), $"{table.Section.Name}, строка {i + 1}"); }
            }
            if (result.Rows.Count <= 1) throw new InvalidDataException("В таблице нет финансовых записей.");
            return result;
        }
        if (sections.Count != 1) throw new InvalidDataException("Поддерживается один финансовый документ по шаблону проекта в файле.");
        var section = sections[0]; var rows = section.Rows.Select(r => r.Select(Text).ToArray()).ToList();
        if (format == "xlsx" && rows[0].FirstOrDefault() == "СЧЁТ НА ОПЛАТУ")
        {
            result.Type = "invoice";
            if (rows[0].Length != 2) throw new InvalidDataException("В счёте отсутствует номер.");
            Add("invoice", rows[0][1], $"{section.Name}, строка 1");
            decimal items = 0; var inItems = false; var itemCount = 0;
            foreach (var (row, i) in rows.Select((r, i) => (r, i)))
            {
                var location = $"{section.Name}, строка {i + 1}";
                if (row.Length >= 2 && InvoiceKeys.TryGetValue(row[0], out var name)) Add(name, row[1], location);
                if (row.Length > 0 && row[0] == "№") { if (!row.SequenceEqual(new[] { "№", "Наименование", "Ед.", "Количество", "Цена", "Сумма" })) throw new InvalidDataException("Неподдерживаемая таблица позиций счёта."); inItems = true; continue; }
                if (row.Length > 0 && row[0] == "ИТОГО") inItems = false;
                if (inItems)
                {
                    if (row.Length != 6 || !int.TryParse(row[0], out _) || row[1] == "") throw new InvalidDataException("Некорректная позиция счёта.");
                    var amount = Money(row[5]); if (FinanceCalculator.Money(Money(row[3]) * Money(row[4])) != amount) throw new InvalidDataException("Количество и цена не совпадают с суммой позиции счёта.");
                    items += amount; itemCount++;
                }
            }
            Require("invoice", "contract", "counterparty_id", "counterparty", "currency", "account", "direction", "due", "amount", "invoice_date");
            if (itemCount == 0 || items != Money(result.Values["amount"])) throw new InvalidDataException("Итог счёта не совпадает с суммой его позиций.");
        }
        else if (format is "docx" or "pdf")
        {
            var lines = rows.Select(row => Text(string.Join(' ', row))).ToList(); var title = lines[0];
            if (title.StartsWith("ДОГОВОР", StringComparison.Ordinal)) result.Type = "contract";
            else
            {
                var act = ActTitle().Match(title);
                if (!act.Success) throw Unsupported();
                result.Type = "act"; Add("act", act.Groups[1].Value, $"{section.Name}, строка 1"); Add("recognized_date", Date(act.Groups[2].Value), $"{section.Name}, строка 1");
            }
            if (lines.Count < 2 || lines[1].Contains(':')) throw new InvalidDataException("В шаблоне отсутствует компания-владелец документа.");
            Add("company", lines[1], $"{section.Name}, строка 2");
            for (var i = 1; i < lines.Count; i++)
            {
                var location = $"{section.Name}, строка {i + 1}"; var line = lines[i]; var colon = line.IndexOf(':');
                if (colon < 0) continue;
                var label = line[..colon]; var value = line[(colon + 1)..].Trim();
                if (label is "Договор" or "Счёт на оплату")
                {
                    var match = NumberDate().Match(value); if (!match.Success) throw new InvalidDataException("Неверные номер и дата документа.");
                    var name = label == "Договор" ? "contract" : "invoice";
                    Add(name, match.Groups[1].Value, location); Add(name + "_date", Date(match.Groups[2].Value), location);
                }
                else if (label is "Сумма" or "Итого")
                {
                    var match = AmountCurrency().Match(value); if (!match.Success) throw new InvalidDataException("Не удалось однозначно прочитать сумму и валюту.");
                    Add("amount", DocumentFiles.Number(Money(match.Groups[1].Value)), location); Add("currency", match.Groups[2].Value, location);
                }
                else if (label == "Регистрационный код") Add("counterparty_id", value.EndsWith(" (учебный)", StringComparison.Ordinal) ? value[..^10] : value, location);
                else if (TextKeys.TryGetValue(label, out var name)) Add(name, value, location);
            }
            Require("contract", "counterparty_id", "counterparty", "currency", "direction", "amount", "due");
            if (result.Type == "act") Require("invoice", "account", "recognized_date", "pnl_category");
            if (result.Type == "contract" && result.Values.ContainsKey("invoice")) Require("account");
        }
        else throw Unsupported();
        ValidateCodes(result.Values["currency"], result.Values.GetValueOrDefault("account"), result.Values["direction"]);
        foreach (var key in new[] { "due", "scheduled", "recognized_date", "invoice_date", "contract_date" }) if (result.Values.TryGetValue(key, out var value)) result.Values[key] = Date(value);
        return result;

        void Add(string name, string value, string location)
        {
            value = Text(value);
            if (name is "counterparty_id" or "obligation_id") Identifier(value);
            if (name == "direction") value = value switch { "выплата" => "out", "поступление" => "in", _ => value };
            if (name == "pnl_category") value = FinanceCalculator.PnlCategories.FirstOrDefault(p => p.Value == value).Key ?? throw new InvalidDataException("Неизвестная статья ОПиУ в акте.");
            if (name is "amount" or "opening" or "incoming" or "outgoing" or "closing") value = DocumentFiles.Number(Money(value, name is "opening" or "closing"));
            if (value == "") throw new InvalidDataException($"Пустое обязательное поле: {name}.");
            if (result.Values.TryGetValue(name, out var previous) && previous != value) throw new InvalidDataException($"Неоднозначное поле: {name}.");
            result.Values[name] = value; result.Fields.Add(new(name, value, location));
        }
        void Require(params string[] names) { foreach (var name in names) if (!result.Values.ContainsKey(name)) throw new InvalidDataException($"В шаблоне отсутствует поле: {name}."); }
    }
    private static void Identifier(string value)
    {
        if (!Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9_.-]{0,79}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("Внутренние ID шаблона должны содержать только латинские буквы, цифры, точку, дефис и подчёркивание. Неоднозначное распознавание ID требует исправления.");
    }
    private static void ValidateCodes(string currency, string? account, string direction)
    {
        if (currency is not ("RUB" or "CHF" or "GBP") || direction is not ("in" or "out") || account is not null && account != "A-" + currency)
            throw new InvalidDataException("Неизвестные валюта, счёт или направление в финансовом документе.");
    }
    private static InvalidDataException Unsupported() => new("Файл не соответствует поддерживаемому шаблону договора, счёта, акта, выписки или плана проекта. Произвольные документы не распознаются.");
    private static readonly Dictionary<string, string> InvoiceKeys = new()
    {
        ["Компания"] = "company", ["Дата"] = "invoice_date", ["Контрагент"] = "counterparty", ["Контрагент ID"] = "counterparty_id", ["Договор"] = "contract", ["Валюта"] = "currency", ["Счёт компании"] = "account", ["Срок оплаты"] = "due", ["Направление"] = "direction", ["ИТОГО"] = "amount", ["Обязательство ID"] = "obligation_id", ["Ожидаемая дата"] = "scheduled"
    };
    private static readonly Dictionary<string, string> TextKeys = new()
    {
        ["Контрагент"] = "counterparty", ["Контрагент ID"] = "counterparty_id", ["Валютный счёт компании"] = "account", ["Направление"] = "direction", ["Оплатить не позднее"] = "due", ["Обязательство ID"] = "obligation_id", ["Ожидаемая дата"] = "scheduled", ["Статья ОПиУ"] = "pnl_category"
    };
    private static readonly Dictionary<string, string> TableKeys = new()
    {
        ["Компания"] = "company", ["Счёт"] = "account", ["Валюта"] = "currency", ["Входящий остаток"] = "opening", ["Оборот по поступлениям"] = "incoming", ["Оборот по списаниям"] = "outgoing", ["Исходящий остаток"] = "closing"
    };
    [GeneratedRegex(@"^АКТ СДАЧИ-ПРИ[ЁЕ]МКИ (\S+) от (\d{4}-\d{2}-\d{2})$")]
    private static partial Regex ActTitle();
    [GeneratedRegex(@"^(\S+) от (\d{4}-\d{2}-\d{2})$")]
    private static partial Regex NumberDate();
    [GeneratedRegex(@"^(\d+(?:\.\d{1,2})?) (RUB|CHF|GBP)(?:\. Без НДС\.)?$")]
    private static partial Regex AmountCurrency();
}
