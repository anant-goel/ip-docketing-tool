namespace IPDocketing.WinUI.Views;

/// <summary>
/// The JavaScript that actually drives the embedded IP India pages.
///
/// WHY THIS EXISTS AS A REWRITE (phase 31)
///
/// The previous version guessed CSS selectors:
///
///     document.querySelector('input[id*="mark" i], input[name*="mark" i]')
///
/// and it half-worked, which is the worst outcome - the Class box filled while
/// the Wordmark box stayed empty. Two reasons:
///
///  1. `querySelector` returns the FIRST match in document order. That page has
///     hidden ASP.NET WebForms inputs and other controls whose id or name
///     contains "mark" (the Well Known Marks / Prohibited Marks tab plumbing
///     sits in the same document), so the selector matched something invisible
///     and wrote the value into a field nobody can see. "class" happened to be
///     unambiguous, which is exactly why only that one appeared to work.
///
///  2. Even a correctly filled pair of text boxes cannot search, because the
///     page has two radio groups that were never touched - Search Type
///     (Wordmark / Vienna Code / Phonetic) and the wordmark match criteria
///     (Start With / Contains / Match With). Both start unselected.
///
/// WHAT IT DOES INSTEAD
///
/// It discovers fields the way a person does: by the label text next to them.
/// Every visible input is scored against the words in its associated label,
/// its `label[for]`, aria-label, placeholder, name and id, plus the text of the
/// nearest containing row or cell. Hidden, disabled and read-only inputs are
/// discarded before scoring, so an invisible field can never win.
///
/// Label text also survives redesigns far better than element ids do. IP India
/// has renumbered its ASP.NET control ids more than once; it has not renamed
/// the word "Wordmark" on screen.
///
/// CAPTCHA IS EXPLICITLY EXCLUDED. Any field whose label mentions captcha, an
/// answer, or the arithmetic prompt is dropped from the candidate set before
/// anything is scored, and the submit button is never clicked by the fill path.
/// The person solves the challenge and presses Search themselves. This only
/// types values the person already entered into this app.
///
/// Values are written through the native property setter and followed by
/// input/change/blur events, so ASP.NET WebForms postback hooks and any
/// framework-controlled input both observe the change. Assigning `.value`
/// directly, as the old code did, is silently ignored by some controlled
/// inputs - the box looks filled and submits empty.
/// </summary>
internal static class PortalScripts
{
    /// <summary>Token replaced with a JSON payload before execution.</summary>
    public const string PayloadToken = "%%PAYLOAD%%";

