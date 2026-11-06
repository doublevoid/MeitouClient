using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Dialogue;
using Meitou.Ui;

namespace Meitou.Game;

/// <summary>
/// Conversations (docs/game/dialogue.md "UI", docs/simulation.md "Dialogue"), the same in both UI modes: while the world publishes a conversation the original's conversation panel
/// (<c>Kenshi_ConversationPanel</c>) is open, modal, so the game is paused as the original pauses it. The NPC panel shows the speaker's name and line, the player panel the talker's name,
/// the money and the choices (colours <c>DialogueTextOption</c> #b3b3b3, <c>DialogueTextOptionHover</c> #00ff00 from <c>kenshi_colours.xml</c>); a click on a choice says it, Esc or
/// closing leaves the conversation. The portraits are the speaker's and the player character's, drawn into the last two cells of the portrait atlas.
/// </summary>
sealed partial class PlayerInterface
{
    static readonly Vector4 ChoiceColour = new(0xb3 / 255f, 0xb3 / 255f, 0xb3 / 255f, 1), ChoiceHover = new(0, 1, 0, 1);
    bool conversationShown, conversationEndSent;
    long dialogueRequestHandled;
    string? dialogueText;

    /// <summary>Opens the panel when a conversation opens, closes it when it ends, ends the conversation when the player closed the panel; runs a finished conversation's request.</summary>
    void SyncDialogueScreen()
    {
        if (faithfulScreens is null) return;
        var snapshot = session.CurrentSnapshot;
        var talk = snapshot.Conversation;
        if (talk is null)
        {
            if (conversationShown && faithfulScreens.IsOpen("dialogue")) faithfulScreens.Close("dialogue");
            if (conversationShown) UpdateMenuPause();
            conversationShown = conversationEndSent = false;
        }
        else if (!conversationShown)
        {
            conversationShown = true;
            conversationEndSent = false;
            CloseContextMenu();
            faithfulScreens.Open("dialogue");
            // The exe posts it as a player conversation starts (FUN_140684320); only the never-loaded SFX_Bank has the event, so it is silent there and here.
            Sound("Initiate_Conversation");
            UpdateMenuPause();
        }
        else if (!faithfulScreens.IsOpen("dialogue") && !conversationEndSent)
        {
            Say(new EndConversation { Tick = session.World.Tick });
            conversationEndSent = true;
            UpdateMenuPause();
        }
        if (snapshot.DialogueRequest is { } request && request.Id > dialogueRequestHandled)
        {
            dialogueRequestHandled = request.Id;
            if (request.Kind == DialogueRequestKind.CharacterEditor) OpenEditor(Meitou.Simulation.Editing.EditKind.Surgery, [request.Player], request.Npc, paid: true);
            else if (request.Kind == DialogueRequestKind.RecruitEditor) OpenEditor(Meitou.Simulation.Editing.EditKind.Recruit, [request.Player]);
            else if (request.Kind == DialogueRequestKind.AnimalEditor) OpenEditor(Meitou.Simulation.Editing.EditKind.Recruit, request.Characters);
        }
    }

