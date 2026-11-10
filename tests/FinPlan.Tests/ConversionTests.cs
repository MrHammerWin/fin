using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FinPlan;
using SkiaSharp;
using UglyToad.PdfPig;
using Xunit;

namespace FinPlan.Tests;

public sealed class NativeFactAttribute : FactAttribute
{
    public NativeFactAttribute() { if (Environment.GetEnvironmentVariable("FINPLAN_INTEGRATION_TESTS") != "1") Skip = "Set FINPLAN_INTEGRATION_TESTS=1 to exercise LibreOffice, Poppler and Tesseract."; }
}
public sealed class NativeTheoryAttribute : TheoryAttribute
{
    public NativeTheoryAttribute() { if (Environment.GetEnvironmentVariable("FINPLAN_INTEGRATION_TESTS") != "1") Skip = "Set FINPLAN_INTEGRATION_TESTS=1 to exercise LibreOffice."; }
}

public sealed class ConversionFixture : IDisposable
{
    public string Root { get; }
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "finplan-conversion-" + Guid.NewGuid().ToString("N"));
    public const string Probe = "CONVERSIONPROBE72";
    public ConversionFixture()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Solution1.sln"))) root = root.Parent;
        Root = root!.FullName; System.IO.Directory.CreateDirectory(Directory);
        DocumentFiles.Docx(Path.Combine(Directory, "source.docx"), [Probe, "Сумма 1234.56", "Только пользовательский документ"]);
        DocumentFiles.Pdf(Path.Combine(Directory, "source.pdf"), [Probe, "Сумма 1234.56", "Только пользовательский документ"]);
        List<string[]> rows = [["Код", "Сумма"], [Probe, "1234.56"], ["literal", "=2+2"]];
        DocumentFiles.Xlsx(Path.Combine(Directory, "source.xlsx"), rows); DocumentFiles.Csv(Path.Combine(Directory, "source.csv"), rows);
    }
    public string NewDirectory() { var path = Path.Combine(Directory, Guid.NewGuid().ToString("N")); System.IO.Directory.CreateDirectory(path); return path; }
    public DocumentConverter Converter() => new(new ConversionTools(Root));
    public void Dispose() => System.IO.Directory.Delete(Directory, true);
}

public sealed class ConversionTests(ConversionFixture fixture) : IClassFixture<ConversionFixture>
{
    [Theory]
    [InlineData("docx", "csv")][InlineData("docx", "xlsx")]
    [InlineData("xlsx", "csv")][InlineData("xlsx", "docx")]
    [InlineData("csv", "xlsx")][InlineData("csv", "docx")]
    [InlineData("pdf", "csv")][InlineData("pdf", "xlsx")][InlineData("pdf", "docx")]
    public async Task ActualSourceContentSurvivesEveryManagedDirection(string source, string target) => await Check(source, target);

    [NativeTheory]
    [InlineData("docx")][InlineData("xlsx")][InlineData("csv")]
    public async Task OfficeEngineProducesRealPdfWithSourceText(string source) => await Check(source, "pdf");

