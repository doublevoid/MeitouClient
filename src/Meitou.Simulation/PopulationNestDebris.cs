using System.Globalization;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Construction;
using Meitou.Simulation.Construction;

namespace Meitou.Simulation;

/// <summary>A piece of a nest's debris as laid out: a BUILDING (made a complete <see cref="BuildSite"/>) or a NEST_ITEM (a ground item), where it stands and how it is turned about Y.</summary>
public readonly record struct NestDebrisPiece(string Record, bool Building, Vector3 Position, float Yaw);

/// <summary>
/// Nest debris (docs/game/factions-squads-towns.md "Nests"): the camp a nest's TOWN record describes with <c>debris building</c> (BUILDINGs: a fogman camp's prisoner poles and torch
/// posts, a bandit camp's beds and campfire), <c>debris</c> (NEST_ITEMs: bones, dung, eggs), <c>num centrepoints</c> and <c>size radius</c>. Laid out from the world seed and the nest's
/// key when the nest loads, so the same nest gets the same camp every time, and taken away when it unloads; nothing of it is saved. The buildings are complete sites of the nest's
/// faction, so their cages (a prisoner pole) take the faction's captives and their lamps burn.
/// </summary>
public sealed partial class PopulationSystem
{
    /// <summary>Tries at a walkable spot for one piece.</summary>
    const int DebrisTries = 5;

    /// <summary>
    /// The pieces of a nest's debris (engine reading of fcs.def's field texts; the original's scatter <c>FUN_140938440</c> is not decoded). Buildings: an entry makes round(v0 x U[0.5, 1.5))
    /// of its building (v0 = 0: one with a chance of 1/2), each at a random one of the nest's centrepoints (the centre itself, then <c>num centrepoints</c> - 1 seeded points within the
    /// nest radius), a distance U[v1, v2] away in a random direction. Items: an entry makes U[v0, v1] of its item, scattered uniformly within the nest radius. A piece lands on walkable
    /// ground (<see cref="DebrisTries"/> tries, else it is left out) at the ground's height, turned at random. Not modelled: the items' v2 (mesh scale %) and their <c>cluster</c> lists, the
    /// TOWN's <c>loot spawn</c>. Pure given its arguments; any thread.
    /// </summary>
    static List<NestDebrisPiece> LayoutDebris(WildSite site, TownData nest, Vector2 centre, float radius, ulong seed, IWalkability walk, GameDatabase db)
    {
        var pieces = new List<NestDebrisPiece>();
        ulong key = Rng.Mix(SeedSalts.NestDebris ^ site.Key);
        ulong n = 0;
        ulong Roll() => Rng.Hash(seed, key, RngPurpose.Spawn, SeedSalts.NestDebrisRoll + n++);
        int points = Math.Max(1, nest.NumCentrepoints);
        var centres = new Vector2[points];
        centres[0] = centre;
        for (int k = 1; k < points; k++) centres[k] = Rng.PointInDisc(Roll(), centre, radius);
        foreach (var e in nest.DebrisBuilding)
        {
            if (db.Find(e.Id) is not { Type: FcsRecordType.BUILDING }) continue;
            int count = e.V0 == 0 ? (Rng.Float(Roll()) < 0.5f ? 1 : 0) : (int)MathF.Round(e.V0 * (0.5f + Rng.Float(Roll())));
            float min = Math.Min(e.V1, e.V2), max = Math.Max(e.V1, e.V2);
            for (int j = 0; j < count; j++)
                for (int t = 0; t < DebrisTries; t++)
                {
                    var at = centres[Math.Min((int)(Rng.Float(Roll()) * points), points - 1)];
                    float angle = Rng.Float(Roll()) * MathF.Tau, d = min + (max - min) * Rng.Float(Roll()), yaw = Rng.Float(Roll()) * MathF.Tau;
                    var p = at + new Vector2(MathF.Sin(angle), MathF.Cos(angle)) * d;
                    if (!walk.IsWalkable(p.X, p.Y)) continue;
                    pieces.Add(new NestDebrisPiece(e.Id, true, new Vector3(p.X, walk.GroundHeight(p.X, p.Y), p.Y), yaw));
                    break;
                }
        }
        foreach (var e in nest.Debris)
        {
            if (db.Find(e.Id) is not { Type: FcsRecordType.NEST_ITEM }) continue;
            int min = Math.Min(e.V0, e.V1), max = Math.Max(e.V0, e.V1);
            int count = Math.Min(min + (int)(Rng.Float(Roll()) * (max - min + 1)), max);
            for (int j = 0; j < count; j++)
                for (int t = 0; t < DebrisTries; t++)
                {
                    var p = Rng.PointInDisc(Roll(), centre, radius);
                    float yaw = Rng.Float(Roll()) * MathF.Tau;
                    if (!walk.IsWalkable(p.X, p.Y)) continue;
                    pieces.Add(new NestDebrisPiece(e.Id, false, new Vector3(p.X, walk.GroundHeight(p.X, p.Y), p.Y), yaw));
                    break;
                }
        }
        return pieces;
    }

    /// <summary>
    /// Puts a loaded nest's debris into the world (serial): each BUILDING piece a complete <see cref="BuildSite"/> of the nest's faction (<see cref="BuildSite.Debris"/>; the lock system
    /// registers its cages and beds, the host shows it and lights its lamps), each NEST_ITEM piece a ground item (<see cref="Items.GroundItems.Scatter"/>). What was placed is noted on the
    /// site's state and removed when the nest unloads (<see cref="Drop"/>).
    /// </summary>
    void PlaceDebris(World world, WildSite site, SiteState state, List<NestDebrisPiece> debris)
    {
        var reg = world.Buildings;
        int k = 0;
        foreach (var piece in debris)
        {
            if (piece.Building)
            {
                if (data.Db.Find(piece.Record) is not { } record) continue;
                string id = string.Create(CultureInfo.InvariantCulture, $"nest{site.Index}-{k++}-debris");
                if (reg.Find(id) is not null) continue;
                var build = BuildingBuildData.From(record);
                reg.Add(new BuildSite
                {
                    Id = id, Record = record.StringId, Name = record.Name, Position = piece.Position, Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, piece.Yaw),
                    Owner = site.Faction, Target = build.Target, Rate = build.Rate, Progress = build.Target, Complete = true, Debris = true,
                    Materials = [.. build.Materials.Select(m => new MaterialSlot(m.Item, m.Required) { Delivered = m.Required })],
                });
                state.DebrisBuildings.Add(id);
            }
            else if (data.Items.Create(piece.Record) is { } item)
                state.DebrisItems.Add(world.GroundItems.Scatter(item, piece.Position, piece.Yaw));
        }
    }
}
