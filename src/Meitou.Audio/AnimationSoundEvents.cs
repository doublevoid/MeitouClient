using Meitou.Data;
using Meitou.Data.Fcs;

namespace Meitou.Audio;

/// <summary>
/// One sound riding on an animation (an ANIMATION_EVENT record the way the original's <c>AnimationSFXEvent</c> keeps it, docs/audio.md "Footsteps and animation events"):
/// the Wwise event to post at <see cref="Fraction"/> of the clip, the switch to set first (<see cref="OverrideState"/> = <see cref="OverrideValue"/>) and the event
/// to post when the animation ends (<see cref="Stop"/>). <see cref="Footstep"/> is an event called exactly <c>Footsteps</c>.
/// </summary>
public sealed record AnimationSoundEvent(string Event, string OverrideState, string OverrideValue, string Stop, bool Footstep, float Fraction);

/// <summary>
/// The sound events of the animation records: the <c>events</c> of ANIMATION, ANIMAL_ANIMATION and COMBAT_TECHNIQUE records and the <c>animation events</c> of
/// BUILDING_FUNCTIONALITY ones, each a reference to an ANIMATION_EVENT with the time in per cent as its first value (<b>Verified</b>: the exe adds an event at value x 0.01, <c>FUN_140064e10</c>).
/// Kept per record string id, sorted by time like the original's insertion (<c>FUN_140064870</c>). Records whose <c>event</c> is empty make no sound event.
/// </summary>
public sealed class AnimationSoundEvents
{
    static readonly string[] Lists = ["events", "animation events"];
    readonly Dictionary<string, AnimationSoundEvent[]> byRecord = new(StringComparer.Ordinal);

    AnimationSoundEvents() { }

    public static AnimationSoundEvents FromDatabase(GameDatabase db)
    {
        var result = new AnimationSoundEvents();
        foreach (var type in new[] { FcsRecordType.ANIMATION, FcsRecordType.ANIMAL_ANIMATION, FcsRecordType.COMBAT_TECHNIQUE, FcsRecordType.BUILDING_FUNCTIONALITY })
            foreach (var record in db.OfType(type))
            {
                List<AnimationSoundEvent>? list = null;
                foreach (var name in Lists)
                    foreach (var reference in record.GetReferences(name))
                    {
                        if (db.Find(reference.TargetStringId) is not { Type: FcsRecordType.ANIMATION_EVENT } def || def.GetString("event") is not { Length: > 0 } ev) continue;
                        var sound = new AnimationSoundEvent(ev, def.GetString("override state"), def.GetString("override value"), def.GetString("stop"), ev == "Footsteps", reference.Values.Value0 * 0.01f);
                        Insert(list ??= [], sound);
                    }
                if (list is not null) result.byRecord[record.StringId] = [.. list];
            }
        return result;
    }

    /// <summary>The events of the record with this string id, in time order; null when it has none.</summary>
    public AnimationSoundEvent[]? Of(string recordId) => byRecord.GetValueOrDefault(recordId);

    readonly Dictionary<(string, string), AnimationSoundEvent[]?> merged = [];

    /// <summary>
    /// An animation's events with a BUILDING_FUNCTIONALITY's <c>animation events</c> added, as an operator's clip carries them (<b>Observed</b>: Task_OperateMachine's work slot
    /// <c>FUN_14035bc90</c> and Task_UseTrainingDummy's <c>FUN_14035c1e0</c> add the functionality's list to the state of the clip they play, through the same insertion,
    /// docs/audio.md "Footsteps and animation events"). Kept per pair, so the same pair gives the same array; null when neither has events.
    /// </summary>
    public AnimationSoundEvent[]? With(string animationId, string functionalityId)
    {
        if (merged.TryGetValue((animationId, functionalityId), out var known)) return known;
        var own = Of(animationId);
        var added = Of(functionalityId);
        AnimationSoundEvent[]? result = own;
        if (added is not null)
        {
            var list = own is null ? new List<AnimationSoundEvent>() : [.. own];
            foreach (var sound in added) Insert(list, sound);
            result = [.. list];
        }
        merged[(animationId, functionalityId)] = result;
        return result;
    }

    // Insert before the first event later than this one; the same event at the same time twice is kept once (FUN_140064870).
    static void Insert(List<AnimationSoundEvent> list, AnimationSoundEvent sound)
    {
        int at = 0;
        for (; at < list.Count; at++)
        {
            if (list[at].Fraction == sound.Fraction && list[at].Event == sound.Event && list[at].OverrideValue == sound.OverrideValue) return;
            if (sound.Fraction < list[at].Fraction) break;
        }
        list.Insert(at, sound);
    }

    /// <summary>How many records carry events.</summary>
    public int Records => byRecord.Count;
}

/// <summary>
/// The position of one animation's events (<b>Verified</b>, <c>FUN_140064690</c>): the last fraction seen and the next event to fire. An update with an unchanged
/// fraction does nothing; one that went back (the clip looped) first fires the events left from the pass before, then goes on from the first. While the animation
/// is not <paramref name="enabled"/> only the fraction is kept, so an animation that becomes the dominant one fires every event up to its fraction at once.
/// </summary>
public struct AnimationEventCursor
{
    public float Last;
    public int Next;

    public void Update(AnimationSoundEvent[] events, float fraction, bool enabled, Action<AnimationSoundEvent> fire)
    {
        if (events.Length == 0 || fraction == Last) return;
        bool wrapped = fraction < Last;
        Last = fraction;
        if (!enabled) return;
        if (wrapped && Next == events.Length)
        {
            Next = 0;
            wrapped = false;
        }
        while (Next < events.Length)
        {
            if (!wrapped && fraction < events[Next].Fraction) return;
            fire(events[Next]);
            Next++;
            if (wrapped && Next == events.Length)
            {
                Next = 0;
                wrapped = false;
            }
        }
    }
}
