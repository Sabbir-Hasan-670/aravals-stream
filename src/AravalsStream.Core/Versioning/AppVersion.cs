namespace AravalsStream.Core.Versioning;

public static class AppVersion
{
    public static readonly string Version = typeof(AppVersion).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "unknown";
    public const string Name = "Aravals Stream";
    public const string Publisher = "Aravals";
    public static readonly string ReleaseChannel = Version.Contains('-') ? "Development" : "Stable";
    public static readonly DateTime BuildDate = DateTime.UtcNow;

    public static string FullVersionString => $"{Name} v{Version} ({ReleaseChannel})";

    public static bool TryParse(string versionString, out Version? version)
    {
        var cleaned = versionString.TrimStart('v', 'V').Split('-')[0];
        return System.Version.TryParse(cleaned, out version);
    }
}
