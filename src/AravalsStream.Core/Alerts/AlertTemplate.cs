using System.Text.RegularExpressions;
using AravalsStream.Core.Models;

namespace AravalsStream.Core.Alerts;

public static class AlertTemplate
{
    private static readonly Regex Variable = new(@"\{([a-zA-Z]+)\}", RegexOptions.Compiled);
    public static string Render(string? template, StreamEvent item, int groupedCount = 1)
    {
        if (string.IsNullOrEmpty(template)) return "";
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["user"] = item.ActorName, ["amount"] = item.Amount?.ToString() ??
                item.Metadata.GetValueOrDefault("displayAmount") ?? "",
            ["currency"] = item.Currency ?? "", ["count"] = (item.Quantity ?? groupedCount).ToString(),
            ["tier"] = item.Tier ?? "", ["message"] = item.Message ?? "", ["platform"] = item.Platform
        };
        return Variable.Replace(template, match => values.GetValueOrDefault(match.Groups[1].Value) ?? "");
    }
}