    /// <summary>
    /// Shared helpers: document/frame walking, visibility, label extraction and
    /// value setting. Prepended to each of the scripts below.
    /// </summary>
    private const string Helpers = """
        function ipdDocuments() {
            var list = [document];
            try {
                document.querySelectorAll('iframe, frame').forEach(function (f) {
                    try { if (f.contentDocument) list.push(f.contentDocument); } catch (e) { }
                });
            } catch (e) { }
            return list;
        }

        function ipdVisible(el) {
            if (!el) return false;
            if (el.type === 'hidden') return false;
            if (el.disabled || el.readOnly) return false;
            try {
                var view = el.ownerDocument.defaultView || window;
                var s = view.getComputedStyle(el);
                if (s.display === 'none' || s.visibility === 'hidden' || s.opacity === '0') return false;
            } catch (e) { }
            return el.offsetParent !== null || el.getClientRects().length > 0;
        }

        function ipdLabelText(el) {
            var parts = [];
            try {
                if (el.id) {
                    var esc = (window.CSS && CSS.escape) ? CSS.escape(el.id) : el.id;
                    var lab = el.ownerDocument.querySelector('label[for="' + esc + '"]');
                    if (lab) parts.push(lab.innerText || '');
                }
            } catch (e) { }
            var wrapping = el.closest ? el.closest('label') : null;
            if (wrapping) parts.push(wrapping.innerText || '');
            if (el.getAttribute && el.getAttribute('aria-label')) parts.push(el.getAttribute('aria-label'));
            if (el.placeholder) parts.push(el.placeholder);
            if (el.title) parts.push(el.title);
            if (el.value && el.type === 'radio') parts.push(el.value);
            if (el.name) parts.push(el.name);
            if (el.id) parts.push(el.id);

            // Nearest ancestor that carries readable text - a table row or cell
            // on this site, a div elsewhere. Capped so a whole page section
            // can't drown out the field's own label.
            var node = el, hops = 0;
            while (node && hops < 7) {
                node = node.parentElement; hops++;
                if (!node) break;
                var tag = node.tagName;
                if (tag === 'TR' || tag === 'TD' || tag === 'TH' || tag === 'LI' ||
                    tag === 'DIV' || tag === 'P' || tag === 'FIELDSET') {
                    var t = (node.innerText || '').trim();
                    if (t.length > 0 && t.length < 200) { parts.push(t); break; }
                }
            }

            // Immediately preceding text, for layouts that put the caption in a
            // bare text node rather than a label element.
            try {
                var prev = el.previousElementSibling;
                if (prev && prev.innerText) parts.push(prev.innerText);
            } catch (e) { }

            return parts.join(' ').replace(/\s+/g, ' ').toLowerCase();
        }

        // Anything matching this is never written to, never clicked, and never
        // offered as a candidate. The CAPTCHA stays a manual step.
        function ipdIsCaptcha(text) {
            return /captcha|enter answer|enter the last|last number|verification code|security code|are you human|robot/.test(text);
        }

        function ipdSetValue(el, value) {
            try {
                var view = el.ownerDocument.defaultView || window;
                var proto = el.tagName === 'SELECT' ? view.HTMLSelectElement.prototype
                          : el.tagName === 'TEXTAREA' ? view.HTMLTextAreaElement.prototype
                          : view.HTMLInputElement.prototype;
                var desc = Object.getOwnPropertyDescriptor(proto, 'value');
                if (desc && desc.set) { desc.set.call(el, value); } else { el.value = value; }
            } catch (e) { el.value = value; }

            try { el.focus(); } catch (e) { }
            ['input', 'change', 'keyup', 'blur'].forEach(function (name) {
                try { el.dispatchEvent(new Event(name, { bubbles: true })); } catch (e) { }
            });
        }

        // Scores a candidate against a set of weighted keywords. Negative
        // keywords veto outright - this is what stops the Wordmark box being
        // matched by "Well Known Marks" plumbing.
        function ipdScore(text, positives, negatives) {
            for (var n = 0; n < negatives.length; n++) {
                if (text.indexOf(negatives[n]) !== -1) return -1;
            }
            var score = 0;
            for (var p = 0; p < positives.length; p++) {
                if (text.indexOf(positives[p][0]) !== -1) score += positives[p][1];
            }
            return score;
        }

        function ipdBestField(selector, positives, negatives) {
            var best = null, bestScore = 0;
            ipdDocuments().forEach(function (doc) {
                doc.querySelectorAll(selector).forEach(function (el) {
                    if (!ipdVisible(el)) return;
                    var text = ipdLabelText(el);
                    if (ipdIsCaptcha(text)) return;
                    var score = ipdScore(text, positives, negatives);
                    if (score > bestScore) { bestScore = score; best = el; }
                });
            });
            return best;
        }

        function ipdClickRadio(groupWords, optionWords) {
            var best = null, bestScore = 0;
            ipdDocuments().forEach(function (doc) {
                doc.querySelectorAll('input[type="radio"]').forEach(function (el) {
                    if (!ipdVisible(el)) return;
                    var text = ipdLabelText(el);
                    if (ipdIsCaptcha(text)) return;

                    var score = 0;
                    optionWords.forEach(function (w) { if (text.indexOf(w) !== -1) score += 10; });
                    if (score === 0) return;
                    groupWords.forEach(function (w) { if (text.indexOf(w) !== -1) score += 3; });
                    if (score > bestScore) { bestScore = score; best = el; }
                });
            });
            if (!best) return false;
            try {
                best.checked = true;
                best.dispatchEvent(new Event('change', { bubbles: true }));
                best.click();
            } catch (e) { return false; }
            return true;
        }
        """;

