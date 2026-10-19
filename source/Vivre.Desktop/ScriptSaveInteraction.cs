using System.Windows;
using Vivre.Core.Scripts;

namespace Vivre.Desktop;

internal static class ScriptSaveInteraction
{
    internal static bool TrySave(Window? owner, Action<string?> save, Func<string, bool>? confirm = null)
    {
        try { save(null); return true; }
        catch (ScriptOverwriteRequiredException collision)
        {
            string message = $"Replace the existing script?\n\n{collision.Destination}\n\nIts saved contents will be replaced. Choose No to keep it and use another name.";
            bool approved = confirm?.Invoke(message) ?? MessageBox.Show(owner, message, "Replace saved script",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            if (!approved) return false;
            save(collision.Revision);
            return true;
        }
    }
}
