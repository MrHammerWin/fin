using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FinPlan;
using Xunit;

namespace FinPlan.Tests;

public sealed class DemoFixture : IDisposable
{
    public string Output { get; } = Path.Combine(Path.GetTempPath(), "finplan-csharp-test-" + Guid.NewGuid());
    public Dataset Data { get; }
    public DemoFixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Solution1.sln"))) dir = dir.Parent;
        Data = new DemoGenerator(dir!.FullName).Generate(Output, 42);
    }
    public void Dispose() => Directory.Delete(Output, true);
}
public sealed class DocumentTests(DemoFixture fixture) : IClassFixture<DemoFixture>
{
    [Fact]
    public void PdfWriterReleasesFileBeforeImmediateExclusiveReadAndDelete()
    {
        var path = Path.Combine(Path.GetTempPath(), "finplan-pdf-lock-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            DocumentFiles.Pdf(path, ["PDF ownership regression", "Сумма 1234.56"]);
            using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Assert.True(file.Length > 0);
            File.Delete(path); Assert.False(File.Exists(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public void HundredFilesTwentyCounterpartiesAndAllLinksExist()
    {
        var d = fixture.Data;
        Assert.Equal(100, Directory.GetFiles(Path.Combine(fixture.Output, "documents")).Length); Assert.Equal(100, d.Documents.Count);
        Assert.Equal(20, d.Counterparties.Count); Assert.Equal(420, d.Obligations.Select(o => o.Id).Distinct().Count());
        Assert.Equal(240, d.Operations.Count); Assert.Equal(20, d.Accruals.Count);
        foreach (var o in d.Obligations) { Assert.NotEmpty(o.Sources); foreach (var source in o.Sources) Assert.True(File.Exists(Path.Combine(fixture.Output, source))); }
    }
    [Fact]
    public void AllRegistersContainTwentyUniqueOperationsAndARepeat()
    {
        foreach (var doc in fixture.Data.Documents.Where(d => d.Type is "bank_statement" or "plan"))
        {
            var rows = DocumentFiles.ReadRows(Path.Combine(fixture.Output, doc.Path));
            var header = doc.Type == "plan" ? DuplicateChecker.PlanHeader : DuplicateChecker.BankHeader;
            var entries = rows.Skip(rows.FindIndex(r => r.SequenceEqual(header)) + 1).ToList();
            Assert.Equal(21, entries.Count); Assert.Equal(20, entries.Select(r => r[0]).Distinct().Count());
        }
    }
    [Fact]
    public void OpenXmlAndPdfAreRealDocuments()
    {
        foreach (var doc in fixture.Data.Documents)
        {
            var path = Path.Combine(fixture.Output, doc.Path);
            if (path.EndsWith(".docx") || path.EndsWith(".xlsx")) { using var zip = ZipFile.OpenRead(path); foreach (var e in zip.Entries) { using var stream = e.Open(); XDocument.Load(stream); } }
            if (path.EndsWith(".pdf")) { var text = Encoding.Latin1.GetString(File.ReadAllBytes(path)); Assert.StartsWith("%PDF-", text); Assert.Contains("/ToUnicode", text); Assert.Contains("/FontFile2", text); }
        }
    }
    [Fact]
    public void RepeatsAreExcludedAndCalendarAndReportsMatchOriginalUniqueData()
    {
        var d = fixture.Data; var s = d.Duplicates.Summary;
        Assert.Equal(8, s["duplicate_files"]); Assert.Equal(273, s["bank_rows"]); Assert.Equal(33, s["duplicate_bank_rows"]); Assert.Equal(441, s["plan_rows"]); Assert.Equal(41, s["duplicate_plan_rows"]); Assert.Equal(0, s["conflicts"]);
        var original = DemoGenerator.Build(42); FinanceCalculator.Calculate(original);
        Assert.Equal(JsonSerializer.Serialize(original.Calendar, JsonFiles.Options), JsonSerializer.Serialize(d.Calendar, JsonFiles.Options));
        Assert.Equal(JsonSerializer.Serialize(original.Reports, JsonFiles.Options), JsonSerializer.Serialize(d.Reports, JsonFiles.Options));
        foreach (var c in d.Rates.Keys) Assert.Equal(d.Calendar[0].Currencies[c].Opening, d.Reports!.Currencies[c]["closing"]);
    }
    [Fact]
    public void RepeatedIdChangedAmountBlocksImportButSameAmountWithDifferentIdIsAccepted()
    {
        var temp = Path.Combine(Path.GetTempPath(), "finplan-conflict-" + Guid.NewGuid()); Directory.CreateDirectory(temp);
        try
        {
            List<string[]> rows = [DuplicateChecker.BankHeader, ["T1", "2026-10-10", "A-RUB", "RUB", "CP1", "Компания", "out", "100.00", "", "Оплата", "supplier_payments"], ["T1", "2026-10-10", "A-RUB", "RUB", "CP1", "Компания", "out", "200.00", "", "Оплата", "supplier_payments"] ];
            var doc = new Document { Path = "bank.csv", Type = "bank_statement" };
            DocumentFiles.Csv(Path.Combine(temp, doc.Path), rows);
            Assert.Throws<InvalidDataException>(() => DuplicateChecker.Analyze(temp, [doc]));
            Assert.True(File.Exists(Path.Combine(temp, "duplicates.json")));
            rows[2][0] = "T2"; rows[2][7] = "100.00"; DocumentFiles.Csv(Path.Combine(temp, doc.Path), rows);
            Assert.Equal(2, DuplicateChecker.Analyze(temp, [doc]).Operations.Count);
            rows[2][0] = "T1"; rows[2][7] = "100"; DocumentFiles.Csv(Path.Combine(temp, doc.Path), rows);
            Assert.Single(DuplicateChecker.Analyze(temp, [doc]).Operations);
        }
        finally { Directory.Delete(temp, true); }
    }
}