    /// <summary>
    /// Fills the trademark search form. Payload:
    /// { mark, tmClass, searchType, criteria }.
    /// Returns JSON: { filled: [], missing: [], note }.
    /// </summary>
    public const string FillTrademarkSearch = Helpers + """

        (function () {
            var payload = %%PAYLOAD%%;
            var filled = [], missing = [];

            // Search type first: on this page the wordmark row only becomes
            // interactive once the Wordmark radio is selected, so filling the
            // text box before choosing the type can be discarded by the page.
            if (payload.searchType) {
                if (ipdClickRadio(['search type'], [payload.searchType])) filled.push('search type');
                else missing.push('search type radio');
            }

            if (payload.criteria) {
                if (ipdClickRadio(['wordmark'], [payload.criteria])) filled.push('match criteria');
                else missing.push('match criteria radio');
            }

            if (payload.mark) {
                // "wordmark" is worth far more than a bare "mark", and anything
                // that looks like a tab or a different register vetoes the
                // candidate entirely.
                var markField = ipdBestField(
                    'input[type="text"], input:not([type]), input[type="search"], textarea',
                    [['wordmark', 20], ['word mark', 20], ['search term', 8], ['mark', 5], ['keyword', 4]],
                    ['captcha', 'class', 'vienna', 'well known', 'prohibited', 'proprietor',
                     'application', 'user', 'password', 'email', 'inn', 'journal']);

                // Fallback: the first visible empty text box that is not the
                // class field and not the CAPTCHA answer. On a form this small
                // that is nearly always the wordmark box.
                if (!markField) {
                    ipdDocuments().forEach(function (doc) {
                        if (markField) return;
                        doc.querySelectorAll('input[type="text"], input:not([type])').forEach(function (el) {
                            if (markField || !ipdVisible(el)) return;
                            var t = ipdLabelText(el);
                            if (ipdIsCaptcha(t)) return;
                            if (t.indexOf('class') !== -1) return;
                            if ((el.value || '').length > 0) return;
                            markField = el;
                        });
                    });
                }

                if (markField) { ipdSetValue(markField, payload.mark); filled.push('mark'); }
                else missing.push('mark field');
            }

            if (payload.tmClass) {
                var classField = ipdBestField(
                    'input[type="text"], input:not([type]), select',
                    [['class', 20]],
                    ['captcha', 'wordmark', 'vienna', 'classification of', 'well known']);
                if (classField) { ipdSetValue(classField, payload.tmClass); filled.push('class'); }
                else missing.push('class field');
            }

            return JSON.stringify({
                filled: filled,
                missing: missing,
                note: 'CAPTCHA and the Search button were deliberately not touched.'
            });
        })();
        """;

    /// <summary>
    /// Fills an OTP field. Payload: { otp }. Returns JSON { filled: bool }.
    /// </summary>
    public const string FillOtp = Helpers + """

        (function () {
            var payload = %%PAYLOAD%%;
            var field = ipdBestField(
                'input[type="text"], input[type="tel"], input[type="number"], input[type="password"], input:not([type])',
                [['otp', 20], ['one time password', 20], ['one-time', 15], ['verification', 6]],
                ['captcha', 'class', 'wordmark']);
            if (!field) return JSON.stringify({ filled: false });
            ipdSetValue(field, payload.otp);
            return JSON.stringify({ filled: true });
        })();
        """;

    /// <summary>
    /// Reports every field the page exposes, so a selector problem can be
    /// diagnosed from inside the app rather than by opening DevTools. This is
    /// read-only - it writes nothing and clicks nothing.
    /// </summary>
    public const string DiagnoseForm = Helpers + """

        (function () {
            var fields = [], radios = [], buttons = [];

            ipdDocuments().forEach(function (doc) {
                doc.querySelectorAll('input, select, textarea').forEach(function (el) {
                    if (!ipdVisible(el)) return;
                    var type = (el.type || el.tagName).toLowerCase();
                    var label = ipdLabelText(el).slice(0, 110);
                    var entry = {
                        type: type,
                        id: el.id || '',
                        name: el.name || '',
                        value: (el.value || '').slice(0, 40),
                        label: label,
                        captcha: ipdIsCaptcha(label)
                    };
                    if (type === 'radio' || type === 'checkbox') radios.push(entry);
                    else if (type === 'submit' || type === 'button') buttons.push(entry);
                    else fields.push(entry);
                });

                doc.querySelectorAll('button').forEach(function (el) {
                    if (!ipdVisible(el)) return;
                    buttons.push({
                        type: 'button', id: el.id || '', name: el.name || '',
                        value: (el.innerText || '').trim().slice(0, 40),
                        label: '', captcha: false
                    });
                });
            });

            return JSON.stringify({
                url: location.href,
                fields: fields.slice(0, 40),
                radios: radios.slice(0, 40),
                buttons: buttons.slice(0, 20)
            });
        })();
        """;

