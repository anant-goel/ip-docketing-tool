using IPDocketing.Core.Services;

namespace IPDocketing.WinUI.Services;

/// <summary>
/// Routes every document through the best reader available, in a fixed order of
/// trustworthiness:
///
///   1. PDF TEXT LAYER  - exact. These are the characters the publisher wrote.
///                        Always tried first, always preferred, and never
///                        overridden by an OCR guess.
///   2. TESSERACT       - used for pages with no text layer, when tesseract.exe
///                        is present. Markedly better than the Windows engine on
///                        the display type and coined words that fill a
///                        trademark journal.
///   3. WINDOWS OCR     - the always-available fallback. No install required.
///
/// The ordering matters and is not arbitrary. Running OCR over a page that
/// already has a text layer would replace exact characters with guesses - which
/// on a mark like "KWIK BRITE" is exactly how you lose a conflict you should
/// have caught.
///
/// The result reports which engines actually contributed, so nothing downstream
/// has to assume. The similarity matcher lowers its confidence for OCR-derived
/// text, and can only do that if it is told.
/// </summary>
public sealed class ChainedTextExtractor : IDocumentTextExtractor
{
    private readonly PdfTextExtractor _windows;
    private readonly TesseractTextExtractor? _tesseract;

    public bool SupportsOcr => true;

    /// <summary>Null when Tesseract isn't installed - surfaced in Settings.</summary>
    public TesseractTextExtractor? Tesseract => _tesseract;

    public bool TesseractAvailable => _tesseract is not null;

    public ChainedTextExtractor(PdfTextExtractor windows, TesseractTextExtractor? tesseract)
    {
        _windows = windows;
        _tesseract = tesseract;
    }

    public static ChainedTextExtractor Create(string? tesseractPath = null) =>
        new(new PdfTextExtractor(), TesseractTextExtractor.TryCreate(tesseractPath));

    public async Task<ExtractionResult> ExtractAsync(string pdfPath, CancellationToken ct = default)
    {
        // Pass 1: text layer, plus the Windows OCR fallback the base extractor
        // already performs for image-only pages.
        var baseResult = await _windows.ExtractAsync(pdfPath, ct);

        // Nothing more to do when every page had real text.
        if (baseResult.Method == ExtractionResult.TextLayer) return baseResult;

        // No Tesseract, so whatever Windows OCR managed is the answer.
        if (_tesseract is null) return baseResult;

        // Tesseract is available and some pages needed OCR. Re-run those pages
        // through it and merge, preferring text-layer content wherever it
        // exists.
        var pagedText = await _windows.ExtractPagesAsync(pdfPath, ct);
        var pagedOcr = await _tesseract.ExtractPagesAsync(pdfPath, ct);

        if (!pagedOcr.Pages.Any(p => p.Trim().Length > 0)) return baseResult;

        var merged = new List<string>();
        var usedTextLayer = 0;
        var usedOcr = 0;

        for (var i = 0; i < Math.Max(pagedText.PageCount, pagedOcr.PageCount); i++)
        {
            var layer = i < pagedText.PageCount ? pagedText.Pages[i] : string.Empty;
            var ocr = i < pagedOcr.PageCount ? pagedOcr.Pages[i] : string.Empty;

            if (layer.Trim().Length >= 80)
            {
                merged.Add(layer);
                usedTextLayer++;
            }
            else if (ocr.Trim().Length > 0)
            {
                merged.Add(ocr);
                usedOcr++;
            }
        }

        var method = usedOcr == 0 ? ExtractionResult.TextLayer
                   : usedTextLayer == 0 ? ExtractionResult.Ocr
                   : ExtractionResult.Mixed;

        return new ExtractionResult(
            string.Join("\n", merged),
            method,
            merged.Count,
            usedOcr,
            usedOcr > 0 ? $"{usedOcr} page(s) read by Tesseract; {usedTextLayer} from the text layer." : null);
    }

    public async Task<PagedExtractionResult> ExtractPagesAsync(string pdfPath, CancellationToken ct = default)
    {
        var layerPages = await _windows.ExtractPagesAsync(pdfPath, ct);

        // Page-level search wants speed over completeness; only fall through to
        // Tesseract when the text layer gave essentially nothing, because
        // OCRing hundreds of pages to answer "which page is this name on"
        // would take minutes per issue.
        var usable = layerPages.Pages.Count(p => p.Trim().Length >= 80);
        if (_tesseract is null || usable > layerPages.PageCount / 2) return layerPages;

        var ocrPages = await _tesseract.ExtractPagesAsync(pdfPath, ct);
        if (ocrPages.Pages.Count == 0) return layerPages;

        var merged = new List<string>();
        for (var i = 0; i < Math.Max(layerPages.PageCount, ocrPages.PageCount); i++)
        {
            var layer = i < layerPages.PageCount ? layerPages.Pages[i] : string.Empty;
            var ocr = i < ocrPages.PageCount ? ocrPages.Pages[i] : string.Empty;
            merged.Add(layer.Trim().Length >= 80 ? layer : ocr);
        }

        return new PagedExtractionResult(merged, ExtractionResult.Mixed,
            "Some pages were read by Tesseract - verify hits against the PDF.");
    }
}
