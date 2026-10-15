using System.Collections.Generic;
using System.Windows.Media;
using CodexBar.Core;
using CodexBar.Windows.ViewModels;

namespace CodexBar.UiTests;

/// <summary>
/// Regression cover for the card action row: it used to bind to the inverse of
/// IsEmpty, which hid every action button exactly when a card had no windows
/// (the not-configured and authentication-error states).
/// </summary>
internal static class CardActionTests
{
    public static void Register()
    {
        TestRunner.Add("a card with actions reports HasActions", CardWithActionsReportsTrue);
        TestRunner.Add("a refreshed card re-evaluates HasActions", HasActionsTracksActionChanges);
    }

    private static ProviderCardViewModel NewCard()
        => new(ProviderId.Codex, "Codex", Brushes.Gray, DisplayMode.Remaining);

    private static CardActionViewModel Action(string text) => new(text, new RelayCommand(() => { }));

    private static void CardWithActionsReportsTrue()
    {
        var card = NewCard();
        Check.False(card.HasActions, "a fresh card should have no actions");
        Check.False(card.HasWindows, "a fresh card should have no windows");

        // The broken binding used IsEmpty here, so this exact state (actions but
        // no windows) rendered nothing at all.
        card.SetActions(new List<CardActionViewModel> { Action("打开设置") });

        Check.True(card.HasActions, "a card with one action should report HasActions");
        Check.True(card.IsEmpty, "this state intentionally has no windows");
    }

    private static void HasActionsTracksActionChanges()
    {
        var card = NewCard();
        card.SetActions(new List<CardActionViewModel> { Action("立即重试") });
        Check.True(card.HasActions, "HasActions should follow the first action set");

        card.SetActions(new List<CardActionViewModel>());
        Check.False(card.HasActions, "HasActions should follow an emptied action set");

        card.SetActions(new List<CardActionViewModel> { Action("官方用量页面"), Action("打开设置") });
        Check.Equal(2, card.Actions.Count, "both actions should be stored");
        Check.True(card.HasActions, "HasActions should be true again");
    }
}
