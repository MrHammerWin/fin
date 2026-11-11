using System.Diagnostics;
using System.Globalization;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace FinPlan;

public sealed record RecognitionOptions(string Ocr = "auto", string Languages = "eng+rus");

public sealed class RecognitionTools(string root)
{
    public string? Tesseract { get; } = Locate("FINPLAN_TESSERACT", "tesseract", [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR", "tesseract.exe")]);
    public string? PdfToPpm { get; } = Locate("FINPLAN_PDFTOPPM", "pdftoppm", []);
    public string? Tessdata { get; } = Environment.GetEnvironmentVariable("FINPLAN_TESSDATA") ?? (Directory.Exists(Path.Combine(root, ".local-tools", "tessdata")) ? Path.Combine(root, ".local-tools", "tessdata") : null);
    private static string? Locate(string variable, string name, string[] candidates)
    {
        var configured = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(p => Path.Combine(p, name + (OperatingSystem.IsWindows() ? ".exe" : "")));
        return candidates.Concat(paths).FirstOrDefault(File.Exists);
    }
    public async Task<string[]> LanguagesAsync(CancellationToken cancellation = default)
    {
        if (Tesseract is null) return [];
        List<string> args = ["--list-langs"];
        if (Tessdata is not null) args.AddRange(["--tessdata-dir", Tessdata]);
        var result = await RunAsync(Tesseract, args, cancellation);
        return result.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(s => s.Trim()).Where(s => s.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')).ToArray();
    }
    public static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Не удалось запустить движок обработки документов.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromMinutes(3));
        // Drain both pipes concurrently; tool output is never returned to the browser or logged with document text.
        var stdout = process.StandardOutput.ReadToEndAsync(cancellation); var stderr = process.StandardError.ReadToEndAsync(cancellation);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            if (cancellation.IsCancellationRequested) throw;
            throw new InvalidOperationException("Движок превысил время обработки одного файла или страницы (3 минуты).");
        }
        var output = await stdout; await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException("Движок не смог обработать документ. Проверьте, что файл открывается, и установлены нужные языки OCR.");
        return output;
    }
}