    /// <summary>
    /// Lists the downloadable documents shown on the current status page.
    ///
    /// Anchors on anchor elements whose href or text looks like a document
    /// rather than on any particular table layout, then walks up to the
    /// containing row to pick up the label and date sitting beside the link.
    /// That survives a redesign; a row-index-based reader would not.
    ///
    /// Navigation links, sort headers and pagination are excluded - a status
    /// page is mostly links, and treating all of them as documents would fill
    /// the docket with copies of the page's own chrome.
    /// </summary>
    public const string ExtractDocumentLinks = Helpers + """

        (function () {
            var docs = [];
            var seen = {};

            function rowContext(el) {
                var node = el, hops = 0;
                while (node && hops < 5) {
                    node = node.parentElement; hops++;
                    if (!node) break;
                    if (node.tagName === 'TR' || node.tagName === 'LI' || node.tagName === 'DIV') {
                        var t = (node.innerText || '').replace(/\s+/g, ' ').trim();
                        if (t.length > 0 && t.length < 300) return t;
                    }
                }
                return '';
            }

            ipdDocuments().forEach(function (doc) {
                doc.querySelectorAll('a[href]').forEach(function (a) {
                    if (!ipdVisible(a)) return;

                    var href = a.href || '';
                    var text = (a.innerText || '').replace(/\s+/g, ' ').trim();
                    var combined = (href + ' ' + text).toLowerCase();

                    if (href.indexOf('javascript:') === 0 && !/postback/i.test(href)) return;
                    if (href.indexOf('#') === 0) return;

                    // Must look like a document, by extension, by a viewer/
                    // download endpoint, or by the label naming a known filing.
                    var looksLikeFile =
                        /\.(pdf|tiff?|jpe?g|png|docx?|zip)(\?|$)/i.test(href) ||
                        /download|viewdoc|getdoc|showdoc|attachment|document|certificate/i.test(href) ||
                        /examination report|hearing notice|show cause|order|certificate|reply|affidavit|evidence|counter statement|notice of opposition|tm-\d/i.test(text);

                    if (!looksLikeFile) return;

                    // Page chrome, not filings.
                    if (/^(home|back|next|previous|first|last|logout|login|help|print|search|sort|page \d+)$/i.test(text)) return;
                    if (text.length === 0 && !/\.(pdf|tiff?)(\?|$)/i.test(href)) return;

                    if (seen[href]) return;
                    seen[href] = true;

                    var context = rowContext(a);
                    var dateMatch = context.match(/\b(\d{1,2}[\/\-.]\d{1,2}[\/\-.]\d{2,4})\b/);

                    docs.push({
                        url: href,
                        label: text || 'Document',
                        context: context.slice(0, 200),
                        date: dateMatch ? dateMatch[1] : '',
                        combined: combined.slice(0, 300)
                    });
                });
            });

            return JSON.stringify({ url: location.href, documents: docs.slice(0, 60) });
        })();
        """;

