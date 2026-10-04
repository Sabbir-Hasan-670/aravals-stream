namespace AravalsStream.Core.Versioning;

public readonly record struct ReleaseVersion(int Major, int Minor, int Patch, string? Prerelease) : IComparable<ReleaseVersion>
{
    public bool IsPrerelease => !string.IsNullOrEmpty(Prerelease);

    public static bool TryParse(string? value, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim().TrimStart('v', 'V').Split('+', 2)[0];
        var parts = text.Split('-', 2);
        var numbers = parts[0].Split('.');
        if (numbers.Length != 3 || !int.TryParse(numbers[0], out var major) ||
            !int.TryParse(numbers[1], out var minor) || !int.TryParse(numbers[2], out var patch) ||
            major < 0 || minor < 0 || patch < 0) return false;
        var suffix = parts.Length == 2 ? parts[1] : null;
        if (parts.Length == 2 && (string.IsNullOrWhiteSpace(suffix) ||
            suffix.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and '-'))) return false;
        version = new ReleaseVersion(major, minor, patch, suffix);
        return true;
    }

    public int CompareTo(ReleaseVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        if (!IsPrerelease || !other.IsPrerelease)
            return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;
        return ComparePrerelease(Prerelease!, other.Prerelease!);
    }

    private static int ComparePrerelease(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aNumeric = int.TryParse(a[i], out var an);
            var bNumeric = int.TryParse(b[i], out var bn);
            var result = aNumeric && bNumeric ? an.CompareTo(bn) :
                aNumeric ? -1 : bNumeric ? 1 : string.CompareOrdinal(a[i], b[i]);
            if (result != 0) return result;
        }
        return a.Length.CompareTo(b.Length);
    }
}
