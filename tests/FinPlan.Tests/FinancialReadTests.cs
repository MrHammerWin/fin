using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FinPlan;
using SkiaSharp;
using Xunit;

namespace FinPlan.Tests;

public sealed class NativeFactAttribute : FactAttribute
{
    public NativeFactAttribute() { if (Environment.GetEnvironmentVariable("FINPLAN_INTEGRATION_TESTS") != "1") Skip = "Set FINPLAN_INTEGRATION_TESTS=1 to exercise Poppler and Tesseract."; }
}
public sealed class ReaderFixture : IDisposable
{
    public string Root { get; }
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "finplan-reader-" + Guid.NewGuid().ToString("N"));
    public ReaderFixture()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Solution1.sln"))) root = root.Parent;
        Root = root!.FullName; System.IO.Directory.CreateDirectory(Directory);
    }
    public string NewDirectory() { var path = Path.Combine(Directory, Guid.NewGuid().ToString("N")); System.IO.Directory.CreateDirectory(path); return path; }
    public void Dispose() => System.IO.Directory.Delete(Directory, true);
}
public sealed class FinancialReadTests(ReaderFixture fixture) : IClassFixture<ReaderFixture>
{
    [Fact]
    public void FinancialResultsComeFromFilesEvenIfTheGeneratedModelIsEmptyOrWrong()
    {
        var output = fixture.NewDirectory(); var d = new DemoGenerator(fixture.Root).Generate(output, 42);
        var calendar = JsonSerializer.Serialize(d.Calendar, JsonFiles.Options); var reports = JsonSerializer.Serialize(d.Reports, JsonFiles.Options);
        d.Company = "Incorrect cached company"; d.Obligations.Clear(); d.Operations.Clear(); d.Accruals.Clear(); d.Accounts.Clear(); d.Counterparties.Clear();
        foreach (var doc in d.Documents) { doc.Extraction = null; doc.DocumentKey = ["incorrect metadata"]; doc.Obligations.Clear(); }
        FinancialImport.Calculate(output, d);
        Assert.Equal(calendar, JsonSerializer.Serialize(d.Calendar, JsonFiles.Options)); Assert.Equal(reports, JsonSerializer.Serialize(d.Reports, JsonFiles.Options));
        Assert.Equal("ООО «Демо Финансы» (вымышленная)",d.Company);
        Assert.Equal(420, d.Obligations.Count); Assert.Equal(20, d.Accruals.Count); Assert.Equal(240, d.Operations.Count);
        Assert.Equal(new[] { "csv", "docx", "pdf", "xlsx" }, d.Ingestion!.Formats); Assert.Equal(100, d.Ingestion.Documents);
        Assert.All(d.Documents, doc => Assert.NotNull(doc.Extraction));
    }
    [Fact]
    public async Task EditingActualDocxXlsxAndPdfChangesFinancialAmountsAndCachedModelCannotOverrideThem()
    {
        var output = fixture.NewDirectory(); var d = new DemoGenerator(fixture.Root).Generate(output, 42);
        var o = d.Obligations.Single(o => o.Id == "O-002"); var old = o.Amount; var changed = old * 2;
        var category = o.PnlCategory!; var oldExpense = d.Reports!.Currencies[o.Currency][category];
        foreach (var doc in d.Documents.Where(doc => doc.Obligations.Contains(o.Id) && doc.Type is "contract" or "contract_alternative" or "invoice" or "act"))
        {
            var path = Path.Combine(output, doc.Path); var format = DocumentContentReader.Format(path);
            var content = format == "pdf" ? (await new PdfDocumentReader(null).ReadAsync(path, output, new("off"), null, default)).Content : DocumentContentReader.Read(path);
            var rows = content.Sections.Single().Rows.Select(row => row.Select(v => v.Replace(DocumentFiles.Number(old), DocumentFiles.Number(changed), StringComparison.Ordinal)).ToArray()).ToList();
            File.Delete(path);
            if (format == "docx") DocumentFiles.Docx(path, rows.Select(r => string.Join(' ', r)));
            else if (format == "xlsx") DocumentFiles.Xlsx(path, rows);
            else DocumentFiles.Pdf(path, rows.Select(r => string.Join(' ', r)));
        }
        o.Amount = 0; d.Accruals.Clear(); FinancialImport.Calculate(output, d);
        var imported = d.Obligations.Single(x => x.Id == "O-002");
        Assert.Equal(changed, imported.Amount);
        Assert.Equal(oldExpense + old, d.Reports!.Currencies[o.Currency][category]);
        Assert.Equal(changed, d.Accruals.Single(a => a.Obligation == imported.Id).Amount);
        Assert.Contains(d.Documents, doc => doc.Extraction!.Fields.Any(f => f.Name == "amount" && f.Value == DocumentFiles.Number(changed) && f.Location.Contains("строка")));
    }
    [Fact]
    public void DisagreementBetweenInvoiceAndActOrBadStatementTotalsBlocksCalculation()
    {
        var output = fixture.NewDirectory(); var d = new DemoGenerator(fixture.Root).Generate(output, 42);
        var path = Path.Combine(output, "documents/S-002.xlsx"); var rows = DocumentContentReader.Read(path).Sections[0].Rows;
        var amount = d.Obligations.Single(o => o.Id == "O-002").Amount;
        foreach (var row in rows) for (var i = 0; i < row.Length; i++) if (row[i] == DocumentFiles.Number(amount)) row[i] = DocumentFiles.Number(amount * 2);
        File.Delete(path); DocumentFiles.Xlsx(path, rows);
        Assert.Throws<InvalidDataException>(() => FinancialImport.Calculate(output, d));
        var output2 = fixture.NewDirectory(); var d2 = new DemoGenerator(fixture.Root).Generate(output2, 43);
        var bank = Path.Combine(output2, "documents/bank_RUB_01-03.csv"); var bankRows = DocumentContentReader.ReadCsv(bank);
        bankRows.Single(r => r[0] == "Входящий остаток")[1] = "0.00"; DocumentFiles.Csv(bank, bankRows);
        Assert.Throws<InvalidDataException>(() => FinancialImport.Calculate(output2, d2));
    }
    [Fact]
    public void ArbitraryTablesTextAndMalformedFilesAreRejectedInsteadOfReceivingInventedFields()
    {
        var output = fixture.NewDirectory(); var csv = Path.Combine(output, "other.csv"); var word = Path.Combine(output, "other.docx"); var pdf = Path.Combine(output, "fake.pdf");
        DocumentFiles.Csv(csv, [["Name", "Amount"], ["USERPROBE", "9876.54"]]); DocumentFiles.Docx(word, ["Some document", "Сумма 9876.54"]); File.WriteAllText(pdf, "not-pdf");
        foreach (var path in new[] { csv, word, pdf }) Assert.Throws<InvalidDataException>(() => FinancialTemplateReader.Read(path));
        DocumentFiles.Csv(csv,[DuplicateChecker.PlanHeader,["X\");alert(1)//","CP-001","Company","A-RUB","RUB","out","100.00","2026-11-20","2026-11-20","Purpose"]]);
        Assert.Throws<InvalidDataException>(()=>FinancialTemplateReader.Read(csv));
        File.WriteAllText(csv, "a;b\n\"unterminated"); Assert.Throws<InvalidDataException>(() => DocumentContentReader.ReadCsv(csv));
    }
    [Fact]
    public void ExcelDateCellsSharedStringsAndCachedFormulaAreReadAsFinancialValues()
    {
        var directory=fixture.NewDirectory();var path=Path.Combine(directory,"plan.xlsx");
        DocumentFiles.Xlsx(path,[DuplicateChecker.PlanHeader,["P-EXCEL","CP-001","Company","A-RUB","RUB","out","1234.56","2026-11-20","2026-11-20","Source values"]]);
        var ns=(System.Xml.Linq.XNamespace)"http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        using(var zip=ZipFile.Open(path,ZipArchiveMode.Update))
        {
            System.Xml.Linq.XDocument Read(string name){using var stream=zip.GetEntry(name)!.Open();return System.Xml.Linq.XDocument.Load(stream);}
            void Write(string name,System.Xml.Linq.XDocument xml){zip.GetEntry(name)?.Delete();using var writer=new StreamWriter(zip.CreateEntry(name).Open());xml.Save(writer);}
            var styles=Read("xl/styles.xml");var xfs=styles.Root!.Element(ns+"cellXfs")!;var dateIndex=xfs.Elements().Count();xfs.Add(new System.Xml.Linq.XElement(ns+"xf",new System.Xml.Linq.XAttribute("numFmtId",14)));xfs.SetAttributeValue("count",dateIndex+1);Write("xl/styles.xml",styles);
            var sheet=Read("xl/worksheets/sheet1.xml");var cells=sheet.Descendants(ns+"c").ToDictionary(c=>(string)c.Attribute("r")!);
            cells["C2"].RemoveNodes();cells["C2"].SetAttributeValue("t","s");cells["C2"].Add(new System.Xml.Linq.XElement(ns+"v",0));
            cells["H2"].RemoveNodes();cells["H2"].SetAttributeValue("t",null);cells["H2"].SetAttributeValue("s",dateIndex);cells["H2"].Add(new System.Xml.Linq.XElement(ns+"v",new DateTime(2026,11,20).ToOADate()));
            cells["G2"].RemoveNodes();cells["G2"].SetAttributeValue("t",null);cells["G2"].Add(new System.Xml.Linq.XElement(ns+"f","1000+234.56"),new System.Xml.Linq.XElement(ns+"v","1234.56"));Write("xl/worksheets/sheet1.xml",sheet);
            Write("xl/sharedStrings.xml",new(new System.Xml.Linq.XElement(ns+"sst",new System.Xml.Linq.XElement(ns+"si",new System.Xml.Linq.XElement(ns+"t","USER COMPANY")))));
        }
        var report=FinancialTemplateReader.Read(path);Assert.Equal("USER COMPANY",report.Rows[1][2]);Assert.Equal("2026-11-20",report.Rows[1][7]);Assert.Equal("1234.56",report.Rows[1][6]);Assert.NotEmpty(report.Warnings);
        using(var zip=ZipFile.Open(path,ZipArchiveMode.Update)){using(var stream=zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()){var xml=System.Xml.Linq.XDocument.Load(stream);xml.Descendants(ns+"c").Single(c=>(string?)c.Attribute("r")=="G2").Element(ns+"v")!.Remove();stream.Close();zip.GetEntry("xl/worksheets/sheet1.xml")!.Delete();using var writer=new StreamWriter(zip.CreateEntry("xl/worksheets/sheet1.xml").Open());xml.Save(writer);}}
        Assert.Throws<InvalidDataException>(()=>FinancialTemplateReader.Read(path));
    }
    [NativeFact]
    public async Task ScanReadsActualTextAndAmbiguousFinancialFieldsCannotBeApplied()
    {
        var output = fixture.NewDirectory(); var path = Path.Combine(output, "scan.pdf");
        string[] lines = ["АКТ СДАЧИ-ПРИЕМКИ ACT-901 от 2026-10-03", "ООО «Демо Финансы» (вымышленная)", "Обязательство ID: O-901", "Ожидаемая дата: 2026-11-01", "Контрагент: TEST COMPANY", "Контрагент ID: CP-901", "Договор: D-901 от 2026-10-01", "Счёт на оплату: S-901 от 2026-10-02", "Валютный счёт компании: A-RUB", "Направление: out", "Сумма: 1234.56 RUB", "Оплатить не позднее: 2026-11-01", "Статья ОПиУ: Операционные расходы"];
        using (var font = SKTypeface.FromFile(Path.Combine(AppContext.BaseDirectory, "Assets", "DejaVuSans.ttf")))
        using (var paint = new SKPaint { Typeface = font, TextSize = 35, Color = SKColors.Black, IsAntialias = true })
        using (var surface = SKSurface.Create(new SKImageInfo(2000, 1700)))
        {
            surface.Canvas.Clear(SKColors.White); for (var i = 0; i < lines.Length; i++) surface.Canvas.DrawText(lines[i], 60, 100 + i * 110, paint);
            using var image = surface.Snapshot(); using var file = File.Create(path); using var pdf = SKDocument.CreatePdf(file);
            var page = pdf.BeginPage(700, 600); page.DrawImage(image, new SKRect(0, 0, 700, 595)); pdf.EndPage(); pdf.Close();
        }
        var raw = await new PdfDocumentReader(new(fixture.Root)).ReadAsync(path, output, new(), null, default);
        Assert.Equal(1, raw.OcrPages); Assert.Contains("1234.56", string.Join(" ", raw.Content.Sections.SelectMany(s=>s.Rows).SelectMany(r=>r)));
        var report = await FinancialTemplateReader.ReadAsync(path, output, new(fixture.Root), new(), null, default);
        Assert.Equal(1, report.OcrPages); Assert.Contains(report.Warnings, w => w.Contains("OCR"));
        if (report.CanApply) Assert.Equal("1234.56", report.Values["amount"]);
        else { Assert.Empty(report.Values); Assert.Contains(report.Warnings, w=>w.Contains("не будут включены")); }
        await Assert.ThrowsAsync<InvalidDataException>(() => FinancialTemplateReader.ReadAsync(path, output, new(fixture.Root), new("off"), null, default));
    }
}
