using System.Net.Http;
using System.Text.Json;

namespace IPDocketing.Core.Services;

/// <summary>
/// Looks up India Post PIN codes via api.postalpincode.in - a free, keyless
/// government-backed API (listed under Government/India in the public-apis
/// list). Used to auto-fill the State field from a PIN code instead of
/// relying on free-text entry, which is where typos/inconsistent state
/// names (that break search/filtering) come from.
///
/// Most of public-apis (weather, jokes, anime, currency, etc.) has nothing
/// to do with an IP docketing tool - this was the one entry actually
/// relevant to what this app does. Everything else in that list (email
/// sending, OCR, court data) needs either a paid key or is already covered
/// by the connector interfaces from earlier in this project.
/// </summary>
public class IndiaPincodeService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("IPDocketing/1.0 (+desktop docketing tool)");
        return client;
    }

    public record PincodeResult(string District, string State, string Region);

    /// <summary>Returns null if the PIN code isn't found or the lookup fails (network down, etc.) -
    /// callers should treat that as "couldn't auto-fill, let the person type it manually" rather than an error.</summary>
    public async Task<PincodeResult?> LookupAsync(string pincode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pincode) || pincode.Trim().Length != 6)
            return null;

        try
        {
            var response = await Http.GetAsync($"https://api.postalpincode.in/pincode/{pincode.Trim()}", ct);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            // API returns a JSON array with one object: { Status, Message, PostOffice: [...] }
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return null;

            var first = root[0];
            if (!first.TryGetProperty("Status", out var status) || status.GetString() != "Success")
                return null;
            if (!first.TryGetProperty("PostOffice", out var postOffices) || postOffices.GetArrayLength() == 0)
                return null;

            var office = postOffices[0];
            var district = office.TryGetProperty("District", out var d) ? d.GetString() ?? "" : "";
            var state = office.TryGetProperty("State", out var s) ? s.GetString() ?? "" : "";
            var region = office.TryGetProperty("Region", out var r) ? r.GetString() ?? "" : "";

            return string.IsNullOrWhiteSpace(state) ? null : new PincodeResult(district, state, region);
        }
        catch
        {
            // Network failure, timeout, malformed response, etc. - treat as "no result",
            // never let a lookup failure block the person from typing the state manually.
            return null;
        }
    }
}
