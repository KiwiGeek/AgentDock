using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentDock.Views;

/// <summary>
/// Turning Waifu backgrounds on asks once more, with a different joke each time (owner's request,
/// 2026-10-20: a second confirmation "like 'just do it, I'm not proud'", with a variety of comical
/// ones). Turning them off never asks.
/// </summary>
public sealed partial class SettingsWindow
{
    /// <summary>A title, a line, the button that turns them on, and the one that backs out.</summary>
    private static readonly (string Title, string Message, string Yes, string No)[] WaifuPrompts =
    [
        ("Turn on waifu mode?", "Your agents will look at you while you work. Some of them will look at you a lot.",
            "Just do it, I'm not proud", "I have some dignity left"),
        ("Are you sure?", "Your coworkers can probably see your screen. Maybe they're into it. Maybe they're not.",
            "They knew what I was", "Keep it professional"),
        ("One more thing before the hearts start", "There will be hearts. Floating ones. You can't negotiate with the hearts.",
            "Bring on the hearts", "I fear the hearts"),
        ("This can't be undone", "That's a lie, it's a switch. But think about what you're doing.",
            "I've thought about it", "I'll think some more"),
        ("Screen share check", "Is anyone watching your screen right now? Be honest.",
            "Nobody's watching", "Someone is watching"),
        ("Waifu mode needs a second opinion", "We asked your mom. She said fine, but she sounded disappointed.",
            "She'll get over it", "Call her back"),
        ("Agent Dock is legally required to ask", "Pictures behind the flyout, these settings, and your Terminal tabs. All of them. Every day.",
            "Yes, all of them", "Not today"),
        ("Bold choice", "Your terminal tabs will have company. Your commits will feel watched.",
            "Let them watch", "Plain terminals, please"),
        ("Second confirmation", "Waifu mode is for professionals with nothing left to lose.",
            "That's me", "I still have things to lose"),
        ("Final answer?", "Your future self may have questions about this.",
            "Future me can cope", "Protect future me"),
        ("Before you commit", "Unlike your last commit, this one comes with a picture.",
            "Ship it", "Revert"),
        ("Really?", "Claude Code is already blushing. Codex is pretending not to notice.",
            "Don't make it weird", "Okay, it's weird"),
        ("A word from your therapist", "She says this is fine, as long as you're honest with yourself about why.",
            "I'm being honest", "Book another session"),
        ("Waifu mode", "Studies show developers with waifu mode on write 0% more code. But they feel better about it.",
            "Science agrees", "I need peer review"),
        ("Hold on", "Have you considered touching grass first? Grass is free.",
            "Grass can wait", "Going outside"),
        ("Last chance", "After this there's no going back. Well, there is. It's the same switch.",
            "Flip it", "Leave it alone"),
        ("Permission requested", "Agent Dock would like to make your desktop significantly more anime.",
            "Permission granted", "Permission denied"),
        ("Think of the children", "There aren't any here, but think of them anyway.",
            "I thought of them", "I'll go think"),
        ("Pull request #69", "Adds waifus. 22 files changed. 0 tests. Reviewer: you.",
            "Approve and merge", "Request changes"),
        ("This is fine", "Everything is fine. The hearts are fine. You're fine.",
            "Everything is fine", "Nothing is fine"),
        ("Hey, it's me, your browser history", "I'm not saying anything. I'm just here.",
            "Mind your business", "Fair point"),
        ("Compliance notice", "Waifu mode has not been approved by HR, Legal or your mom.",
            "I am my own HR", "Loop in HR"),
        ("Are you at work right now?", "Don't answer that. Just know that we know.",
            "I work from home", "I am at work"),
        ("Achievement unlocked", "Shameless: turn on waifu mode without hesitating. You hesitated a bit.",
            "Claim the achievement", "Hesitate more"),
        ("Sudo make me a waifu", "You are not in the sudoers file. This incident will be reported.",
            "Report it, I don't care", "Please don't report it"),
        ("Git blame will remember", "Every line you write from now on will be written in front of an audience.",
            "I perform best for a crowd", "Stage fright"),
        ("Rubber duck debugging, but", "Instead of a duck, it's her. She's listening. She believes in you.",
            "She believes in me", "Bring back the duck"),
        ("Your manager is typing…", "Never mind, it was about the stand-up. Continue.",
            "Continuing", "That was too close"),
        ("Low battery warning", "Your dignity is at 4%. Plug in to continue.",
            "Run it down to 0%", "Find a charger"),
        ("Warning: side effects may include", "Smiling at your terminal, naming your branches after her, and floating hearts.",
            "I accept the side effects", "Read the label again"),
        ("Do you want to save your dignity before closing?", "Your dignity has unsaved changes.",
            "Don't save", "Save"),
        ("Windows is checking for a solution to the problem", "There is no problem. You just want waifus. That's allowed.",
            "Thanks, Windows", "Keep checking"),
        ("Error 418: I'm a teapot", "Just kidding. That's the waifu prompt's way of saying hello.",
            "Hello", "Who's a teapot?"),
        ("Merge conflict", "Your professional self and your other self both changed this setting.",
            "Accept incoming", "Keep current"),
        ("Are you sure you're sure?", "Like, sure sure? Not just regular sure?",
            "Sure sure", "Only regular sure"),
        ("Reminder", "Screenshots of your desktop may end up in bug reports. Bug reports are forever.",
            "I'll crop them", "Bug reports are forever"),
        ("Real programmers", "Real programmers use butterflies. You use waifus. That's evolution.",
            "Embrace evolution", "Go back to butterflies"),
        ("Loading confidence…", "Confidence loaded. Shame not found. Proceed?",
            "Proceed", "Look for the shame again"),
        ("Stack Overflow says", "Closed as duplicate of \"Why does my terminal have a girl in it?\"",
            "I'll ask anyway", "Reopen as off-topic"),
        ("You are about to enter waifu mode", "Please keep your arms and legs inside the terminal at all times.",
            "Keep arms inside", "Exit the ride"),
        ("Updating your personality", "Please don't turn off your PC. This may take a few hearts.",
            "Update now", "Remind me later"),
        ("Linter warning", "Excessive charm on line 1. Consider refactoring into a normal life.",
            "Suppress warning", "Refactor my life"),
        ("Cookie consent", "Waifu mode uses hearts, which are like cookies, but they float.",
            "Accept all hearts", "Reject non-essential hearts"),
        ("Two-factor authentication", "Please confirm on your second device that you really want this.",
            "Confirmed on my phone", "My phone said no"),
        ("Breaking change", "Waifu mode changes the public API of your reputation.",
            "Bump the major version", "Stay on v1"),
        ("Somewhere, a senior developer sighs", "They don't know why. They just felt it.",
            "Let them sigh", "Spare the senior"),
        ("Accessibility check", "Hearts pass WCAG contrast guidelines. Your choices are still under review.",
            "Ship it accessible", "Under review"),
        ("Timeout", "We've been waiting 0.4 seconds for you to say no. You didn't.",
            "And I won't", "Okay, no"),
        ("Fun fact", "The flyout can't tell what you're thinking. The waifus might.",
            "Let them read me", "Close my mind"),
        ("Deploying to production", "Waifu mode will now go live on your actual computer, which you use for actual work.",
            "Deploy on a Friday", "Wait until Monday"),
        ("The council has reviewed your request", "The vote was 3 to 2. The dissenting opinions are long and hurt.",
            "The majority rules", "Read the dissent"),
        ("Pick your path", "One road leads to plain productivity. The other has hearts on it.",
            "The road with hearts", "The sensible road"),
        ("Copilot suggests", "// TODO: reconsider life choices",
            "Ignore suggestion", "Accept suggestion"),
        ("Gentle reminder", "Your webcam light is off. Probably. Check anyway.",
            "It's off, go", "Checking the light"),
        ("You've changed", "You used to be about clean desktops and default themes.",
            "People grow", "I miss the old me"),
        ("Undefined behavior ahead", "The spec says nothing about what happens next. Neither will we.",
            "Live dangerously", "Stay within the spec"),
        ("Tab completion", "You typed \"wai\" and Agent Dock finished the thought for you.",
            "Accept completion", "Backspace, backspace"),
        ("Experimental feature", "Waifu mode is stable. You, we're less sure about.",
            "I'm stable enough", "Run more tests on me"),
        ("Insufficient permissions", "Just kidding, you're the admin of your own bad decisions.",
            "Run as administrator", "Run as a normal user"),
        ("Your future grandchildren", "Will they find out? They will. Grandchildren always do.",
            "They'll understand", "Protect the bloodline"),
        ("Friendly ping from your conscience", "Not trying to stop you. Just letting you know I'm still here.",
            "Mute conscience", "Thanks, conscience"),
        ("Patch notes", "Added: waifus. Removed: plausible deniability. Fixed: nothing, really.",
            "Install the patch", "Roll back")
    ];

    private bool _waifuPromptOpen;

    /// <summary>The second confirmation. True to turn them on.</summary>
    private async Task<bool> ConfirmWaifusAsync()
    {
        var (title, message, yes, no) = WaifuPrompts[Random.Shared.Next(WaifuPrompts.Length)];
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = yes,
            CloseButtonText = no,
            DefaultButton = ContentDialogButton.Close
        };
        _waifuPromptOpen = true;
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return true; // couldn't ask (another dialog open, say): do what the switch says
        }
        finally
        {
            _waifuPromptOpen = false;
        }
    }
}
