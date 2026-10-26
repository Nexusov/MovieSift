using System.Globalization;

namespace MovieSift.Application.Updates;

/// <summary>Compares stable three-part release versions independently of assembly revision and build metadata.</summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch) : IComparable<AppVersion>
{
    /// <summary>Accepts a stable SemVer with an optional tag prefix and informational build suffix.</summary>
    public static bool TryParse(string? value, out AppVersion version)
    {
        version = default;

        if (string.IsNullOrWhiteSpace(value) || value.Length > 160)
        {
            return false;
        }

        var text = value.StartsWith('v') ? value[1..] : value;
        var buildIndex = text.IndexOf('+');

        if (buildIndex >= 0)
        {
            var build = text[(buildIndex + 1)..];
            if (build.Length == 0 || build.Split('.').Any(part => part.Length == 0 ||
                part.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')))
            {
                return false;
            }
            text = text[..buildIndex];
        }

        var parts = text.Split('.');
        var numbers = new int[3];

        if (parts.Length != 3)
        {
            return false;
        }

        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part.Length == 0 || part.Length > 1 && part[0] == '0' ||
                !part.All(char.IsAsciiDigit) || !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index]))
            {
                return false;
            }
        }

        version = new(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    /// <summary>Orders numeric components, never lexicographic tag text.</summary>
    public int CompareTo(AppVersion other)
    {
        var major = Major.CompareTo(other.Major);
        var minor = Minor.CompareTo(other.Minor);
        return major != 0 ? major : minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    /// <summary>Returns the culture-independent public release number.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}
