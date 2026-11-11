using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace FinPlan;

public sealed record ContentSection(string Name, List<string[]> Rows);
public sealed record DocumentContent(List<ContentSection> Sections, List<string> Warnings);

/// <summary>Reads actual source content. It never consults the synthetic dataset.</summary>
public static class DocumentContentReader
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public const long MaxInputBytes = 100_000_000;
    public const int MaxCells = 1_000_000;
    public static readonly string[] Formats = ["docx", "pdf", "xlsx", "csv"];

    public static string Format(string path)
    {
        var format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (!Formats.Contains(format)) throw new InvalidDataException("Поддерживаются DOCX, PDF, XLSX и CSV.");
        return format;
    }

    public static void Validate(string path)
    {
        var size = new FileInfo(path).Length;
        if (size == 0 || size > MaxInputBytes) throw new InvalidDataException("Размер файла должен быть от 1 байта до 100 МБ.");
        var format = Format(path);
        if (format == "pdf")
        {
            using var file = File.OpenRead(path); Span<byte> magic = stackalloc byte[5];
            if (file.Read(magic) != 5 || !magic.SequenceEqual("%PDF-"u8)) throw new InvalidDataException("Содержимое не соответствует PDF.");
        }
        else if (format is "docx" or "xlsx")
        {
            using var zip = ZipFile.OpenRead(path);
            if (zip.Entries.Count > 10000 || zip.Entries.Sum(e => e.Length) > 300_000_000)
                throw new InvalidDataException("Распакованный документ превышает ограничение 300 МБ или 10000 частей.");
            var main = format == "docx" ? "word/document.xml" : "xl/workbook.xml";
            if (zip.GetEntry(main) is null || zip.GetEntry("[Content_Types].xml") is null)
                throw new InvalidDataException("Содержимое не соответствует заявленному формату Office.");
            if (zip.Entries.Any(e => e.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Документы с макросами не поддерживаются.");
        }
    }

    private static XDocument Xml(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path) ?? throw new InvalidDataException("В документе отсутствует обязательная часть OOXML.");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 300_000_000 });
        return XDocument.Load(reader);
    }

    public static DocumentContent Read(string path)
    {
        Validate(path);
        var content = Format(path) switch
        {
            "csv" => new DocumentContent([new("Данные", ReadCsv(path))], []),
            "docx" => ReadDocx(path),
            "xlsx" => ReadXlsx(path),
            _ => throw new InvalidDataException("PDF обрабатывается модулем извлечения текста и OCR.")
        };
        if (content.Sections.Sum(s => (long)s.Rows.Sum(r => r.Length)) > MaxCells)
            throw new InvalidDataException("Документ содержит больше 1000000 ячеек. Разделите его на меньшие файлы.");
        return content;
    }

    public static List<string[]> ReadCsv(string path)
    {
        // Strict UTF-8 is intentional: guessing a legacy encoding can silently corrupt money or names.
        var text = File.ReadAllText(path, new UTF8Encoding(false, true));
        var first = text.Split(['\r', '\n'], 2)[0];
        var separator = new[] { ';', ',', '\t' }.OrderByDescending(c => CountSeparators(first, c)).First();
        var result = new List<string[]>(); var fields = new List<string>(); var value = new StringBuilder();
        var quoted = false; var afterQuote = false; var cells = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') { value.Append('"'); i++; }
                else if (quoted) { quoted = false; afterQuote = true; }
                else if (value.Length == 0 && !afterQuote) quoted = true;
                else throw new InvalidDataException("Кавычка внутри неэкранированного поля CSV.");
            }
            else if (!quoted && (c == separator || c is '\r' or '\n'))
            {
                AddField();
                if (c != separator) { result.Add(fields.ToArray()); fields.Clear(); if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; }
            }
            else
            {
                if (afterQuote && !quoted) throw new InvalidDataException("Недопустимый символ после закрывающей кавычки CSV.");
                value.Append(c);
            }
        }
        if (quoted) throw new InvalidDataException("Незакрытая кавычка CSV.");
        if (value.Length > 0 || fields.Count > 0 || afterQuote) { AddField(); result.Add(fields.ToArray()); }
        return result;
        void AddField()
        {
            if (++cells > MaxCells) throw new InvalidDataException("CSV содержит больше 1000000 ячеек.");
            fields.Add(value.ToString()); value.Clear(); afterQuote = false;
        }
    }

    private static int CountSeparators(string text, char separator)
    {
        var count = 0; var quoted = false;
        foreach (var c in text) { if (c == '"') quoted = !quoted; else if (!quoted && c == separator) count++; }
        return count;
    }

    private static string WordText(XElement element) => string.Concat(element.Descendants().Select(e => e.Name == W + "t" ? e.Value : e.Name == W + "tab" ? "\t" : e.Name == W + "br" ? "\n" : ""));
    private static DocumentContent ReadDocx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var sections = new List<ContentSection>();
        var parts = new[] { "word/document.xml" }.Concat(zip.Entries.Select(e => e.FullName).Where(n => n.StartsWith("word/", StringComparison.Ordinal) &&
            (n.StartsWith("word/header", StringComparison.Ordinal) || n.StartsWith("word/footer", StringComparison.Ordinal) || n is "word/footnotes.xml" or "word/endnotes.xml") && n.EndsWith(".xml", StringComparison.Ordinal)));
        foreach (var part in parts)
        {
            var xml = Xml(zip, part); var rows = new List<string[]>();
            foreach (var block in xml.Descendants().Where(e => (e.Name == W + "p" || e.Name == W + "tbl") && !e.Ancestors(W + "tbl").Any()))
            {
                if (block.Name == W + "p") rows.Add([WordText(block)]);
                else foreach (var row in block.Elements(W + "tr")) rows.Add(row.Elements(W + "tc").Select(cell => string.Join('\n', cell.Descendants(W + "p").Select(WordText))).ToArray());
            }
            sections.Add(new(part == "word/document.xml" ? "Документ" : Path.GetFileNameWithoutExtension(part), rows));
        }
        return new(sections, []);
    }

    private static DocumentContent ReadXlsx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var workbook = Xml(zip, "xl/workbook.xml");
        var relationships = Xml(zip, "xl/_rels/workbook.xml.rels").Root!.Elements()
            .Where(e => e.Attribute("TargetMode")?.Value != "External")
            .ToDictionary(e => e.Attribute("Id")!.Value, e => e.Attribute("Target")!.Value);
        var shared = zip.GetEntry("xl/sharedStrings.xml") is null ? [] : Xml(zip, "xl/sharedStrings.xml").Descendants(S + "si").Select(e => string.Concat(e.Descendants(S + "t").Select(t => t.Value))).ToArray();
        var sections = new List<ContentSection>(); var warnings = new List<string>(); var total = 0;
        var styles = zip.GetEntry("xl/styles.xml") is null ? null : Xml(zip, "xl/styles.xml");
        var dateStyles = styles?.Root?.Element(S + "cellXfs")?.Elements().Select(e => (int?)e.Attribute("numFmtId") ?? 0).ToArray() ?? [];
        var customFormats = styles?.Descendants(S + "numFmt").ToDictionary(e => (int)e.Attribute("numFmtId")!, e => (string)e.Attribute("formatCode")!) ?? [];
        var epoch = workbook.Descendants(S + "workbookPr").Any(e => e.Attribute("date1904")?.Value is "1" or "true") ? new DateTime(1904, 1, 1) : new DateTime(1899, 12, 30);
        foreach (var sheet in workbook.Descendants(S + "sheet"))
        {
            var target = relationships[(string)sheet.Attribute(R + "id")!];
            var part = new Uri(new Uri("https://ooxml.invalid/xl/workbook.xml"), target).AbsolutePath.TrimStart('/');
            var xml = Xml(zip, part); var rows = new List<string[]>(); var rowIndex = 0;
            foreach (var row in xml.Descendants(S + "sheetData").Elements(S + "row"))
            {
                var index = (int?)row.Attribute("r") ?? rowIndex + 1;
                if (index <= rowIndex || index > 1_048_576) throw new InvalidDataException("Некорректный индекс строки XLSX.");
                while (++rowIndex < index) { rows.Add([]); if (++total > MaxCells) throw new InvalidDataException("Слишком много строк XLSX."); }
                var values = new List<string>();
                foreach (var cell in row.Elements(S + "c"))
                {
                    var reference = (string?)cell.Attribute("r"); var column = 0;
                    if (reference is not null) foreach (var c in reference.TakeWhile(char.IsLetter)) column = checked(column * 26 + char.ToUpperInvariant(c) - 'A' + 1);
                    else column = values.Count + 1;
                    if (column < values.Count + 1 || column > 16384) throw new InvalidDataException("Некорректный индекс колонки XLSX.");
                    while (values.Count < column - 1) values.Add("");
                    var raw = cell.Element(S + "v")?.Value ?? "";
                    var value = cell.Attribute("t")?.Value switch
                    {
                        "inlineStr" => string.Concat(cell.Descendants(S + "t").Select(t => t.Value)),
                        "s" => int.TryParse(raw, out var si) && si >= 0 && si < shared.Length ? shared[si] : throw new InvalidDataException("Некорректная ссылка sharedStrings XLSX."),
                        "b" => raw == "1" ? "TRUE" : "FALSE",
                        _ => raw
                    };
                    if (cell.Element(S + "f") is not null)
                    {
                        if (raw == "") throw new InvalidDataException("У формулы XLSX нет сохранённого результата. Пересчитайте и сохраните книгу в Excel или совместимом офисном приложении.");
                        warnings.Add("Прочитаны сохранённые результаты формул; их актуальность зависит от последнего сохранения книги.");
                    }
                    var style = (int?)cell.Attribute("s") ?? 0;
                    var numberFormat = style < dateStyles.Length ? dateStyles[style] : 0;
                    if ((numberFormat is >= 14 and <= 22 or >= 45 and <= 47 || (customFormats.TryGetValue(numberFormat, out var fmt) && IsDateFormat(fmt))) && double.TryParse(raw, CultureInfo.InvariantCulture, out var serial))
                        value = epoch.AddDays(serial).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    values.Add(value);
                    if (total + values.Count > MaxCells) throw new InvalidDataException("XLSX содержит больше 1000000 ячеек.");
                }
                total += values.Count; rows.Add(values.ToArray());
            }
            sections.Add(new((string?)sheet.Attribute("name") ?? "Лист", rows));
        }
        return new(sections, warnings.Distinct().ToList());
    }
    private static bool IsDateFormat(string format)
    {
        var stripped = System.Text.RegularExpressions.Regex.Replace(format, "\"[^\"]*\"|\\\\.|\\[[^\\]]*\\]", "").ToLowerInvariant();
        return stripped.Contains('y') || stripped.Contains('d') || stripped.Contains('h') || stripped.Contains('s');
    }
}
