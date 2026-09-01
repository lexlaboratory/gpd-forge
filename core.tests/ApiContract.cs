// GPD Forge — reads and enforces tests/contract/api-contract.json. GPL-3.0-or-later.
//
// Deliberately hand-written rather than pulled in as a JSON-Schema package. The contract needs
// exactly four things (type unions, nullability, array item shapes, and enum membership) and the
// failure messages have to name the field and say what arrived — a schema library's "does not
// match #/properties/alerts/items/severity" is the kind of message that gets a guard switched off.
using System.Text.Json;

namespace GpdForge.Core.Tests;

/// <param name="MockOnly">
/// Declared to exist in the mock daemon and NOT in the real one. There is exactly one today
/// (<c>/telemetry/stream</c>, an SSE feed the mock offers so the UI can be developed against
/// something live; production polls). It is spelled out per route rather than handled by allowing
/// unknown extras, because "the mock may serve anything not in the contract" is how a phantom
/// endpoint gets built, passes E2E, and 404s in production.
/// </param>
public sealed record ContractRoute(string Method, string Path, JsonElement? Shape, bool MockOnly = false);

public static class ApiContract
{
    private static readonly Lock Gate = new();
    private static List<ContractRoute>? _routes;
    private static JsonDocument? _document;

    public sealed record Contract(IReadOnlyList<ContractRoute> Routes);

    public static Contract Load()
    {
        lock (Gate)
        {
            if (_routes is not null) return new Contract(_routes);

            var path = Path.Combine(AppContext.BaseDirectory, "api-contract.json");
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"The API contract was not copied next to the test assembly ({path}). " +
                    "GpdForge.Core.Tests.csproj links ../tests/contract/api-contract.json as Content; " +
                    "if that item was removed, every contract test silently stops running.", path);

            _document = JsonDocument.Parse(File.ReadAllText(path));
            var list = new List<ContractRoute>();

            foreach (var r in _document.RootElement.GetProperty("routes").EnumerateArray())
            {
                var method = r.GetProperty("method").GetString()!;
                var route = r.GetProperty("path").GetString()!;
                JsonElement? shape = r.TryGetProperty("shape", out var s) && s.ValueKind is not JsonValueKind.Null
                    ? s
                    : null;
                var mockOnly = r.TryGetProperty("mockOnly", out var m) && m.ValueKind is JsonValueKind.True;
                list.Add(new ContractRoute(method, route, shape, mockOnly));
            }

            _routes = list;
            return new Contract(_routes);
        }
    }

    /// <summary>Returns one human-readable problem per violation; empty means the response conforms.</summary>
    public static List<string> Validate(JsonElement actual, JsonElement shape, string where)
    {
        var problems = new List<string>();
        ValidateObject(actual, shape, where, problems);
        return problems;
    }

    private static void ValidateObject(JsonElement actual, JsonElement shape, string where, List<string> problems)
    {
        if (actual.ValueKind is not JsonValueKind.Object)
        {
            problems.Add($"{where}: expected a JSON object, got {Describe(actual)}");
            return;
        }

        foreach (var field in shape.EnumerateObject())
        {
            // Missing is a violation on purpose: a field that quietly stops being emitted breaks a
            // client just as surely as one with the wrong type, and is harder to notice.
            if (!actual.TryGetProperty(field.Name, out var value))
            {
                problems.Add($"{where}.{field.Name}: declared in the contract but absent from the response");
                continue;
            }

            ValidateValue(value, field.Value, $"{where}.{field.Name}", problems);
        }

        // Undeclared extra fields are allowed. The contract is a floor, so adding to a response is
        // never a breaking change and does not need a contract edit to ship.
    }

    private static void ValidateValue(JsonElement value, JsonElement rule, string where, List<string> problems)
    {
        if (rule.ValueKind is JsonValueKind.String)
        {
            CheckType(value, rule.GetString()!, where, problems);
            return;
        }

        if (rule.ValueKind is not JsonValueKind.Object)
        {
            problems.Add($"{where}: the contract rule is malformed ({rule.ValueKind}); expected a type string or an object");
            return;
        }

        var type = rule.GetProperty("type").GetString()!;
        if (!CheckType(value, type, where, problems)) return;

        if (rule.TryGetProperty("oneOf", out var oneOf))
        {
            var allowed = oneOf.EnumerateArray().Select(e => e.GetString()).ToList();
            var actualText = value.ValueKind is JsonValueKind.String ? value.GetString() : value.ToString();
            if (!allowed.Contains(actualText))
                problems.Add(
                    $"{where}: got {Describe(value)}, which is not one of [{string.Join(", ", allowed)}]. " +
                    "If this is a number, a C# enum is serialising as its ordinal — the JsonStringEnumConverter " +
                    "in Program.cs is missing, and the UI will crash parsing it.");
        }

        if (rule.TryGetProperty("items", out var items) && value.ValueKind is JsonValueKind.Array)
        {
            // Item shapes are only checked when there is an item. An empty array is legitimate on a
            // clean machine (no alerts, no sessions, no audit entries yet) and failing on it would
            // make the guard depend on machine history, which is how a real guard gets disabled.
            var i = 0;
            foreach (var element in value.EnumerateArray())
            {
                ValidateObject(element, items, $"{where}[{i}]", problems);
                if (++i >= 3) break;   // three is enough to catch a shape error; all of them is noise
            }
        }

        if (rule.TryGetProperty("fields", out var fields) && value.ValueKind is JsonValueKind.Object)
            ValidateObject(value, fields, where, problems);
    }

    private static bool CheckType(JsonElement value, string union, string where, List<string> problems)
    {
        var accepted = union.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var t in accepted)
        {
            var ok = t switch
            {
                "string" => value.ValueKind is JsonValueKind.String,
                "number" => value.ValueKind is JsonValueKind.Number,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "object" => value.ValueKind is JsonValueKind.Object,
                "array" => value.ValueKind is JsonValueKind.Array,
                "null" => value.ValueKind is JsonValueKind.Null,
                _ => throw new InvalidOperationException(
                    $"Unknown type '{t}' in the contract at {where}. Allowed: string, number, boolean, object, array, null."),
            };
            if (ok) return true;
        }

        problems.Add($"{where}: expected {union}, got {Describe(value)}");
        return false;
    }

    private static string Describe(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => $"string \"{Clip(e.GetString())}\"",
        JsonValueKind.Number => $"number {e}",
        JsonValueKind.True or JsonValueKind.False => $"boolean {e}",
        JsonValueKind.Null => "null",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        _ => e.ValueKind.ToString(),
    };

    private static string? Clip(string? s) => s is { Length: > 40 } ? s[..40] + "…" : s;
}
