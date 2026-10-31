using Meitou.Data.Fcs;

namespace Meitou.Simulation.Saving;

/// <summary>An operator's haul leg as a save holds it: the machine it operates, the storage (or producer) it walks to, and the ITEM it fetches there (null: it stores what it carries).</summary>
public sealed record SavedHaul(string Machine, string Site, string? Fetch);

/// <summary>
/// A hauling operator's trip in its GAMESTATE_AI record <c>ai</c> (Meitou's own keys; the original's are <b>Unknown</b>; docs/formats/save.md "Order queue"): strings <c>meitou haulM</c>
/// (<see cref="CharacterCold.OperateSite"/>), <c>meitou haul</c> (<see cref="CharacterCold.HaulSite"/>) and <c>meitou haulF</c> (<see cref="CharacterCold.HaulFetch"/>, only when fetching).
/// A load keeps them in <see cref="CharacterCold.SavedHaul"/> until the character next starts a production task (<see cref="Production.ProductionSystem"/>).
/// </summary>
public static class HaulSave
{
    public const string MachineKey = "meitou haulM", SiteKey = "meitou haul", FetchKey = "meitou haulF";

    /// <summary>Writes the running leg, or the one a load read that is not taken up yet, over the old keys.</summary>
    public static void Write(FcsRecord ai, CharacterCold cold)
    {
        ai.Strings.Remove(MachineKey);
        ai.Strings.Remove(SiteKey);
        ai.Strings.Remove(FetchKey);
        var leg = cold is { OperateSite: { } machine, HaulSite: { } site } ? new SavedHaul(machine, site, cold.HaulFetch) : cold.SavedHaul;
        if (leg is null) return;
        ai.Strings[MachineKey] = leg.Machine;
        ai.Strings[SiteKey] = leg.Site;
        if (leg.Fetch is { } fetch) ai.Strings[FetchKey] = fetch;
    }

    public static void Read(FcsRecord ai, CharacterCold cold) =>
        cold.SavedHaul = ai.Strings.TryGetValue(MachineKey, out var machine) && machine.Length > 0 && ai.Strings.TryGetValue(SiteKey, out var site) && site.Length > 0
            ? new SavedHaul(machine, site, ai.Strings.TryGetValue(FetchKey, out var fetch) && fetch.Length > 0 ? fetch : null)
            : null;
}
