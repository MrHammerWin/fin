using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
namespace FinPlan;

public static class DocumentFiles
{
    private static readonly XNamespace Word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public static string Number(decimal value) => value.ToString("F2", CultureInfo.InvariantCulture);
    public static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static void Entry(ZipArchive zip, string path, string text)
    {
        var entry = zip.CreateEntry(path); entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); writer.Write(text);
    }
    public static void Docx(string path, IEnumerable<string> lines)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var item in OpenXmlTemplates.Docx) Entry(zip, item.Key, item.Value);
        var body = new XElement(Word + "body", lines.Select(line => new XElement(Word + "p", new XElement(Word + "r", new XElement(Word + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), line)))), new XElement(Word + "sectPr"));
        Entry(zip, "word/document.xml", new XElement(Word + "document", body).ToString());
    }
    private static string Column(int number)
    {
        var result = "";
        while (number > 0) { number--; result = (char)('A' + number % 26) + result; number /= 26; }
        return result;
    }
    public static void Xlsx(string path, List<string[]> rows)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var item in OpenXmlTemplates.Xlsx) Entry(zip, item.Key, item.Value);
        var header = rows.FindIndex(r => r.Length > 0 && r[0] is "ID операции" or "ID плана" or "№") + 1;
        if (header == 0) header = 1;
        var columns = rows.Max(r => r.Length);
        var worksheet = new XElement(Sheet + "worksheet",
            new XElement(Sheet + "sheetViews", new XElement(Sheet + "sheetView", new XAttribute("workbookViewId", 0), new XElement(Sheet + "pane", new XAttribute("ySplit", header), new XAttribute("topLeftCell", $"A{header + 1}"), new XAttribute("activePane", "bottomLeft"), new XAttribute("state", "frozen")))),
            new XElement(Sheet + "cols", Enumerable.Range(1, columns).Select(j => new XElement(Sheet + "col", new XAttribute("min", j), new XAttribute("max", j), new XAttribute("width", Math.Clamp(rows.Where(r => r.Length >= j).Max(r => r[j - 1].Length), 18, 45)), new XAttribute("customWidth", 1)))),
            new XElement(Sheet + "sheetData", rows.Select((row, i) => new XElement(Sheet + "row", new XAttribute("r", i + 1), new XAttribute("ht", 32), new XAttribute("customHeight", 1), row.Select((text, j) => new XElement(Sheet + "c", new XAttribute("r", $"{Column(j + 1)}{i + 1}"), new XAttribute("s", i == 0 || i + 1 == header ? 1 : 0), new XAttribute("t", "inlineStr"), new XElement(Sheet + "is", new XElement(Sheet + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text))))))),
            new XElement(Sheet + "autoFilter", new XAttribute("ref", $"A{header}:{Column(columns)}{rows.Count}")));
        Entry(zip, "xl/worksheets/sheet1.xml", worksheet.ToString(SaveOptions.DisableFormatting));
    }
    public static void Csv(string path, IEnumerable<string[]> rows)
    {
        static string Escape(string value) => value.IndexOfAny([';', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        File.WriteAllText(path, string.Join("\r\n", rows.Select(row => string.Join(';', row.Select(Escape)))) + "\r\n", new UTF8Encoding(true));
    }
    public static List<string[]> ReadRows(string path)
    {
        if (Path.GetExtension(path) == ".xlsx")
        {
            using var zip = ZipFile.OpenRead(path);
            using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            return XDocument.Load(stream).Descendants(Sheet + "row").Select(r => r.Elements(Sheet + "c").Select(c => c.Attribute("t")?.Value == "inlineStr" ? string.Concat(c.Descendants(Sheet + "t").Select(t => t.Value)) : c.Element(Sheet + "v")?.Value ?? "").ToArray()).ToList();
        }
        var text = File.ReadAllText(path); var result = new List<string[]>(); var fields = new List<string>(); var field = new StringBuilder(); var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"') { if (quoted && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; } else quoted = !quoted; }
            else if (!quoted && c is ';' or '\r' or '\n')
            {
                fields.Add(field.ToString()); field.Clear();
                if (c != ';') { result.Add(fields.ToArray()); fields.Clear(); if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; }
            }
            else field.Append(c);
        }
        if (quoted) throw new InvalidDataException("Незакрытая кавычка CSV.");
        if (field.Length > 0 || fields.Count > 0) { fields.Add(field.ToString()); result.Add(fields.ToArray()); }
        return result;
    }
    public static void Pdf(string path, IEnumerable<string> lines)
    {
        using var font = SKTypeface.FromFile(Path.Combine(AppContext.BaseDirectory, "Assets", "DejaVuSans.ttf")) ?? throw new InvalidOperationException("Не найден шрифт DejaVuSans.");
        using var paint = new SKPaint { Typeface = font, TextSize = 10, IsAntialias = true, Color = SKColors.Black };
        // Own the file stream explicitly: a native PDF stream can keep the file locked on Windows.
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var document = SKDocument.CreatePdf(output);
        var canvas = document.BeginPage(595, 842); var y = 55f;
        foreach (var line in lines)
        {
            var current = "";
            foreach (var word in line.Split(' '))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (paint.MeasureText(candidate) > 490 && current.Length > 0) { Draw(current); current = word; } else current = candidate;
            }
            Draw(current); y += 8;
        }
        document.EndPage(); document.Close();
        void Draw(string line)
        {
            if (y > 785) { document.EndPage(); canvas = document.BeginPage(595, 842); y = 55; }
            canvas.DrawText(line, 48, y, paint); y += 15;
        }
    }
}