public sealed class PdfDocumentReader(RecognitionTools? tools)
{
    public async Task<(DocumentContent Content, int OcrPages)> ReadAsync(string path, string directory, RecognitionOptions options, Action<string>? progress, CancellationToken cancellation)
    {
        DocumentContentReader.Validate(path);
        if (options.Ocr is not ("auto" or "force" or "off")) throw new ArgumentException("Неизвестный режим OCR.");
        using var pdf = PdfDocument.Open(path);
        if (pdf.NumberOfPages != 1) throw new InvalidDataException("Шаблоны PDF проекта одностраничные. Загрузите каждый акт или договор отдельным файлом.");
        var sections = new List<ContentSection>(); var warnings = new List<string>();
        var ocrPages = 0;
        foreach (var page in pdf.GetPages())
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Invoke($"Читаем страницу {page.Number} из {pdf.NumberOfPages}");
            var words = page.GetWords(NearestNeighbourWordExtractor.Instance).ToList();
            var needOcr = options.Ocr == "force" || (words.Sum(w => w.Text.Count(char.IsLetterOrDigit)) < 10 && page.NumberOfImages > 0);
            List<string[]> rows;
            if (needOcr)
            {
                if (options.Ocr == "off") throw new InvalidDataException($"Страница {page.Number} содержит скан. Включите OCR.");
                if (tools is null || tools.Tesseract is null || tools.PdfToPpm is null) throw new InvalidOperationException("Для сканов установите Tesseract и Poppler (pdftoppm). Пути: FINPLAN_TESSERACT и FINPLAN_PDFTOPPM.");
                ocrPages++;
                var available = await tools.LanguagesAsync(cancellation);
                var languages = options.Languages.Split('+', StringSplitOptions.RemoveEmptyEntries);
                if (languages.Length == 0 || languages.Any(l => !available.Contains(l))) throw new InvalidDataException("Не установлены выбранные языки OCR: " + options.Languages + ". Установите языковые данные Tesseract или измените выбор.");
                var image = Path.Combine(directory, "ocr-page"); var tsv = Path.Combine(directory, "ocr-text");
                try
                {
                    await RecognitionTools.RunAsync(tools.PdfToPpm, ["-f", page.Number.ToString(CultureInfo.InvariantCulture), "-l", page.Number.ToString(CultureInfo.InvariantCulture), "-scale-to", "3500", "-singlefile", "-png", path, image], cancellation);
                    List<string> args = [image + ".png", tsv, "-l", options.Languages];
                    if (tools.Tessdata is not null) args.AddRange(["--tessdata-dir", tools.Tessdata]);
                    // A custom tessdata directory need only contain models, not Tesseract's configs/tsv file.
                    args.AddRange(["-c", "tessedit_create_tsv=1"]);
                    await RecognitionTools.RunAsync(tools.Tesseract, args, cancellation);
                    if (!File.Exists(tsv + ".tsv")) throw new InvalidDataException("OCR не создал результат. Проверьте установку Tesseract и языковых данных.");
                    rows = ReadTsv(tsv + ".tsv");
                }
                finally
                {
                    foreach (var temp in new[] { image + ".png", tsv + ".tsv" }) if (File.Exists(temp)) File.Delete(temp);
                }
            }
            else rows = PositionRows(words.Select(w => new PositionedWord(w.Text, w.BoundingBox.Left, -w.BoundingBox.Bottom, w.BoundingBox.Right, w.BoundingBox.Height)));
            sections.Add(new($"Страница {page.Number}", rows));
            if (rows.Count == 0) warnings.Add($"На странице {page.Number} не найден текст. Страница может быть пустой или не поддаваться распознаванию.");
            if (sections.Sum(s => (long)s.Rows.Sum(r => r.Length)) > DocumentContentReader.MaxCells) throw new InvalidDataException("PDF содержит слишком много текстовых ячеек.");
        }
        if (!sections.Any(s => s.Rows.Any(r => r.Any(v => !string.IsNullOrWhiteSpace(v))))) throw new InvalidDataException("В PDF не удалось извлечь текст. Попробуйте принудительный OCR или улучшите качество скана.");
        if (ocrPages > 0) warnings.Add($"OCR использован для {ocrPages} страниц. Возможны ошибки в числах, датах и названиях; распознанные данные требуют проверки.");
        return (new(sections, warnings), ocrPages);
    }

    private sealed record PositionedWord(string Text, double Left, double Top, double Right, double Height);
    private static List<string[]> PositionRows(IEnumerable<PositionedWord> words)
    {
        var lines = new List<List<PositionedWord>>();
        foreach (var word in words.OrderBy(w => w.Top).ThenBy(w => w.Left))
        {
            if (lines.Count == 0 || Math.Abs(lines[^1][0].Top - word.Top) > Math.Max(3, word.Height * .45)) lines.Add([]);
            lines[^1].Add(word);
        }
        return lines.Select(line =>
        {
            var cells = new List<string>(); double right = double.MinValue;
            foreach (var word in line.OrderBy(w => w.Left))
            {
                if (cells.Count == 0 || word.Left - right > Math.Max(12, word.Height * 1.5)) cells.Add(word.Text);
                else cells[^1] += " " + word.Text;
                right = word.Right;
            }
            return cells.ToArray();
        }).ToList();
    }
    private static List<string[]> ReadTsv(string path)
    {
        var words = new List<PositionedWord>();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var f = line.Split('\t', 12);
            if (f.Length != 12 || f[0] != "5" || string.IsNullOrWhiteSpace(f[11])) continue;
            var left = double.Parse(f[6], CultureInfo.InvariantCulture); var top = double.Parse(f[7], CultureInfo.InvariantCulture);
            words.Add(new(f[11], left, top + double.Parse(f[9], CultureInfo.InvariantCulture), left + double.Parse(f[8], CultureInfo.InvariantCulture), double.Parse(f[9], CultureInfo.InvariantCulture)));
        }
        return PositionRows(words);
    }
}
