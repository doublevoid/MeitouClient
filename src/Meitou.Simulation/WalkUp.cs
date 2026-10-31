using System.Numerics;

namespace Meitou.Simulation;

/// <summary>
/// A player character walking up to a trader on a trade order (as the original's order 12 <c>Task_PlayerTalkto</c>: walking there is the task's precondition, so the
/// walker keeps heading for where the target is now). The first leg is a <see cref="MoveOrder"/> (it gets a sleeper up and ends a fight); afterwards the walker is sent again
/// whenever the target has moved <paramref name="retarget"/> from the goal or the walker has stopped short. An order the player gave the walker since (another goal, another task)
/// ends the walk. Serial phases only.
/// </summary>
internal sealed class WalkUp
{
    long started = -1;
    Vector2 goal;
    bool adopted;

    /// <summary>Sends <paramref name="walker"/> towards <paramref name="at"/> (the target's position) with a move order on the next tick.</summary>
    public void Start(World world, CharacterId walker, Vector3 at)
    {
        started = world.Tick;
        adopted = false;
        world.Defer(new MoveOrder([walker], at) { Tick = world.Tick + 1 });
    }

    /// <summary>Forgets the walk (no order is sent).</summary>
    public void Clear() => (started, adopted) = (-1, false);

    /// <summary>
    /// Once a tick while out of reach: keeps the walker in <paramref name="walker"/> heading for the character in <paramref name="target"/>. False when the player has sent the
    /// walker elsewhere or given it another task since the order, so the caller drops its approach.
    /// </summary>
    public bool Step(World world, int walker, int target, float retarget)
    {
        if (world.Tick <= started) return true;   // the first leg's move order is not in yet
        var table = world.Characters;
        ref var n = ref table.Next[walker];
        var cold = table.Cold(walker)!;
        bool going = n.Task == (byte)CharacterTask.GoTo;
        if (going && !adopted) { goal = n.Goal; adopted = true; }   // the move order's goal (the target's spot, put onto a floor)
        else if (going && n.Goal != goal) return false;   // another goal: the player sent it elsewhere
        else if (!going && n.Task != (byte)CharacterTask.Idle) return false;   // another task
        var to = table.Next[target].Position;
        if (going && Vector2.Distance(goal, new(to.X, to.Z)) <= retarget) return true;
        goal = MovementSystem.GoToFloor(world, walker, to, cold.RunSpeed);
        cold.OrderMove = true;
        adopted = true;
        return true;
    }
}
