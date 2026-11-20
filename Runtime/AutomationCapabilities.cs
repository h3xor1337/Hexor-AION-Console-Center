namespace Hexor.Runtime;

// Storage verification is distinct from runtime acknowledgement.
public static class AutomationCapabilities
{
    public const string Connection = "Unavailable: no supported external automation interface configured.";
    public const string State = "Unknown — a running game does not prove automation is running.";
    public const string Fallback = "Use the in-game menu (F10) to start or stop supported activities, or the Start/Stop, Start/Stop gathering, and Use-default-config buttons on the Daily Activities page, which send the game's own start, stop, and apply-default-config commands. Hexor never starts automation on its own. Desktop workflow scheduling still requires provider integration.";
    public const string Recovery = "Choose an existing configuration data root or its config folder, then Reload. If files are missing or invalid, restore a known-good backup or use the provider's supported setup/recovery procedure. Hexor leaves originals intact and does not fabricate compatible files. Reopen Hexor after recovery; unsaved drafts are not applied to the engine.";
    public static string SettingsEffect(int page) => page switch
    {
        0 => "Hexor UI language and manual profiles apply locally. Machine paths and original language preferences persist to JSON. Game language is used by provider launch arguments; no live language switch or provider UI notification is sent.",
        1 => "Workflow saves persist to settings.json. Reference orchestration reloads the template when starting a flow; editing does not replace an active workflow. Hexor-only drafts do not control automation.",
        2 => "Verified AutoTask booleans persist to JSON and registry mirrors. Read-back confirms storage only; engine refresh timing is unverified. Named race/job controls and AutoDailyQuests remain unavailable.",
        3 => "Enhancement preferences persist to individual registry values. Engine initialization/read timing and live application are unverified; no refresh command is sent.",
        4 => "Schedule saves persist to JSON. The provider caches settings and uses an in-process notification that Hexor cannot invoke. A provider flow start reloads the schedule in the reference; an active scheduler is not notified by Hexor.",
        5 => "Shugo preferences persist to independent registry values. Engine refresh timing is unverified; provider permissions still apply. No live reload or entitlement change is sent.",
        _ => throw new ArgumentOutOfRangeException(nameof(page))
    };
}
