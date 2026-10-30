using System.Text.RegularExpressions;

namespace Plantain.Core;

// Baseline lexical/reparse checks. Handle-based protection against path replacement is stage 3.
internal static partial class PathSafety
{
    internal static string Child(string parent, string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') ||
            name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)) || ReservedDevice().IsMatch(name))
            throw new LocalFileException("Имя файла несовместимо с Windows.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var child = Path.GetFullPath(Path.Combine(root, name));
        if (!child.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new LocalFileException("Недопустимый путь файла.");
        CheckParents(child);
        return child;
    }

    internal static void CheckParents(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new LocalFileException("Путь содержит символическую ссылку или точку перенаправления.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedDevice();
}