    /// <summary>
    /// Downloads one file from inside the page's own session and returns it
    /// base64-encoded.
    ///
    /// This runs `fetch` in the page context with credentials included, so the
    /// request carries the session cookies you established when you signed in
    /// and solved the CAPTCHA. That is the whole reason it works: these
    /// documents are not public URLs, and fetching them from outside the
    /// browser would just return a login page.
    ///
    /// Capped at 25 MB. Marshalling a larger file through ExecuteScriptAsync as
    /// base64 means holding it three times over in memory, and a status-page
    /// attachment over that size is far more likely to be a wrong link than a
    /// real filing.
    ///
    /// Payload: { url }.
    /// </summary>
    public const string FetchFileAsBase64 = """

        (async function () {
            var payload = %%PAYLOAD%%;
            try {
                var response = await fetch(payload.url, {
                    credentials: 'include',
                    redirect: 'follow'
                });

                if (!response.ok)
                    return JSON.stringify({ ok: false, reason: 'HTTP ' + response.status });

                var type = response.headers.get('content-type') || '';

                // An HTML body here means the session lapsed and the server
                // returned the login page with a 200. Saving that as a PDF
                // produces a file that fails much later and far less obviously.
                if (/text\/html/i.test(type))
                    return JSON.stringify({ ok: false, reason: 'session-expired' });

                var buffer = await response.arrayBuffer();
                if (buffer.byteLength > 25 * 1024 * 1024)
                    return JSON.stringify({ ok: false, reason: 'too-large' });
                if (buffer.byteLength < 512)
                    return JSON.stringify({ ok: false, reason: 'empty' });

                var bytes = new Uint8Array(buffer);
                var binary = '';
                var chunk = 8192;
                for (var i = 0; i < bytes.length; i += chunk) {
                    binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
                }

                return JSON.stringify({
                    ok: true,
                    contentType: type,
                    size: buffer.byteLength,
                    data: btoa(binary)
                });
            } catch (e) {
                return JSON.stringify({ ok: false, reason: String(e) });
            }
        })();
        """;

    /// <summary>
    /// Drives the e-Register navigation you walked me through, one step per
    /// call, so the caller can wait for each page between steps.
    ///
    /// Payload: { step, value }. Steps, in order:
    ///   "tab"      - clicks "Trade Mark Application/Registered Mark"
    ///   "national" - selects the "National/IRDI Number" radio
    ///   "number"   - types the TM number into the number box (NOT the captcha)
    ///   "view"     - clicks View, only valid once the captcha has been typed
    ///
    /// Split into steps rather than run as one script because each transition
    /// is a postback: the next control does not exist in the DOM until the
    /// previous page has come back. A single script would find nothing.
    ///
    /// The captcha is never filled and "view" is only ever called by the caller
    /// after a human has typed the code.
    /// </summary>
    public const string EStatusStep = Helpers + """

        (function () {
            var payload = %%PAYLOAD%%;

            function clickByText(patterns, tags) {
                var found = null;
                ipdDocuments().forEach(function (doc) {
                    if (found) return;
                    doc.querySelectorAll(tags).forEach(function (el) {
                        if (found || !ipdVisible(el)) return;
                        var text = ((el.innerText || '') + ' ' + (el.value || '')).trim().toLowerCase();
                        if (ipdIsCaptcha(text)) return;
                        for (var i = 0; i < patterns.length; i++) {
                            if (text.indexOf(patterns[i]) !== -1) { found = el; return; }
                        }
                    });
                });
                if (!found) return false;
                try { found.click(); } catch (e) { return false; }
                return true;
            }

            if (payload.step === 'tab') {
                var ok = clickByText(
                    ['trade mark application/registered mark',
                     'trade mark application',
                     'registered mark'],
                    'a, button, input[type="button"], input[type="submit"], li');
                return JSON.stringify({ ok: ok, step: 'tab' });
            }

            if (payload.step === 'national') {
                // The two options are National/IRDI Number and International
                // Registration Number. Matching on "national" alone is safe;
                // "international" contains "national" as a substring, so the
                // negative check matters.
                var picked = false;
                ipdDocuments().forEach(function (doc) {
                    if (picked) return;
                    doc.querySelectorAll('input[type="radio"]').forEach(function (el) {
                        if (picked || !ipdVisible(el)) return;
                        var text = ipdLabelText(el);
                        if (text.indexOf('international') !== -1) return;
                        if (text.indexOf('national') === -1 && text.indexOf('irdi') === -1) return;
                        try {
                            el.checked = true;
                            el.dispatchEvent(new Event('change', { bubbles: true }));
                            el.click();
                            picked = true;
                        } catch (e) { }
                    });
                });
                return JSON.stringify({ ok: picked, step: 'national' });
            }

            if (payload.step === 'number') {
                var field = ipdBestField(
                    'input[type="text"], input:not([type])',
                    [['trade mark/application number', 25], ['application number', 20],
                     ['trade mark number', 20], ['enter number', 15], ['number', 4]],
                    ['captcha', 'answer', 'last number']);
                if (!field) return JSON.stringify({ ok: false, step: 'number', reason: 'no-field' });
                ipdSetValue(field, payload.value);
                return JSON.stringify({ ok: true, step: 'number' });
            }

            if (payload.step === 'view') {
                var ok = clickByText(['view'], 'input[type="submit"], input[type="button"], button, a');
                return JSON.stringify({ ok: ok, step: 'view' });
            }

            return JSON.stringify({ ok: false, reason: 'unknown-step' });
        })();
        """;

