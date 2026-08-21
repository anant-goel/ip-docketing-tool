using System.Diagnostics;
using System.Text;
using IPDocketing.Core.Services;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace IPDocketing.WinUI.Services;

/// <summary>
/// OCR through Tesseract, driven as a subprocess rather than through P/Invoke.
///
/// WHY A SUBPROCESS AND NOT A .NET BINDING
///
/// The obvious choice is the `Tesseract` NuGet wrapper (charlesw), but it ships
/// native binaries for x86 and x64 only - there is no win-arm64 asset. On a
/// Snapdragon X machine it fails at load time with a DllNotFoundException that
/// says nothing useful about why. `NAPS2.Tesseract.Binaries` is the one package
/// that publishes Windows arm64 builds, and what it publishes is the
/// command-line executable.
///
/// Running the executable also sidesteps the Visual C++ runtime dependency that
/// the in-process wrappers carry (they are built against the VS2022 redist,
/// which is not present on every machine), and it isolates crashes: a
/// segfault on a malformed page kills a child process instead of the app.
///
/// The cost is process-per-page overhead. That is real but acceptable here -
/// OCR only runs on pages with no text layer, which for the Journal is a small
/// minority, and each page is already hundreds of milliseconds of actual
/// recognition.
///
/// WHAT IT NEEDS
///
/// tesseract.exe and a tessdata folder containing at least eng.traineddata.
/// Neither is bundled: the binaries are ~30 MB and the language data another
/// ~15 MB, which would more than undo the publish trimming. The path is
/// configurable in Settings and auto-detected from the usual install locations.
///
/// If Tesseract is not present this reports that plainly rather than silently
/// producing nothing - see <see cref="IsAvailable"/>. The caller
/// (<see cref="ChainedTextExtractor"/>) then falls back to Windows OCR.
/// </summary>
public sealed class TesseractTextExtractor : IDocumentTextExtractor
{
    private readonly string _exePath;
    private readonly string? _tessDataPath;

    public bool SupportsOcr => true;

    /// <summary>Language codes passed to Tesseract. "eng" alone unless more traineddata is installed.</summary>
    public string Languages { get; set; } = "eng";

    /// <summary>
    /// Page segmentation mode. 1 = automatic with orientation and script
    /// detection, which suits Journal pages: multi-column, mixed type sizes,
    /// and occasionally rotated scans.
    /// </summary>
    public int PageSegmentationMode { get; set; } = 1;

    public int MaxOcrPages { get; set; } = 60;

    private const double RenderScale = 4.0;

    private TesseractTextExtractor(string exePath, string? tessDataPath)
    {
        _exePath = exePath;
        _tessDataPath = tessDataPath;
    }

    /// <summary>
    /// Locates Tesseract, or returns null. Checks an explicit path first, then
    /// PATH, then the standard installer locations for both architectures.
    /// </summary>
    public static TesseractTextExtractor? TryCreate(string? configuredExePath = null)
    {
        foreach (var candidate in CandidatePaths(configuredExePath))
        {
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate)) continue;

