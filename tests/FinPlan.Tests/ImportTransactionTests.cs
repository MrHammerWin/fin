using System.Text.Json;
using FinPlan;
using Xunit;

namespace FinPlan.Tests;

public sealed class ImportTransactionTests(ReaderFixture fixture) : IClassFixture<ReaderFixture>
{
    private static PreviewFile PreparePlan(string directory, Dataset data)
    {
        var path=Path.Combine(directory,"input-000.csv");List<string[]> rows=[DuplicateChecker.PlanHeader];
        var cp = data.Corpus!.Counterparties.First();
        for(var i=0;i<20;i++) rows.Add([$"P-FILE-{i:000}",cp.Id,cp.Name,"A-RUB","RUB","out","1234.56","2026-11-20","2026-11-20","From file"]);
        DocumentFiles.Csv(path,rows);return new("plan.csv","documents/import_test.csv",[],FinancialImport.Hash(path),FinancialTemplateReader.Read(path));
    }
    [Fact]
    public async Task ImportKeepsBulkFilesAndMeasuredSizeAndResizeStillWorksAfterRestart()
    {
        var output=fixture.NewDirectory();var store=new DemoStore(fixture.Root,output);var previous=await store.RegenerateAsync(91,25_000_000);
        var bulk=DocumentApi.ReadVolume(previous)!.Files.Where(f=>f.Kind!="control").ToList();var inputs=fixture.NewDirectory();
        var file=PreparePlan(inputs,previous.Data);var next=await store.ImportAsync(new(previous.Id,FinancialImport.Fingerprint(previous),[file]),inputs);
        Assert.Equal(440,next.Data.Obligations.Count);Assert.False(Directory.Exists(previous.Directory));
        var manifest=DocumentApi.ReadVolume(next)!;Assert.True(manifest.Summary.MeasuredTarget);
        Assert.Equal(BulkVolume.BytesIn(next.Directory),manifest.Summary.TotalBytes);Assert.Equal(manifest.Summary.TotalBytes,manifest.Summary.TargetBytes);
        foreach(var original in bulk){Assert.Contains(original,manifest.Files);Assert.Equal(original.Sha256,FinancialImport.Hash(Path.Combine(next.Directory,original.Path)));}
        var restarted=new DemoStore(fixture.Root,output);Assert.Equal(next.Id,restarted.Current.Id);
        var added=await restarted.ResizeAsync(next.Id,"add",1_000_000);Assert.False(added.Data.Volume!.MeasuredTarget);Assert.InRange(added.Data.Volume.TotalBytes-restarted.Current.Data.Volume!.LastChange!.BeforeBytes,1_000_000,101_000_000);
    }
    [Fact]
    public async Task CancelledOrChangedPreviewAndFinancialConflictLeaveCurrentDatasetUntouched()
    {
        var output=fixture.NewDirectory();var store=new DemoStore(fixture.Root,output);var current=await store.RegenerateAsync(92,20_000_000);
        var fingerprint=FinancialImport.Fingerprint(current);var pointer=File.ReadAllBytes(Path.Combine(output,"current.json"));var inputs=fixture.NewDirectory();var file=PreparePlan(inputs,current.Data);
        using(var cancel=new CancellationTokenSource()) await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.ImportAsync(new(current.Id,fingerprint,[file]),inputs,_=>cancel.Cancel(),cancel.Token));
        File.AppendAllText(Path.Combine(inputs,"input-000.csv"),"changed");await Assert.ThrowsAsync<InvalidDataException>(()=>store.ImportAsync(new(current.Id,fingerprint,[file]),inputs));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.ImportAsync(new(current.Id,"stale",[file]),inputs));
        var invoice=Path.Combine(inputs,"input-000.xlsx");var rows=DocumentContentReader.Read(Path.Combine(current.Directory,"documents/S-002.xlsx")).Sections[0].Rows;
        var value=rows.Single(r=>r[0]=="ИТОГО")[1];foreach(var row in rows)for(var i=0;i<row.Length;i++)if(row[i]==value)row[i]="9999999.00";
        DocumentFiles.Xlsx(invoice,rows);var conflict=new PreviewFile("invoice.xlsx","documents/S-002.xlsx",["documents/S-002.xlsx"],FinancialImport.Hash(invoice),FinancialTemplateReader.Read(invoice));
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.ImportAsync(new(current.Id,fingerprint,[conflict]),inputs));
        var unknown=new FinancialDocument{CanApply=false,Type="unrecognized"};await Assert.ThrowsAsync<InvalidDataException>(()=>store.ImportAsync(new(current.Id,fingerprint,[file with{Document=unknown}]),inputs));
        Assert.Equal(current.Id,store.Current.Id);Assert.Equal(pointer,File.ReadAllBytes(Path.Combine(output,"current.json")));Assert.Equal(fingerprint,FinancialImport.Fingerprint(store.Current));Assert.Single(Directory.GetDirectories(Path.Combine(output,"generations")));
    }
}