    /// <summary>
    /// Opens one of the modal panels under a result - "Uploaded Documents" or
    /// "Correspondence &amp; Notices" - and returns the rows inside it, each with
    /// its View link.
    ///
    /// From your screenshots the two modals have different shapes:
    ///   Uploaded Documents      : S.No | Document description | Document Date | View
    ///   Correspondence &amp; Notices: S.No | Corres. No | Corres. Date | Subject |
    ///                             Despatch No | Despatch Date | View
    /// so the description column is located by header name rather than by index.
    /// That is what lets the same script read both, and keeps working if a
    /// column is added.
    ///
    /// Payload: { panel: "documents" | "correspondence" }.
    /// </summary>
    public const string OpenResultPanel = Helpers + """

        (function () {
            var payload = %%PAYLOAD%%;

            // Closing an open modal so the next panel button underneath becomes
            // clickable again - from your screenshot the modal overlays the
            // buttons, so without this the second panel can never be opened.
            if (payload.panel === 'close') {
                var closed = false;
                ipdDocuments().forEach(function (doc) {
                    if (closed) return;
                    doc.querySelectorAll('button, a, span, div').forEach(function (el) {
                        if (closed || !ipdVisible(el)) return;
                        var text = (el.innerText || '').trim();
                        var cls = (el.className || '').toString().toLowerCase();
                        var label = (el.getAttribute('aria-label') || '').toLowerCase();
                        if (text === '×' || text === 'X' || text === 'x' ||
                            cls.indexOf('close') !== -1 || label.indexOf('close') !== -1 ||
                            el.getAttribute('data-dismiss') === 'modal') {
                            try { el.click(); closed = true; } catch (e) { }
                        }
                    });
                });
                return JSON.stringify({ opened: false, closed: closed, panel: 'close' });
            }

            var wanted = payload.panel === 'correspondence'
                ? ['correspondence', 'notices']
                : ['uploaded document', 'uploaded documents'];

            var opened = false;
            ipdDocuments().forEach(function (doc) {
                if (opened) return;
                doc.querySelectorAll('button, a, input[type="button"], input[type="submit"]').forEach(function (el) {
                    if (opened || !ipdVisible(el)) return;
                    var text = ((el.innerText || '') + ' ' + (el.value || '')).trim().toLowerCase();
                    for (var i = 0; i < wanted.length; i++) {
                        if (text.indexOf(wanted[i]) !== -1) {
                            try { el.click(); opened = true; } catch (e) { }
                            return;
                        }
                    }
                });
            });

            return JSON.stringify({ opened: opened, panel: payload.panel });
        })();
        """;

    /// <summary>
    /// Reads the rows of an open modal panel, pairing each View link with the
    /// description and date on its row.
    ///
    /// Column meaning is resolved from the header text, not from position:
    /// "Document description" in one modal, "Subject" in the other. Filing an
    /// examination report under the wrong label because a column index shifted
    /// is exactly the kind of silent error that makes a docket untrustworthy.
    /// </summary>
    public const string ReadOpenPanelRows = Helpers + """

        (function () {
            var rows = [];

            function cellText(cell) {
                return (cell.innerText || cell.textContent || '').replace(/\s+/g, ' ').trim();
            }

            ipdDocuments().forEach(function (doc) {
                doc.querySelectorAll('table').forEach(function (table) {
                    if (!ipdVisible(table)) return;

                    var trs = Array.prototype.slice.call(table.querySelectorAll('tr'));
                    if (trs.length < 2) return;

                    var headers = Array.prototype.map.call(
                        trs[0].querySelectorAll('th, td'),
                        function (c) { return cellText(c).toLowerCase(); });

                    // Only tables that actually carry a View link are panels.
                    if (!table.querySelector('a')) return;

                    function indexOfHeader(names) {
                        for (var h = 0; h < headers.length; h++) {
                            for (var n = 0; n < names.length; n++) {
                                if (headers[h].indexOf(names[n]) !== -1) return h;
                            }
                        }
                        return -1;
                    }

                    var descIndex = indexOfHeader(['document description', 'subject', 'description']);
                    var dateIndex = indexOfHeader(['document date', 'corres. date', 'corres date', 'despatch date', 'date']);
                    if (descIndex < 0) return;

                    for (var r = 1; r < trs.length; r++) {
                        var cells = trs[r].querySelectorAll('td');
                        if (cells.length === 0) continue;

                        var link = trs[r].querySelector('a[href]');
                        if (!link) continue;

                        var href = link.href || '';
                        var onclick = link.getAttribute('onclick') || '';

                        rows.push({
                            description: descIndex < cells.length ? cellText(cells[descIndex]) : '',
                            date: (dateIndex >= 0 && dateIndex < cells.length) ? cellText(cells[dateIndex]) : '',
                            url: href,
                            onclick: onclick.slice(0, 300),
                            linkText: cellText(link)
                        });
                    }
                });
            });

            return JSON.stringify({ rows: rows.slice(0, 60) });
        })();
        """;

