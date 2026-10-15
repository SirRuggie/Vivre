using System.Collections.ObjectModel;
using System.IO;
using Vivre.Core.Scripts;

namespace Vivre.Desktop.ViewModels;

internal static class ScriptProjectionSync
{
    public static void Apply(ObservableCollection<ScriptFile> target, IReadOnlyList<ScriptFile> source)
    {
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!source.Any(s => PathsEqual(s.FullPath, target[i].FullPath))) target.RemoveAt(i);
        }

        for (int desired = 0; desired < source.Count; desired++)
        {
            int existing = IndexOf(target, source[desired].FullPath);
            if (existing < 0) target.Insert(desired, source[desired]);
            else if (existing != desired) target.Move(existing, desired);
        }
    }

    public static void ApplyCategories(ObservableCollection<string> target, IReadOnlyList<string> source)
    {
        for (int i = target.Count - 1; i >= 0; i--)
            if (!source.Contains(target[i], StringComparer.OrdinalIgnoreCase)) target.RemoveAt(i);
        for (int desired = 0; desired < source.Count; desired++)
        {
            int existing = -1;
            for (int i = 0; i < target.Count; i++)
                if (string.Equals(target[i], source[desired], StringComparison.OrdinalIgnoreCase)) { existing = i; break; }
            if (existing < 0) target.Insert(desired, source[desired]);
            else if (existing != desired) target.Move(existing, desired);
        }
    }

    private static int IndexOf(ObservableCollection<ScriptFile> scripts, string path)
    {
        for (int i = 0; i < scripts.Count; i++) if (PathsEqual(scripts[i].FullPath, path)) return i;
        return -1;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
