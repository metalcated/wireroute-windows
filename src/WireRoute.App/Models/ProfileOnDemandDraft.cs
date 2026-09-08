namespace WireRoute.App.Models;

// Kept separate from saved profiles so Cancel never changes the editor or storage.
internal sealed record ProfileOnDemandDraft(bool Ethernet, bool WiFi, bool ResumeSavedRules = false)
{
    public string Summary(bool automaticEnabled, bool savedRulesSuspended)
    {
        if (automaticEnabled) return "Automatic profile switching";
        var networks = (Ethernet, WiFi) switch
        {
            (true, true) => "Ethernet, Wi-Fi",
            (true, false) => "Ethernet",
            (false, true) => "Wi-Fi",
            _ => "Off",
        };
        return savedRulesSuspended && !ResumeSavedRules && (Ethernet || WiFi)
            ? networks + " (paused)" : networks;
    }
}
