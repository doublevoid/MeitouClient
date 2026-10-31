using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Construction;

namespace Meitou.Simulation.Production;

// NPC town machines (docs/simulation.md "Production"; the original's production update tests no owner, docs/game/buildings-production.md "Production"): a level building of an NPC town
// whose BUILDING is a producer or a farm gets a Machine keyed by its BuildingKey once its zone is active (the original has building objects in active zones only). Its site is made
// from the level data (no registry site), so the player's orders, jobs, hauling, windows and power grids, which look sites up in the registry or by owner, never see it.
public sealed partial class ProductionSystem
{
    /// <summary>The towns' level buildings (null: the system was made without population data, so no NPC town runs a machine).</summary>
    readonly PopulationData? towns;
    /// <summary>The NPC town buildings that get a machine (producers and farms, not ruins or rocks), by key, and their keys by zone; built on first use from the static level data.</summary>
    Dictionary<string, (TownBuilding Building, int Site)>? townByKey;
    Dictionary<ZoneCoordinate, List<string>>? townByZone;
    /// <summary>The active zones whose town buildings have their machines (derived from <see cref="World.ActiveZones"/> history; a load or new world starts over with a new system).</summary>
    readonly HashSet<ZoneCoordinate> townZonesDone = [];

    /// <summary>A BUILDING whose level placements in an NPC town run as machines: a producer (mine, well, refinery, rain collector) or a farm, not a rock (<c>is foliage</c>).</summary>
    bool TownMachineRecord(string record) =>
        Data(record) is { } d && (d.IsProducer || d.Farm is not null) && db.Find(record) is { } r && !r.GetBool("is foliage");

    void IndexTowns()
    {
        if (townByKey is not null || towns is null) return;
        townByKey = new(StringComparer.Ordinal);
        townByZone = [];
        foreach (var site in towns.Sites)
            foreach (var b in site.Buildings)
            {
                if (b.Destroyed || !TownMachineRecord(b.BuildingId) || !townByKey.TryAdd(b.Key, (b, site.Index))) continue;
                if (!townByZone.TryGetValue(b.Zone, out var list)) townByZone[b.Zone] = list = [];
                list.Add(b.Key);
            }
    }

    /// <summary>
    /// Makes the machines of the NPC town buildings in the zones that became active (once per zone; they then stay, and run as <see cref="Live"/> says). Serial; with no active zones
    /// known (a world without population) nothing is made.
    /// </summary>
    void SyncTowns(World world)
    {
        if (towns is null || world.ActiveZones is not HashSet<ZoneCoordinate> active || active.Count == 0) return;
        foreach (var zone in active)
        {
            if (townZonesDone.Contains(zone)) continue;
            townZonesDone.Add(zone);
            IndexTowns();
            if (!townByZone!.TryGetValue(zone, out var keys)) continue;
            foreach (var key in keys) EnsureTown(world, key);
        }
    }

    /// <summary>
    /// The machine of the NPC town building <paramref name="key"/> (<see cref="BuildingKey"/>), made empty (a farm freshly planted) unless it has one; null when the key names no such
    /// building or the player's registry holds a site of it (an owned town house: the player's machine).
    /// </summary>
    internal Machine? EnsureTown(World world, string key)
    {
        if (world.Machines.Find(key) is { } existing) return existing;
        if (world.Buildings.Find(key) is not null) return null;
        IndexTowns();
        if (towns is null || townByKey is null || !townByKey.TryGetValue(key, out var town)) return null;
        var b = town.Building;
        // The owner: a bought property's, else the placement's owner faction, else the town's (the AI's seat owners read it the same way).
        int owner = world.Fixtures.PropertyOwner(key)
            ?? (b.OwnerFactionId is { } id && towns.FactionIndex(id) is var fi and >= 0 ? fi : town.Site < towns.SiteFactions.Count ? towns.SiteFactions[town.Site] : -1);
        var site = new BuildSite
        {
            Id = key, Record = b.BuildingId, Name = b.Name, Position = b.Position, Rotation = b.Resolved?.Placement.Rotation ?? System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, b.Yaw),
            Owner = owner, Materials = [], Complete = true, LevelTownName = towns.Sites[town.Site].Town.Name,
        };
        if (Create(world, site, townSite: true) is not { } m) return null;
        world.Machines.Add(m);
        return m;
    }

    /// <summary>The site of a machine: an NPC town machine's own, else the registry's or the rock's (<see cref="Site"/>).</summary>
    BuildSite? MachineSite(World world, Machine m) => m.TownSite ?? Site(world, m.Site);

    /// <summary>
    /// A worker of an NPC town at its building (goal 189 Worker, acted out by 87 Operating machine; <see cref="AiMind.WorkSite"/>): one operator's work on the building's machine, as the
    /// player's operator's (<c>Task_OperateMachine</c> asks no owner). The pretend goal 190 (221) makes nothing. A machine whose operator places are all taken this tick, or an
    /// automatic one, takes no work (<c>tryOperate</c> refuses; an automatic machine works alone).
    /// </summary>
    void TownWork(World world, int slot, CharacterCold cold, AiMind mind, float dt)
    {
        if (mind.WorkSite is not { } key || world.Machines.Find(key) is not { NpcTown: true } m || !Able(cold) || !Live(world, m)) return;
        if (m.Data.IsAutomatic || !(m.Data.IsProducer || m.Farm is not null) || m.Operators >= m.Data.MaxOperators) return;
        m.Operators++;
        var stat = (StatsEnumerated)m.Data.Stat;
        float skill = m.Data.Stat > 0 ? Effective(cold, stat) : -1;
        float s = skill < 0 ? 1 : Lerp(0.5f, 1.4f, skill / 100);
        if (m.Farm is not null) HarvestStep(world, m, skill, s, dt);
        else ProduceStep(world, m, s, dt);
        TakeTownOutput(m);
        if (m.Data.Stat > 0) cold.UseSkill(stat, world.Tick);
        if (m.Data.Stat > 0 && cold.Stats is { } stats && cold.Race is { } race) xp.Continuous(stats, race, stat, dt, OperateXp(m.Data.Stat));
    }

    /// <summary>
    /// An NPC town machine keeps none of what it makes (engine choice, docs/simulation.md "Production": NPC mines in the original's saves hold progress but never output items, Observed;
    /// what empties them is Unknown).
    /// </summary>
    static void TakeTownOutput(Machine m)
    {
        if (m.NpcTown && m.Output is { Count: > 0 } o) o.Count = 0;
    }
}
