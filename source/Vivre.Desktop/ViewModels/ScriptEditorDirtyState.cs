namespace Vivre.Desktop.ViewModels;

public enum ScriptEditChoice { Save, Discard, Cancel }

internal sealed class ScriptEditorDirtyState
{
    private string _text = string.Empty;
    private string _name = string.Empty;
    private string _category = string.Empty;

    public bool IsDirty(string text, string name, string category) =>
        !string.Equals(text, _text, StringComparison.Ordinal)
        || !string.Equals(name, _name, StringComparison.Ordinal)
        || !string.Equals(category, _category, StringComparison.Ordinal);

    public void MarkClean(string text, string name, string category)
    {
        _text = text;
        _name = name;
        _category = category;
    }
}