    private async Task Check(string source, string target)
    {
        var path = Path.Combine(fixture.Directory, "source." + source); var before = SHA256.HashData(File.ReadAllBytes(path));
        var result = await fixture.Converter().ConvertAsync(path, fixture.NewDirectory(), new(target));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
        string text;
        if (target == "pdf") { using var pdf = PdfDocument.Open(result.Path); text = string.Join(' ', pdf.GetPages().Select(p => p.Text)); }
        else text = string.Join('\n', ConversionContentReader.Read(result.Path).FlatRows().Select(r => string.Join(' ', r)));
        Assert.Contains(ConversionFixture.Probe, text); Assert.Contains("1234.56", text);
        Assert.DoesNotContain("Северный Контур", text); // No generated template is substituted for the user file.
        if (source != "csv" || target == "pdf") Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void CsvPreservesQuotedSeparatorsMultilineFieldsAndLiteralFormulas()
    {
        var path = Path.Combine(fixture.NewDirectory(), "quoted.csv");
        File.WriteAllText(path, "\"Название, с запятой\";Сумма;Пусто\r\n\"Строка\r\nвторая; с \"\"кавычкой\"\"\";00123;\r\n=2+2;-10;@name\r\n", new UTF8Encoding(true));
        var rows = ConversionContentReader.Read(path).Sections[0].Rows;
        Assert.Equal("Название, с запятой", rows[0][0]); Assert.Equal("Строка\r\nвторая; с \"кавычкой\"", rows[1][0]);
        Assert.Equal("00123", rows[1][1]); Assert.Equal("", rows[1][2]); Assert.Equal("=2+2", rows[2][0]);
        File.WriteAllText(path, "a;b\n\"unfinished"); Assert.Throws<InvalidDataException>(() => ConversionContentReader.Read(path));
    }

    [Fact]
    public void XlsxReadsSharedStringsSparseCellsEverySheetAndCachedFormula()
    {
        var path = MultiSheet(); var content = ConversionContentReader.Read(path);
        Assert.Equal(2, content.Sections.Count); Assert.Equal("Первый", content.Sections[0].Name);
        Assert.Equal(new[] { ConversionFixture.Probe, "", "1234.56" }, content.Sections[0].Rows[0]);
        Assert.Empty(content.Sections[0].Rows[1]); Assert.Equal("3", content.Sections[0].Rows[2][0]);
        Assert.Equal("00042", content.Sections[1].Rows[0][0]); Assert.Contains(content.Warnings, w => w.Contains("Формулы"));
    }

    [Fact]
    public async Task MultiSheetExportContainsEverySheetInsteadOfSilentlyTakingFirst()
    {
        var result = await fixture.Converter().ConvertAsync(MultiSheet(), fixture.NewDirectory(), new("csv"));
        var text = File.ReadAllText(result.Path);
        Assert.Contains("Первый", text); Assert.Contains("Второй", text); Assert.Contains("00042", text);
        Assert.Contains(result.Warnings, w => w.Contains("Все разделы"));
    }

    [Fact]
    public async Task BadMagicSameFormatAndMissingFormulaCacheAreRejected()
    {
        var directory = fixture.NewDirectory(); var bad = Path.Combine(directory, "false.pdf"); File.WriteAllText(bad, "not a PDF");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Converter().ConvertAsync(bad, directory, new("docx")));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Converter().ConvertAsync(Path.Combine(fixture.Directory, "source.csv"), directory, new("csv")));
        var path = MultiSheet();
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            zip.GetEntry("xl/worksheets/sheet1.xml")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("xl/worksheets/sheet1.xml").Open());
            writer.Write("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1"><f>1+2</f></c></row></sheetData></worksheet>""");
        }
        Assert.Throws<InvalidDataException>(() => ConversionContentReader.Read(path));
    }

    [NativeTheory]
    [InlineData("docx")][InlineData("xlsx")][InlineData("csv")]
    public async Task MixedPdfUsesOcrOnlyForScannedPageAndReportsLosses(string target)
    {
        var path = Path.Combine(fixture.NewDirectory(), "mixed.pdf");
        using (var font = SKTypeface.FromFile(Path.Combine(AppContext.BaseDirectory, "Assets", "DejaVuSans.ttf")))
        using (var paint = new SKPaint { Typeface = font, TextSize = 48, Color = SKColors.Black, IsAntialias = true })
        using (var surface = SKSurface.Create(new SKImageInfo(1400, 500)))
        {
            surface.Canvas.Clear(SKColors.White);
            surface.Canvas.DrawText("СКАН СЧЁТА", 80, 120, paint); surface.Canvas.DrawText("Сумма 1234.56", 80, 220, paint);
            surface.Canvas.DrawText("SCANPROBE72", 80, 320, paint);
            using var image = surface.Snapshot(); using var pdf = SKDocument.CreatePdf(path);
            var page = pdf.BeginPage(600, 400); paint.TextSize = 20; page.DrawText("TEXTLAYERPROBE72", 40, 100, paint); pdf.EndPage();
            page = pdf.BeginPage(600, 400); page.DrawImage(image, new SKRect(0, 30, 600, 244)); pdf.EndPage(); pdf.Close();
        }
        var result = await fixture.Converter().ConvertAsync(path, fixture.NewDirectory(), new(target));
        Assert.Equal(1, result.OcrPages);
        var text = string.Join(' ', ConversionContentReader.Read(result.Path).FlatRows().SelectMany(r => r));
        Assert.Contains("TEXTLAYERPROBE72", text); Assert.True(text.Contains("1234.56") && text.Contains("Сумма"), text);
        Assert.Contains(result.Warnings, w => w.Contains("OCR использован"));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Converter().ConvertAsync(path, fixture.NewDirectory(), new("csv", "off")));
    }

    private string MultiSheet()
    {
        var path = Path.Combine(fixture.NewDirectory(), "sheets.xlsx");
        File.Copy(Path.Combine(fixture.Directory, "source.xlsx"), path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        void Part(string name, string text) { zip.GetEntry(name)?.Delete(); using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(text); }
        Part("xl/workbook.xml", """<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Первый" sheetId="1" r:id="r1"/><sheet name="Второй" sheetId="2" r:id="r2"/></sheets></workbook>""");
        Part("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="r1" Target="worksheets/sheet1.xml"/><Relationship Id="r2" Target="/xl/worksheets/second.xml"/></Relationships>""");
        Part("xl/sharedStrings.xml", $"""<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>{ConversionFixture.Probe}</t></si></sst>""");
        Part("xl/worksheets/sheet1.xml", """<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c><c r="C1"><v>1234.56</v></c></row><row r="3"><c r="A3"><f>1+2</f><v>3</v></c></row></sheetData></worksheet>""");
        Part("xl/worksheets/second.xml", """<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>00042</t></is></c></row></sheetData></worksheet>""");
        return path;
    }
}
