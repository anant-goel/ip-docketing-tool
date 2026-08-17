using IPDocketing.Core.Services;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace IPDocketing.WinUI.Services;

/// <summary>
/// Reads text out of a PDF, with OCR for pages that have no text layer.
///
/// TWO PATHS, IN ORDER OF TRUSTWORTHINESS
///
/// 1. TEXT LAYER (PdfPig). The Trade Marks Journal is typeset and published
///    digitally, so its PDFs carry a real text layer. Reading it gives the
///    exact characters the Registry published. This is the path that will
///    normally run, and it is fast - a 400-page issue in a few seconds.
///
/// 2. OCR (Windows.Data.Pdf + Windows.Media.Ocr). Only for pages where the
///    text layer is empty or near-empty, which happens with scanned older
///    issues and with device-mark pages that are pure image.
///
/// WHY WINDOWS' OWN OCR AND NOT TESSERACT
///
/// Windows ships an OCR engine and a PDF rasteriser as part of the OS. Using
/// them means no extra native binaries, no ~30 MB of language training data in
/// the publish output, nothing further for the trim target to reason about, and
/// no new way for the GitHub Actions build to fail. Tesseract would be more
/// accurate on difficult scans, but it is a native dependency with per-platform
/// binaries, and this app is already carrying more runtime than it needs.
///
/// If OCR is unavailable (a Windows install with no OCR language pack, which
/// does happen on trimmed server images), that is reported rather than silently
/// producing an empty result - a watch service that quietly reads nothing is
/// worse than one that says it could not read.
///
/// WHAT OCR OUTPUT IS AND IS NOT
///
/// OCR is a guess. On trademark journals specifically it is a *worse* guess
/// than usual, because marks are set in stylised display type that OCR engines
/// are not trained on, and because a mark is often a coined word with no
/// dictionary to fall back on. Every result reports its method so the parser
/// can lower its confidence scores accordingly, and so nothing downstream ever
/// treats an OCR read as equivalent to a text-layer read.
/// </summary>
public sealed class PdfTextExtractor : IDocumentTextExtractor
{
    public bool SupportsOcr => true;

    /// <summary>
    /// Below this many characters, a page is treated as having no usable text
    /// layer and is sent to OCR. Journal pages are dense, so a page with under
    /// 80 characters is either blank or an image.
    /// </summary>
    private const int MinCharsForTextLayer = 80;

    /// <summary>
    /// Rendering every page of a 400-page issue at OCR resolution is slow and
    /// memory-hungry. This caps how many pages per document get the OCR
    /// treatment; the rest are reported as skipped rather than pretended over.
    /// </summary>
    public int MaxOcrPages { get; set; } = 40;

    /// <summary>
    /// Render scale for OCR. Windows' engine wants roughly 300 DPI equivalent;
    /// PDF pages report at 72 DPI, so this is about 4x.
    /// </summary>
    private const double OcrRenderScale = 4.0;

    public async Task<ExtractionResult> ExtractAsync(string pdfPath, CancellationToken ct = default)
    {
        if (!File.Exists(pdfPath))
            return new ExtractionResult("", ExtractionResult.Failed, 0, 0, "The file no longer exists.");

        var pageTexts = new List<string?>();
        var pageCount = 0;

        // --- Pass 1: text layer ---------------------------------------
        try
        {
            await Task.Run(() =>
            {
                using var document = UglyToad.PdfPig.PdfDocument.Open(pdfPath);
                pageCount = document.NumberOfPages;

                foreach (var page in document.GetPages())
                {
                    ct.ThrowIfCancellationRequested();
                    var text = page.Text ?? string.Empty;
                    pageTexts.Add(text.Trim().Length >= MinCharsForTextLayer ? text : null);
                }
            }, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ExtractionResult("", ExtractionResult.Failed, 0, 0,
                $"The PDF could not be opened: {ex.Message}");
        }

        var needsOcr = pageTexts.Count(t => t is null);

        // Everything had a text layer - the good case, and the common one.
        if (needsOcr == 0)
            return new ExtractionResult(
                string.Join("\n", pageTexts.Where(t => t is not null)),
                ExtractionResult.TextLayer, pageCount, 0);

        // --- Pass 2: OCR the pages that had none -----------------------
        var engine = OcrEngine.TryCreateFromUserProfileLanguages() ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));

        if (engine is null)
        {
            var partial = string.Join("\n", pageTexts.Where(t => t is not null));
            return new ExtractionResult(partial,
                partial.Length > 0 ? ExtractionResult.TextLayer : ExtractionResult.Failed,
                pageCount, needsOcr,
                "Windows OCR is unavailable on this machine (no OCR language pack installed), so " +
                $"{needsOcr} image-only page(s) could not be read. Install an OCR language pack from " +
                "Settings > Time & language > Language, or add those marks by hand.");
        }

        var ocrPagesDone = 0;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(pdfPath);
            var pdf = await PdfDocument.LoadFromFileAsync(file);

            for (uint i = 0; i < pdf.PageCount && i < pageTexts.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (pageTexts[(int)i] is not null) continue;
                if (ocrPagesDone >= MaxOcrPages) break;

                using var page = pdf.GetPage(i);
                using var stream = new InMemoryRandomAccessStream();

                var options = new PdfPageRenderOptions
                {
                    DestinationWidth = (uint)(page.Size.Width * OcrRenderScale)
                };
                await page.RenderToStreamAsync(stream, options);

                var decoder = await BitmapDecoder.CreateAsync(stream);
                using var bitmap = await decoder.GetSoftwareBitmapAsync();

                var ocrResult = await engine.RecognizeAsync(bitmap);
                pageTexts[(int)i] = ocrResult.Text;
                ocrPagesDone++;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var partial = string.Join("\n", pageTexts.Where(t => t is not null));
            return new ExtractionResult(partial,
                partial.Length > 0 ? ExtractionResult.Mixed : ExtractionResult.Failed,
                pageCount, needsOcr,
                $"OCR failed part-way through: {ex.Message}");
        }

        var combined = string.Join("\n", pageTexts.Where(t => t is not null));
        var textLayerPages = pageCount - needsOcr;

        string? note = null;
        if (needsOcr > ocrPagesDone)
            note = $"{needsOcr - ocrPagesDone} image-only page(s) were left unread - " +
                   $"OCR is capped at {MaxOcrPages} pages per issue for speed.";

        return new ExtractionResult(
            combined,
            textLayerPages > 0 ? ExtractionResult.Mixed : ExtractionResult.Ocr,
            pageCount,
            needsOcr,
            note);
    }
}
