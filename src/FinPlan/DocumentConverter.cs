using System.Diagnostics;
using System.Globalization;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace FinPlan;

public sealed record ConversionOptions(string Target, string Ocr = "auto", string Languages = "rus+eng");
public sealed record ConversionResult(string Path, string Format, List<string> Warnings, int OcrPages);

public sealed class ConversionTools(string root)
{
    public string? Office { get; } = Locate("FINPLAN_SOFFICE", "soffice", ["/Applications/LibreOffice.app/Contents/MacOS/soffice", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.com"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe")]);
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

public sealed class DocumentConverter(ConversionTools tools)
{
    public async Task<ConversionResult> ConvertAsync(string input, string directory, ConversionOptions options, Action<string>? progress = null, CancellationToken cancellation = default)
    {
        ConversionContentReader.Validate(input);
        var source = ConversionContentReader.Format(input); var target = options.Target.ToLowerInvariant();
        if (!ConversionContentReader.Formats.Contains(target) || source == target) throw new ArgumentException("Выберите другой поддерживаемый выходной формат.");
        if (options.Ocr is not ("auto" or "force" or "off")) throw new ArgumentException("Неизвестный режим OCR.");
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "result." + target); var warnings = new List<string>(); var ocrPages = 0;
        progress?.Invoke("Проверяем содержимое исходного файла");
        try
        {
            if (target == "pdf")
            {
                if (tools.Office is null) throw new InvalidOperationException("Для преобразования в PDF установите LibreOffice. Путь можно задать через FINPLAN_SOFFICE.");
                // Parse first, even when using LibreOffice, so malformed CSV/OOXML is not silently repaired.
                var content = ConversionContentReader.Read(input);
                var officeInput = input;
                if (source == "csv")
                {
                    officeInput = Path.Combine(directory, "source.xlsx"); ConversionContentWriter.Xlsx(officeInput, content);
                    warnings.Add("CSV перенесён в таблицу как текстовые значения. Разбиение на страницы и оформление PDF создаются заново.");
                }
                var nativeDir = Path.Combine(directory, "native"); Directory.CreateDirectory(nativeDir);
                var profile = Path.Combine(directory, "office-profile"); Directory.CreateDirectory(profile);
                // A separate profile also prevents an already running office instance from swallowing the request.
                File.WriteAllText(Path.Combine(profile, "registrymodifications.xcu"), """<?xml version="1.0" encoding="UTF-8"?><oor:items xmlns:oor="http://openoffice.org/2001/registry"><item oor:path="/org.openoffice.Office.Common/Security/Scripting"><prop oor:name="MacroSecurityLevel" oor:op="fuse"><value>3</value></prop></item></oor:items>""");
                progress?.Invoke("Создаём PDF через LibreOffice");
                await ConversionTools.RunAsync(tools.Office, ["-env:UserInstallation=" + new Uri(profile + Path.DirectorySeparatorChar).AbsoluteUri,
                    "--headless", "--nologo", "--nodefault", "--norestore", "--convert-to", source == "docx" ? "pdf:writer_pdf_Export" : "pdf:calc_pdf_Export", "--outdir", nativeDir, officeInput], cancellation);
                var produced = Path.Combine(nativeDir, Path.GetFileNameWithoutExtension(officeInput) + ".pdf");
                if (!File.Exists(produced)) throw new InvalidDataException("LibreOffice не создал PDF. Документ может быть повреждён или защищён паролем.");
                File.Move(produced, output, true);
                warnings.Add("PDF создаётся движком LibreOffice; переносы страниц и доступные шрифты могут отличаться от Microsoft Office.");
                if (source == "xlsx") warnings.Add("PDF учитывает печатные области исходной книги; скрытые листы и строки не выводятся. Проверьте охват данных.");
            }
            else
            {
                ConversionContent content;
                if (source == "pdf")
                {
                    (content, ocrPages) = await ReadPdfAsync(input, directory, options, progress, cancellation);
                }
                else content = ConversionContentReader.Read(input);
                warnings.AddRange(content.Warnings);
                cancellation.ThrowIfCancellationRequested();
                progress?.Invoke("Записываем содержимое в " + target.ToUpperInvariant());
                switch (target)
                {
                    case "docx": ConversionContentWriter.Docx(output, content); break;
                    case "xlsx":
                        ConversionContentWriter.Xlsx(output, content);
                        warnings.Add("В XLSX значения записаны как текст, чтобы сохранить точные суммы и номера с начальными нулями.");
                        break;
                    case "csv":
                        DocumentFiles.Csv(output, content.FlatRows());
                        warnings.Add("CSV содержит UTF-8, разделитель «;» и значения ячеек; оформление и формулы не сохраняются. Поля, начинающиеся с =, +, - или @, открывайте в Excel как текст.");
                        break;
                }
                if (content.Sections.Count > 1 && target is "xlsx" or "csv") warnings.Add("Все разделы и листы записаны последовательно в одну таблицу с отдельной строкой названия каждого раздела.");
            }
            cancellation.ThrowIfCancellationRequested();
            ConversionContentReader.Validate(output);
            return new(output, target, warnings.Distinct().ToList(), ocrPages);
        }
        catch { if (File.Exists(output)) File.Delete(output); throw; }
    }

    private async Task<(ConversionContent Content, int OcrPages)> ReadPdfAsync(string path, string directory, ConversionOptions options, Action<string>? progress, CancellationToken cancellation)
    {
        using var pdf = PdfDocument.Open(path);
        if (pdf.NumberOfPages > 200) throw new InvalidDataException("Для конвертации PDF поддерживается до 200 страниц за одно задание.");
        var sections = new List<ContentSection>(); var warnings = new List<string> { "PDF не хранит структуру исходной таблицы или формулы. Строки и колонки восстановлены по расположению текста; проверьте их вручную. Изображения и исходная вёрстка не переносятся." };
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
                if (tools.Tesseract is null || tools.PdfToPpm is null) throw new InvalidOperationException("Для сканов установите Tesseract и Poppler (pdftoppm). Пути: FINPLAN_TESSERACT и FINPLAN_PDFTOPPM.");
                if (++ocrPages > 50) throw new InvalidDataException("OCR поддерживает до 50 страниц за задание. Разделите PDF на меньшие документы.");
                var available = await tools.LanguagesAsync(cancellation);
                var languages = options.Languages.Split('+', StringSplitOptions.RemoveEmptyEntries);
                if (languages.Length == 0 || languages.Any(l => !available.Contains(l))) throw new InvalidDataException("Не установлены выбранные языки OCR: " + options.Languages + ". Установите языковые данные Tesseract или измените выбор.");
                var image = Path.Combine(directory, "ocr-page"); var tsv = Path.Combine(directory, "ocr-text");
                try
                {
                    await ConversionTools.RunAsync(tools.PdfToPpm, ["-f", page.Number.ToString(CultureInfo.InvariantCulture), "-l", page.Number.ToString(CultureInfo.InvariantCulture), "-scale-to", "3500", "-singlefile", "-png", path, image], cancellation);
                    List<string> args = [image + ".png", tsv, "-l", options.Languages];
                    if (tools.Tessdata is not null) args.AddRange(["--tessdata-dir", tools.Tessdata]);
                    // A custom tessdata directory need only contain models, not Tesseract's configs/tsv file.
                    args.AddRange(["-c", "tessedit_create_tsv=1"]);
                    await ConversionTools.RunAsync(tools.Tesseract, args, cancellation);
                    if (!File.Exists(tsv + ".tsv")) throw new InvalidDataException("OCR не создал результат. Проверьте установку Tesseract и языковых данных.");
                    rows = ReadTsv(tsv + ".tsv");
                }
                finally
                {
                    foreach (var temp in new[] { image + ".png", tsv + ".tsv" }) if (File.Exists(temp)) File.Delete(temp);
                }
            }
            else rows = PositionRows(words.Select(w => new PositionedWord(w.Text, w.BoundingBox.Left, -w.BoundingBox.Top, w.BoundingBox.Right, w.BoundingBox.Height)));
            sections.Add(new($"Страница {page.Number}", rows));
            if (rows.Count == 0) warnings.Add($"На странице {page.Number} не найден текст. Страница может быть пустой или не поддаваться распознаванию.");
            if (sections.Sum(s => (long)s.Rows.Sum(r => r.Length)) > ConversionContentReader.MaxCells) throw new InvalidDataException("PDF содержит слишком много текстовых ячеек.");
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
            words.Add(new(f[11], left, top, left + double.Parse(f[8], CultureInfo.InvariantCulture), double.Parse(f[9], CultureInfo.InvariantCulture)));
        }
        return PositionRows(words);
    }
}
