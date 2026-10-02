namespace BadDeduction.Godot;

/// <summary>Every switchable panel implements this; Main calls Refresh on show and on updates.</summary>
public interface IPanel
{
    void Refresh();
}