    /// <summary>
    /// Reads the "Matching Trade Marks" result block - the status line above the
    /// table plus the single data row beneath it.
    ///
    /// The status is deliberately read from the labelled lines ("Status:",
    /// "Sub Status:") rather than from the table, because from your screenshot
    /// those sit ABOVE the table as loose text and carry the value that actually
    /// matters ("Accepted &amp; Advertised").
    /// </summary>
    public const string ReadStatusResult = Helpers + """

        (function () {
            var out = { status: '', subStatus: '', asOn: '', fields: {} };

            var body = '';
            ipdDocuments().forEach(function (doc) {
                body += ' ' + ((doc.body && doc.body.innerText) || '');
            });
            body = body.replace(/\s+/g, ' ');

            function after(label) {
                var i = body.toLowerCase().indexOf(label);
                if (i === -1) return '';
                var slice = body.substring(i + label.length, i + label.length + 90);
                // Stop at the next labelled field.
                return slice.split(/status\s*:|sub status\s*:|as on date\s*:|trade mark no/i)[0]
                            .replace(/^[:\s]+/, '').trim();
            }

            out.asOn = after('as on date :');
            out.subStatus = after('sub status:');
            out.status = after('status:');

            // The result table itself: one row of mark details.
            ipdDocuments().forEach(function (doc) {
                doc.querySelectorAll('table').forEach(function (table) {
                    if (!ipdVisible(table)) return;
                    var trs = table.querySelectorAll('tr');
                    if (trs.length < 2) return;

                    var headers = Array.prototype.map.call(
                        trs[0].querySelectorAll('th, td'),
                        function (c) { return (c.innerText || '').replace(/\s+/g, ' ').trim(); });

                    if (headers.join(' ').toLowerCase().indexOf('trade mark') === -1) return;

                    var cells = trs[1].querySelectorAll('td');
                    for (var i = 0; i < headers.length && i < cells.length; i++) {
                        var key = headers[i];
                        if (key) out.fields[key] = (cells[i].innerText || '').replace(/\s+/g, ' ').trim();
                    }
                });
            });

            return JSON.stringify(out);
        })();
        """;

