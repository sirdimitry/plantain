using System.Text.RegularExpressions;

namespace Plantain.Core;

// Lexical checks are separate from handle-based IO in WindowsFiles.
internal static partial class PathSafety
{
    internal static string Child(string parent, string name)
    {
        ValidateName(name);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var child = Path.GetFullPath(Path.Combine(root, name));
        if (!child.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new LocalFileException("Недопустимый путь файла.");
        return child;
    }

    internal static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 255 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') ||
            name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)) || ReservedDevice().IsMatch(name))
            throw new LocalFileException("Имя файла несовместимо с Windows.");
    }

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedDevice();
}
