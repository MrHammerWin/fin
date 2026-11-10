using System.Diagnostics;
using System.Text.Json;
using FinPlan;

var root = FindRoot();
var output = Path.Combine(root, "output", "runtime"); var seed = 42; var generate = false; var check = false; var open = true;
try
{
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--generate": generate = true; break;
            case "--output": output = Path.GetFullPath(args[++i]); break;
            case "--seed": seed = int.Parse(args[++i]); break;
            case "--check": check = true; open = false; break;
            case "--no-browser": open = false; break;
            case "--open": open = true; break;
            default: throw new ArgumentException("Неизвестный аргумент: " + args[i]);
        }
    }
    if (!generate && !check)
    {
        await DemoHost.RunAsync(root, output, open);
        return 0;
    }
    if (check) output = DemoStore.ResolveCurrentDirectory(output);
    var generator = new DemoGenerator(root);
    if (generate || !File.Exists(Path.Combine(output, "dataset.json")))
    {
        var result = generator.Generate(output, seed);
        Console.WriteLine($"Создано {result.Documents.Count} документов, {result.Counterparties.Count} контрагентов, {result.Obligations.Count} обязательств/планов.");
    }
    else
    {
        var data = JsonSerializer.Deserialize<Dataset>(File.ReadAllText(Path.Combine(output, "dataset.json")), JsonFiles.Options)!;
        if (check)
        {
            var (operations, plans, report) = DuplicateChecker.Analyze(output, data.Documents);
            data.Operations = operations; data.Obligations = data.Obligations.Where(o => o.Kind == "document").Concat(plans).ToList();
            FinanceCalculator.Calculate(data);
            Console.WriteLine($"Проверено {report.Summary["files"]} файлов. Повторы: документов {report.Documents.Count}, банковских строк {report.Operations.Count}, плановых строк {report.Plans.Count}. Конфликтов нет.");
        }
        if (!check) generator.RenderViewer(output, data);
    }
    var viewer = Path.Combine(output, "calendar_view.html");
    Console.WriteLine("ФинПлан: " + viewer);
    if (open) Process.Start(new ProcessStartInfo(viewer) { UseShellExecute = true });
    return 0;
}
catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or JsonException or FormatException or IndexOutOfRangeException)
{
    Console.Error.WriteLine(error.Message); return 1;
}

static string FindRoot()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Solution1.sln"))) return dir.FullName;
    throw new DirectoryNotFoundException("Запускайте приложение из каталога решения Solution1.");
}