    void BindDialogueScreen(UiBindings b)
    {
        if (session.CurrentSnapshot.Conversation is not { } talk) return;
        b.Text["NpcName"] = talk.SpeakerName;
        b.Text["PlayerName"] = talk.PlayerName;
        b.Text["NpcDialog"] = talk.Text;
        b.Properties["NpcDialog"] = new Dictionary<string, string> { ["Static"] = "true", ["WordWrap"] = "true", ["TextAlign"] = "Left Top" };
        FitNpcFrame(b, talk.Text);
        // A line taller than the screen above the frame allows scrolls under the EditBox's own bar; each new line starts at the top.
        if (talk.Text != dialogueText) { dialogueText = talk.Text; faithfulScreens.SetScroll("dialogue", "NpcDialog", 0); }
        // The choices are drawn as buttons, so the player box's template scroll bar would only be an empty track.
        foreach (var bar in faithfulScreens.Layout("dialogue").Find("PlayerDialog")?.Children.Where(w => w.Type == "ScrollBar") ?? []) b.HiddenWidgets.Add(bar);
        b.NativeScroll.Add("PlayerDialog");   // the choices are drawn against the box, not scrolled by its canvas
        b.Text["MoneyLabelText"] = Loc.Tr("Money:");
        b.Text["MoneyAmountText"] = "c." + talk.PlayerMoney.ToString("N0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The NPC frame grown to its wrapped line, never below the layout's height (the original grows it to its text). It grows upward, so the
    /// connecting bars and the player panel stay (engine choice), at most to the top of the screen; the name and portrait keep their size.
    /// </summary>
    void FitNpcFrame(UiBindings b, string text)
    {
        var layout = faithfulScreens.Layout("dialogue");
        if (layout.Find("NpcPanel") is not { RealPosition: true } panel || layout.Find("NpcDialog") is not { } dialog) return;
        if (panel.Children.FirstOrDefault(c => c.Children.Contains(dialog)) is not { } frame) return;

        float panelHeight = panel.Position.Height * Height;
        float frameHeight = frame.Position.Height * panelHeight;
        float dialogHeight = dialog.Position.Height * frameHeight;
        float extra = Math.Clamp(NpcTextHeight(panel, frame, dialog, text) - (dialogHeight - NpcClientInset.Y), 0, panel.Position.Y * Height);
        if (extra <= 0) return;

        float grownPanel = panelHeight + extra;
        float grownFrame = frameHeight + extra;
        b.RelativePositions[panel] = panel.Position with { Y = panel.Position.Y - extra / Height, Height = grownPanel / Height };
        foreach (var child in panel.Children)
        {
            float height = ReferenceEquals(child, frame) ? grownFrame : child.Position.Height * panelHeight;
            b.RelativePositions[child] = child.Position with { Y = child.Position.Y * panelHeight / grownPanel, Height = height / grownPanel };
        }
        float dialogTop = dialog.Position.Y * frameHeight / grownFrame;
        b.RelativePositions[dialog] = dialog.Position with { Y = dialogTop, Height = (dialogHeight + extra) / grownFrame };
    }

    /// <summary>
    /// How much narrower (left inset and scroll bar) and shorter (top inset) <c>Kenshi_EditBoxStrechEmpty</c>'s text client is than the EditBox.
    /// </summary>
    static readonly Vector2 NpcClientInset = new(20, 5);
    (string Text, int Width, int Height, float TextHeight) npcTextHeight;

    /// <summary>
    /// The NPC line's height wrapped in the EditBox's text client at the layout's width, in the client's template font (cached per line and viewport).
    /// </summary>
    float NpcTextHeight(MyGuiWidget panel, MyGuiWidget frame, MyGuiWidget dialog, string text)
    {
        if (uiPainter is null) return 0;
        if (npcTextHeight.Text == text && npcTextHeight.Width == Width && npcTextHeight.Height == Height) return npcTextHeight.TextHeight;

        float width = dialog.Position.Width * frame.Position.Width * panel.Position.Width * Width - NpcClientInset.X;
        var client = new UiControl("NpcDialog", "TextBox", new UiRect(0, 0, width, 0), text, "", "Kenshi_TextboxStandardText", true,
            new Dictionary<string, string> { ["WordWrap"] = "true" }, Vector4.Zero);
        npcTextHeight = (text, Width, Height, uiPainter.TextHeight(client, Height, StandardPixels));
        return npcTextHeight.TextHeight;
    }

    /// <summary>The choices, one line each down the player panel's dialog area.</summary>
    void AddDialogueChoices(List<UiControl> controls)
    {
        AddDialoguePortraits(controls);
        if (session.CurrentSnapshot.Conversation is not { } talk || controls.FirstOrDefault(c => c.Name == "PlayerDialog") is not { } area) return;
        float line = MathF.Max(16, area.Bounds.Height / 7);
        for (int i = 0; i < talk.Choices.Count; i++)
        {
            var bounds = new UiRect(area.Bounds.X, area.Bounds.Y + i * line, area.Bounds.Width, line);
            controls.Add(new($"dialogue-choice-{i}", "Button", bounds, $"{i + 1}. {talk.Choices[i]}", $"dialogue-choice:{i}", "", true,
                new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["TextAlign"] = "Left VCenter" }, Vector4.Zero,
                TextColour: ChoiceColour, HoverColour: ChoiceHover, Pickable: true, Clip: area.Clip ?? area.Bounds));
        }
    }

    void DialogueAction(MyGuiEvent e)
    {
        if (e.Action.StartsWith("dialogue-choice:", StringComparison.Ordinal) && int.TryParse(e.Action[16..], CultureInfo.InvariantCulture, out int index))
            Say(new DialogueChoice(index) { Tick = session.World.Tick });
    }

    /// <summary>The number keys pick a choice while the panel is open (engine convenience).</summary>
    bool DialogueKey(Meitou.Engine.Input.Key key)
    {
        if (!faithfulScreens.IsOpen("dialogue") || session.CurrentSnapshot.Conversation is not { } talk) return false;
        int index = key - Meitou.Engine.Input.Key.D1;
        if (index < 0 || index > 8) return false;
        if (index < talk.Choices.Count) Say(new DialogueChoice(index) { Tick = session.World.Tick });
        return true;
    }
}
