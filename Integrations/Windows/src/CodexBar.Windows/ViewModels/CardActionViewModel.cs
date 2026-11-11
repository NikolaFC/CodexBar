using System.Windows.Input;

namespace CodexBar.Windows.ViewModels;

public sealed class CardActionViewModel
{
    public CardActionViewModel(string text, ICommand command, bool prominent = false)
    {
        Text = text;
        Command = command;
        IsProminent = prominent;
    }

    public string Text { get; }

    public ICommand Command { get; }

    public bool IsProminent { get; }
}