            var tessData = FindTessData(candidate);
            return new TesseractTextExtractor(candidate, tessData);
        }
        return null;
    }

    public static bool IsAvailable(string? configuredExePath = null) =>
        TryCreate(configuredExePath) is not null;

    /// <summary>Where the located binary lives, for display in Settings.</summary>
    public string ExecutablePath => _exePath;

    public string? TessDataPath => _tessDataPath;

    private static IEnumerable<string?> CandidatePaths(string? configured)
    {
        yield return configured;

        // Alongside the app, which is where the NAPS2 binaries package lands
        // its per-RID payload on publish.
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "tesseract.exe");
        yield return Path.Combine(baseDir, "Tesseract", "tesseract.exe");
        yield return Path.Combine(baseDir, "_tesseract", "tesseract.exe");

        // Standard installer locations.
        yield return @"C:\Program Files\Tesseract-OCR\tesseract.exe";
        yield return @"C:\Program Files (x86)\Tesseract-OCR\tesseract.exe";

        // Anything on PATH.
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string? candidate = null;
            try { candidate = Path.Combine(dir.Trim(), "tesseract.exe"); }
            catch { /* malformed PATH entry */ }
            if (candidate is not null) yield return candidate;
        }
    }

    private static string? FindTessData(string exePath)
    {
        var dir = Path.GetDirectoryName(exePath);
        if (dir is null) return null;

        foreach (var candidate in new[]
                 {
                     Path.Combine(dir, "tessdata"),
                     Path.Combine(dir, "..", "tessdata"),
                     Path.Combine(dir, "..", "share", "tessdata"),
                 })
        {
            if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        // TESSDATA_PREFIX is how Tesseract itself is told, so honour it.
        var prefix = Environment.GetEnvironmentVariable("TESSDATA_PREFIX");
        return Directory.Exists(prefix) ? prefix : null;
    }

    public async Task<ExtractionResult> ExtractAsync(string pdfPath, CancellationToken ct = default)
    {
        var paged = await ExtractPagesAsync(pdfPath, ct);
        return new ExtractionResult(
            string.Join("\n", paged.Pages),
            paged.Method,
            paged.PageCount,
            paged.Pages.Count(p => p.Length > 0),
            paged.Error);
    }

    public async Task<PagedExtractionResult> ExtractPagesAsync(string pdfPath, CancellationToken ct = default)
    {
        if (!File.Exists(pdfPath))
            return new PagedExtractionResult(new List<string>(), ExtractionResult.Failed,
                "The file no longer exists.");

        var pages = new List<string>();
        var workDir = Path.Combine(Path.GetTempPath(), "ipdocketing_ocr_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(workDir);

            var file = await StorageFile.GetFileFromPathAsync(pdfPath);
            var pdf = await PdfDocument.LoadFromFileAsync(file);

            var limit = Math.Min(pdf.PageCount, (uint)MaxOcrPages);

            for (uint i = 0; i < limit; i++)
            {
                ct.ThrowIfCancellationRequested();

                var imagePath = Path.Combine(workDir, $"page_{i:D4}.png");
                await RenderPageAsync(pdf, i, imagePath);

                var text = await RunTesseractAsync(imagePath, ct);
                pages.Add(text);

                try { File.Delete(imagePath); } catch { /* temp cleanup */ }
            }

            var note = pdf.PageCount > limit
                ? $"{pdf.PageCount - limit} page(s) beyond the {MaxOcrPages}-page OCR cap were not read."
                : null;

            return new PagedExtractionResult(pages, ExtractionResult.Ocr, note);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new PagedExtractionResult(pages, ExtractionResult.Failed, ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); } catch { }
        }
    }

    /// <summary>OCRs a standalone image or PDF that is not a Journal issue.</summary>
    public async Task<string> ExtractFromImageAsync(string imagePath, CancellationToken ct = default) =>
        await RunTesseractAsync(imagePath, ct);

    private static async Task RenderPageAsync(PdfDocument pdf, uint index, string outputPath)
    {
        using var page = pdf.GetPage(index);
        using var stream = new InMemoryRandomAccessStream();

        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
        {
            DestinationWidth = (uint)(page.Size.Width * RenderScale)
        });

        stream.Seek(0);
        using var fileStream = File.Create(outputPath);
        await stream.AsStreamForRead().CopyToAsync(fileStream);
    }

    /// <summary>
    /// Runs tesseract.exe against one image and returns stdout.
    ///
    /// "-" as the output tells Tesseract to write to stdout rather than a file,
    /// which avoids a second round of temp-file bookkeeping. stderr is captured
    /// separately and only surfaced on failure, because Tesseract writes
    /// progress chatter there on every successful run.
    /// </summary>
    private async Task<string> RunTesseractAsync(string imagePath, CancellationToken ct)
    {
        var arguments = new StringBuilder();
        arguments.Append('"').Append(imagePath).Append("\" - ");
        arguments.Append("-l ").Append(Languages).Append(' ');
        arguments.Append("--psm ").Append(PageSegmentationMode);

        if (!string.IsNullOrWhiteSpace(_tessDataPath))
            arguments.Append(" --tessdata-dir \"").Append(_tessDataPath).Append('"');

        var startInfo = new ProcessStartInfo
        {
            FileName = _exePath,
            Arguments = arguments.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            // A hung Tesseract must not hang the whole sync pass.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return string.Empty;
            }

            var stdout = await stdoutTask;

            if (process.ExitCode != 0)
            {
                var stderr = await stderrTask;
                System.Diagnostics.Debug.WriteLine(
                    $"Tesseract exited {process.ExitCode}: {stderr}");
                return string.Empty;
            }

            return stdout;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Tesseract run failed: {ex}");
            return string.Empty;
        }
    }
}
