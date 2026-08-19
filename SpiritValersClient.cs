using System.Net.Http.Headers;
using System.Text.Json;

namespace NevarisBuildCrafter;

internal static class SpiritValersClient
{
    private const string Origin = "https://spiritvalers.com";
    private const int PageSize = 100;
    private static readonly HttpClient Client = CreateClient();
    private static CancellationTokenSource _activeRequest;

    internal static async Task<IReadOnlyList<CommunityBuildSummary>> GetCommunityBuildsAsync(
        int requestedLimit, string search, CancellationToken cancellationToken)
    {
        int limit = Math.Clamp(requestedLimit, 25, 500);
        string filter = BuildSearchFilter(search);
        var results = new List<CommunityBuildSummary>(limit);
        for (int offset = 0; offset < limit; offset += PageSize)
        {
            int pageLimit = Math.Min(PageSize, limit - offset);
            string query = "select=id,name,class,author,updated_at&shape=list"
                + filter
                + $"&order=updated_at.desc&limit={pageLimit}&offset={offset}";
            using JsonDocument response = await GetJsonAsync($"{Origin}/api/builds?{query}", cancellationToken);
            if (response.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("SpiritValers returned an unexpected gallery response.");

            List<CommunityBuildSummary> page = JsonSerializer.Deserialize<List<CommunityBuildSummary>>(
                response.RootElement.GetRawText(), BuildStorage.JsonOptions) ?? new();
            results.AddRange(page.Where(row => !string.IsNullOrWhiteSpace(row.Id)));
            if (page.Count < pageLimit)
                break;
        }

        return results;
    }

    private static string BuildSearchFilter(string search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return string.Empty;

        // PostgREST uses these characters as filter grammar. Removing them keeps the user's
        // text inside the ILIKE value while preserving ordinary spaces and punctuation.
        string safe = new(search.Trim().Where(character =>
            character is not ('(' or ')' or '*' or ',' or '%')).ToArray());
        if (string.IsNullOrWhiteSpace(safe))
            return string.Empty;
        string pattern = "*" + Uri.EscapeDataString(safe) + "*";
        return $"&or=(name.ilike.{pattern},author.ilike.{pattern},class.ilike.{pattern})";
    }

    internal static async Task<BuildDocument> GetBuildAsync(string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out Guid parsed))
            throw new ArgumentException("The community build ID is not valid.", nameof(id));

        string safeId = Uri.EscapeDataString(parsed.ToString());
        using JsonDocument response = await GetJsonAsync(
            $"{Origin}/rest/v1/builds?id=eq.{safeId}&select=*&limit=1", cancellationToken);
        if (response.RootElement.ValueKind != JsonValueKind.Array || response.RootElement.GetArrayLength() == 0)
            throw new InvalidDataException("That community build is no longer public or could not be found.");

        JsonElement row = response.RootElement[0];
        if (!row.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The community build does not contain build data.");

        BuildDocument build = CommunityBuildParser.Parse(data);
        build.Id = Guid.NewGuid().ToString("N");
        build.SourceCommunityId = id;
        build.SourceAuthor = ReadString(row, "author");
        if (string.IsNullOrWhiteSpace(build.Name))
            build.Name = ReadString(row, "name");
        build.UpdatedUtc = DateTime.UtcNow;
        build.Normalize();
        return build;
    }

    internal static CancellationToken BeginRequest(out CancellationTokenSource source)
    {
        _activeRequest?.Cancel();
        _activeRequest?.Dispose();
        float configured = Plugin.Settings?.RequestTimeout.Value ?? 15f;
        source = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(configured, 5f, 60f)));
        _activeRequest = source;
        return source.Token;
    }

    internal static void Shutdown()
    {
        _activeRequest?.Cancel();
        _activeRequest?.Dispose();
        _activeRequest = null;
        Client.Dispose();
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NevarisBuildCrafter", Plugin.Version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;
}

internal static class CommunityBuildParser
{
    internal static BuildDocument Parse(JsonElement data)
    {
        var build = new BuildDocument
        {
            Name = String(data, "name", "Community build"),
            Class = String(data, "cls", "Warrior"),
            BaseClass = String(data, "base", string.Empty),
            BaseLevel = Integer(data, "lv", 100),
            JobLevel = Integer(data, "job", 50),
            Notes = String(data, "overview", string.Empty)
        };

        if (data.TryGetProperty("attr", out JsonElement attributes) && attributes.ValueKind == JsonValueKind.Object)
        {
            foreach (string key in BuildDocument.NewAttributes().Keys)
                build.Attributes[key] = Integer(attributes, key, 1);
        }

        if (data.TryGetProperty("eq", out JsonElement equipment) && equipment.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty slot in equipment.EnumerateObject())
            {
                if (slot.Value.ValueKind != JsonValueKind.Object)
                    continue;
                build.Equipment[slot.Name] = ParseItem(slot.Value);
            }
        }

        if (data.TryGetProperty("arti", out JsonElement artifacts) && artifacts.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty slot in artifacts.EnumerateObject())
            {
                if (slot.Value.ValueKind != JsonValueKind.Object)
                    continue;
                PlannedItem item = ParseItem(slot.Value);
                build.Artifacts[slot.Name] = new PlannedArtifact
                {
                    ItemId = item.ItemId,
                    DisplayName = item.DisplayName,
                    Refine = item.Refine,
                    Cards = item.Cards,
                    Substats = item.Substats,
                    GemId = String(slot.Value, "gem", string.Empty),
                    GemRefine = Integer(slot.Value, "gemRefine", 0)
                };
            }
        }

        if (data.TryGetProperty("skills", out JsonElement skills) && skills.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty skill in skills.EnumerateObject())
            {
                int level = skill.Value.ValueKind == JsonValueKind.Number && skill.Value.TryGetInt32(out int parsed)
                    ? parsed : 0;
                if (level > 0)
                    build.Skills[skill.Name] = level;
            }
        }

        build.Normalize();
        return build;
    }

    private static PlannedItem ParseItem(JsonElement value)
    {
        string id = String(value, "id", string.Empty);
        var item = new PlannedItem
        {
            ItemId = id,
            DisplayName = id,
            Refine = Integer(value, "refine", 0)
        };

        if (value.TryGetProperty("cards", out JsonElement cards) && cards.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement card in cards.EnumerateArray())
            {
                if (card.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(card.GetString()))
                    item.Cards.Add(card.GetString());
            }
        }

        if (value.TryGetProperty("subs", out JsonElement subs) && subs.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement sub in subs.EnumerateArray())
            {
                if (sub.ValueKind != JsonValueKind.Object)
                    continue;
                item.Substats.Add(new ImportedSubstat
                {
                    Stat = String(sub, "stat", string.Empty),
                    Base = Number(sub, "base", 0f),
                    Quality = String(sub, "q", string.Empty)
                });
            }
        }
        return item;
    }

    private static string String(JsonElement element, string property, string fallback) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback : fallback;

    private static int Integer(JsonElement element, string property, int fallback) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int parsed) ? parsed : fallback;

    private static float Number(JsonElement element, string property, float fallback) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetSingle(out float parsed) ? parsed : fallback;
}
