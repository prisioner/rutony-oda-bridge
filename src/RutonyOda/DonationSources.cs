namespace RutonyOda;

public static class DonationSources
{
    public static IReadOnlyList<string> Supported { get; } = Array.AsReadOnly(new[] { "ODA", "DonationAlerts", "DonatePay", "DonatePay.eu", "Donate.Stream", "DonateX", "Tribute" });
    public static string[] Validate(IEnumerable<string> systems)
    {
        var selected = systems.ToArray();
        if (selected.Length > Supported.Count || selected.Any(s => !Supported.Contains(s, StringComparer.Ordinal))) throw new ArgumentException("Выберите источники из списка.");
        return Supported.Where(s => selected.Contains(s, StringComparer.Ordinal)).ToArray();
    }
}
