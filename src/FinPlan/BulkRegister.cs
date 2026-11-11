using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace FinPlan;

/// <summary>Readers and writers for the fixed, visible financial register templates. Memory is bounded by one part.</summary>
public sealed class BulkRegister : IDisposable
{
    public static readonly string[] Formats = ["csv", "xlsx", "docx", "pdf"];
    private readonly string path;
    private readonly List<string[]> rows;
    public BulkRegister(string path, string[] header) { this.path = path; rows = [header]; }
    public void Row(string[] values) => rows.Add(values);
    public VolumeFile Complete(string relativePath, string kind)
    {
        switch (Path.GetExtension(path))
        {
            case ".csv": DocumentFiles.Csv(path, rows); break;
            case ".xlsx": WriteOffice(true); break;
            case ".docx": WriteOffice(false); break;
            case ".pdf": WritePdf(); break;
            default: throw new InvalidDataException("Неизвестный формат реестра.");
        }
        return new(relativePath, kind, new FileInfo(path).Length, rows.Count - 1, FinancialImport.Hash(path));
    }
    public void Dispose() { }

    private void WriteOffice(bool sheet)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, value) in sheet ? OpenXmlTemplates.Xlsx : OpenXmlTemplates.Docx)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(value);
        }
        using var stream = zip.CreateEntry(sheet ? "xl/worksheets/sheet1.xml" : "word/document.xml").Open();
        using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        var ns = sheet ? "http://schemas.openxmlformats.org/spreadsheetml/2006/main" : "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        void Start(string name) => xml.WriteStartElement(name, ns);
        void End() => xml.WriteEndElement();
        void Attr(string name, string value) { if (sheet) xml.WriteAttributeString(name, value); else xml.WriteAttributeString("w", name, ns, value); }
        Start(sheet ? "worksheet" : "document"); Start(sheet ? "sheetData" : "body");
        if (!sheet) Start("tbl");
        foreach (var row in rows)
        {
            Start(sheet ? "row" : "tr");
            foreach (var value in row)
            {
                if (sheet) { Start("c"); Attr("t", "inlineStr"); Start("is"); Start("t"); xml.WriteString(value); End(); End(); End(); }
                else { Start("tc"); Start("p"); Start("r"); Start("t"); xml.WriteString(value); End(); End(); End(); End(); }
            }
            End();
        }
        if (!sheet)
        {
            End(); Start("sectPr"); Start("pgSz"); Attr("w", "23811"); Attr("h", "16838"); Attr("orient", "landscape"); End(); End();
        }
        End(); End();
    }

    // Base-14 Courier needs no embedded font. Every value is drawn in the page content stream,
    // including continuation lines. The reader below decodes these visible text operators.
    private void WritePdf()
    {
        using var file = new FileStream(path, FileMode.CreateNew);
        using var writer = new StreamWriter(file, Encoding.ASCII, 64 * 1024, true) { NewLine = "\n" };
        var offsets = new List<long> { 0 }; var pages = new List<int>();
        void Raw(string value) { writer.Write(value); writer.Flush(); }
        void Obj(string value) { offsets.Add(file.Position); Raw($"{offsets.Count - 1} 0 obj\n{value}\nendobj\n"); }
        Raw("%PDF-1.4\n%FinPlan-visible-register-v1\n");
        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        // Reserve the pages tree object; it is written at the end.
        offsets.Add(0);
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>");
        var lines = new List<string>();
        foreach (var row in rows)
        {
            var text = CsvLine(row);
            if (text.Any(c => c < 32 || c > 126)) throw new InvalidDataException("PDF-реестр использует фиксированный ASCII-шаблон.");
            for (var start = 0; start < text.Length; start += 175)
                lines.Add((start == 0 ? "R|" : "+|") + text.Substring(start, Math.Min(175, text.Length - start)));
        }
        for (var start = 0; start < lines.Count; start += 90)
        {
            var content = new StringBuilder("BT\n/F1 7 Tf\n9 TL\n28 560 Td\n");
            content.Append("(FinPlan financial register - page ").Append(pages.Count + 1).Append(") Tj\nT*\n");
            foreach (var line in lines.Skip(start).Take(90)) content.Append('(').Append(line.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)")).Append(") Tj\nT*\n");
            content.Append("ET\n");
            // 90 lines fit an A3 landscape page at this size.
            var text = content.ToString().Replace("28 560 Td", "28 820 Td");
            var contentId = offsets.Count;
            Obj($"<< /Length {Encoding.ASCII.GetByteCount(text)} >>\nstream\n{text}endstream");
            pages.Add(offsets.Count);
            Obj($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 1191 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {contentId} 0 R >>");
        }
        offsets[2] = file.Position;
        Raw("2 0 obj\n<< /Type /Pages /Count " + pages.Count + " /Kids [" + string.Join(' ', pages.Select(id => id + " 0 R")) + "] >>\nendobj\n");
        var xref = file.Position;
        Raw($"xref\n0 {offsets.Count}\n0000000000 65535 f \n" + string.Concat(offsets.Skip(1).Select(n => n.ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n")) + $"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }

    public static IEnumerable<string[]> Read(string path)
    {
        var format = Path.GetExtension(path);
        if (format == ".csv")
        {
            using var reader = new StreamReader(path, Encoding.UTF8, true, 128 * 1024);
            while (reader.ReadLine() is { } line) yield return ParseCsv(line);
        }
        else if (format == ".pdf")
        {
            using var reader = new StreamReader(path, Encoding.ASCII);
            if (reader.ReadLine() != "%PDF-1.4" || reader.ReadLine() != "%FinPlan-visible-register-v1") throw new InvalidDataException("Неизвестный шаблон PDF-реестра.");
            var record = new StringBuilder(); var inside = false;
            while (reader.ReadLine() is { } line)
            {
                if (line == "stream") { inside = true; continue; }
                if (line == "endstream") { inside = false; continue; }
                if (!inside || !line.StartsWith('(') || !line.EndsWith(") Tj", StringComparison.Ordinal)) continue;
                var text = new StringBuilder();
                for (var i = 1; i < line.Length - 4; i++) { if (line[i] == '\\') i++; text.Append(line[i]); }
                var value = text.ToString();
                if (value.StartsWith("R|", StringComparison.Ordinal))
                {
                    if (record.Length > 0) yield return ParseCsv(record.ToString());
                    record.Clear(); record.Append(value.AsSpan(2));
                }
                else if (value.StartsWith("+|", StringComparison.Ordinal)) record.Append(value.AsSpan(2));
            }
            if (record.Length > 0) yield return ParseCsv(record.ToString());
        }
        else if (format is ".docx" or ".xlsx")
        {
            using var zip = ZipFile.OpenRead(path);
            var sheet = format == ".xlsx";
            using var stream = (zip.GetEntry(sheet ? "xl/worksheets/sheet1.xml" : "word/document.xml") ?? throw new InvalidDataException("Не найден реестр.")).Open();
            using var xml = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            while (xml.Read()) if (xml.NodeType == XmlNodeType.Element && xml.LocalName == (sheet ? "row" : "tr"))
            {
                using var row = xml.ReadSubtree(); var values = new List<string>();
                while (row.Read()) if (row.NodeType == XmlNodeType.Element && row.LocalName == (sheet ? "c" : "tc"))
                {
                    using var cell = row.ReadSubtree(); var text = new StringBuilder();
                    while (cell.Read()) if (cell.NodeType == XmlNodeType.Element && cell.LocalName == "t") text.Append(cell.ReadElementContentAsString());
                    values.Add(text.ToString());
                }
                yield return values.ToArray();
            }
        }
        else throw new InvalidDataException("Неизвестный формат реестра.");
    }
    private static string CsvLine(IEnumerable<string> row) => string.Join(';', row.Select(value => value.IndexOfAny([';', '"', '\r', '\n']) >= 0 ? '"' + value.Replace("\"", "\"\"") + '"' : value));
    public static string[] ParseCsv(string line)
    {
        if (!line.Contains('"')) return line.Split(';');
        var values = new List<string>(); var field = new StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; } else quoted = !quoted; }
            else if (ch == ';' && !quoted) { values.Add(field.ToString()); field.Clear(); }
            else field.Append(ch);
        }
        if (quoted) throw new InvalidDataException("Незакрытая строка реестра.");
        values.Add(field.ToString()); return values.ToArray();
    }
}
