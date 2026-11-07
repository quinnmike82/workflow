namespace Workflow;

public enum ApprovalMode
{
    /// <summary>Any one actor in the list is enough.</summary>
    Any,

    /// <summary>Every actor in the list needs one person to act (parallel review).</summary>
    All,

    /// <summary>N of the actors are enough.</summary>
    Quorum,
}

/// <summary>
/// Who, and how many, are needed to complete this step.
/// Add <c>using static Workflow.Approval;</c> to write <c>All(...)</c>, <c>Any(...)</c>, <c>Quorum(...)</c>.
/// A single <see cref="Actor"/> converts implicitly to <c>Any(actor)</c>.
/// </summary>
public sealed class Approval
{
    private readonly Actor[] _actors;

    private Approval(ApprovalMode mode, int required, Actor[] actors)
    {
        if (actors.Length == 0) throw new ArgumentException("At least one actor is required.", nameof(actors));
        if (actors.Any(a => a is null)) throw new ArgumentException("Actor is null. Check static field declaration order.", nameof(actors));
        if (actors.Select(a => a.Key).Distinct().Count() != actors.Length)
            throw new ArgumentException("Actor keys must be unique within one approval.", nameof(actors));
        if (required < 1 || required > actors.Length)
            throw new ArgumentOutOfRangeException(nameof(required), $"Required must be between 1 and {actors.Length}.");

        Mode = mode;
        Required = required;
        _actors = actors.ToArray();
    }

    public ApprovalMode Mode { get; }

    /// <summary>Number of distinct actors that must act to complete the step.</summary>
    public int Required { get; }

    public IReadOnlyList<Actor> Actors => _actors;

    public bool IsParallel => Required > 1;

    public static Approval Any(params Actor[] actors) => new(ApprovalMode.Any, 1, actors);

    public static Approval All(params Actor[] actors) => new(ApprovalMode.All, actors.Length, actors);

    public static Approval Quorum(int required, params Actor[] actors) => new(ApprovalMode.Quorum, required, actors);

    public static implicit operator Approval(Actor actor) => Any(actor);

    public override string ToString()
    {
        var keys = string.Join(", ", _actors.Select(a => a.Key));
        return Mode switch
        {
            ApprovalMode.Any when _actors.Length == 1 => _actors[0].Key,
            ApprovalMode.Any => $"ANY({keys})",
            ApprovalMode.All => $"ALL({keys})",
            _ => $"{Required} OF ({keys})",
        };
    }
}