    /// <summary>
    /// Reads every data table on the current page and returns headers plus rows.
    ///
    /// This is how a portfolio gets imported from your own CEFS / e-Filing
    /// account. You sign in yourself, navigate to your filings list, and this
    /// reads what is already rendered on screen - your own data, in your own
    /// authenticated session, no credentials touched and no other party's
    /// records involved.
    ///
    /// It is deliberately generic rather than targeted at one known layout.
    /// I have never seen the CEFS dashboard's DOM, and a hard-coded selector
    /// for it would be the same mistake as the old prior-art filler: it would
    /// half-work and quietly produce wrong data. Instead every table is
    /// returned with its headers, and the app asks you to confirm which column
    /// is which. That works on the CEFS list, on an e-Register result, or on
    /// any other HTML table, and it keeps being right when the page changes.
    ///
    /// Header detection tries a real thead first, then a first row made of th
    /// cells, then falls back to the first row if it looks like labels rather
    /// than data. Tables under three rows or two columns are skipped - those
    /// are layout tables, which older government pages use heavily.
    /// </summary>
    public const string ExtractTables = Helpers + """

        (function () {
            var tables = [];

            function cellText(cell) {
                var text = (cell.innerText || cell.textContent || '').replace(/\s+/g, ' ').trim();
                return text.slice(0, 300);
            }

            function looksLikeHeader(cells) {
                if (cells.length === 0) return false;
                var nonNumeric = 0;
                for (var i = 0; i < cells.length; i++) {
                    var t = cells[i];
                    if (t.length > 0 && !/^[\d\/\-\.\s]+$/.test(t)) nonNumeric++;
                }
                return nonNumeric >= Math.ceil(cells.length / 2);
            }

            ipdDocuments().forEach(function (doc) {
                doc.querySelectorAll('table').forEach(function (table, tableIndex) {
                    if (!ipdVisible(table)) return;

                    var allRows = Array.prototype.slice.call(table.querySelectorAll('tr'));
                    if (allRows.length < 3) return;

                    var headers = [];
                    var bodyRows = [];

                    var theadRow = table.querySelector('thead tr');
                    if (theadRow) {
                        headers = Array.prototype.map.call(
                            theadRow.querySelectorAll('th, td'), cellText);
                        bodyRows = allRows.filter(function (r) { return r !== theadRow; });
                    } else {
                        var first = allRows[0];
                        var firstCells = Array.prototype.map.call(
                            first.querySelectorAll('th, td'), cellText);
                        if (first.querySelectorAll('th').length > 0 || looksLikeHeader(firstCells)) {
                            headers = firstCells;
                            bodyRows = allRows.slice(1);
                        } else {
                            return; // no usable header row
                        }
                    }

                    if (headers.length < 2) return;

                    var rows = [];
                    bodyRows.forEach(function (tr) {
                        // Skip nested-table rows, which would otherwise be
                        // counted twice - once here and once for the inner table.
                        if (tr.closest('table') !== table) return;
                        var cells = Array.prototype.map.call(tr.querySelectorAll('td'), cellText);
                        if (cells.length === 0) return;
                        if (cells.join('').length === 0) return;
                        rows.push(cells.slice(0, headers.length));
                    });

                    if (rows.length === 0) return;

                    tables.push({
                        index: tableIndex,
                        caption: (table.caption ? cellText(table.caption) : ''),
                        headers: headers,
                        rowCount: rows.length,
                        rows: rows.slice(0, 500)
                    });
                });
            });

            // Widest-then-tallest first: the real data grid is almost always
            // the biggest table on the page.
            tables.sort(function (a, b) {
                if (b.headers.length !== a.headers.length) return b.headers.length - a.headers.length;
                return b.rowCount - a.rowCount;
            });

            return JSON.stringify({ url: location.href, tables: tables.slice(0, 8) });
        })();
        """;

    /// <summary>
    /// Enters an application number on the e-Status page and submits it. Used
    /// only by bulk fetch, inside a session the person has already unlocked.
    /// Payload: { number }.
    /// </summary>
    public const string SubmitApplicationNumber = Helpers + """

        (function () {
            var payload = %%PAYLOAD%%;
            var field = ipdBestField(
                'input[type="text"], input[type="number"], input:not([type])',
                [['application number', 20], ['application no', 20], ['app no', 14],
                 ['application', 8], ['number', 3]],
                ['captcha', 'wordmark', 'class', 'vienna']);
            if (!field) return JSON.stringify({ ok: false, reason: 'no-field' });

            ipdSetValue(field, payload.number);

            var button = null;
            ipdDocuments().forEach(function (doc) {
                if (button) return;
                doc.querySelectorAll('input[type="submit"], button').forEach(function (el) {
                    if (button || !ipdVisible(el)) return;
                    var text = ((el.value || '') + ' ' + (el.innerText || '') + ' ' + (el.id || '')).toLowerCase();
                    if (ipdIsCaptcha(text)) return;
                    if (/search|view|submit|show|status/.test(text)) button = el;
                });
            });

            if (button) { try { button.click(); } catch (e) { } }
            return JSON.stringify({ ok: true, submitted: !!button });
        })();
        """;
}
