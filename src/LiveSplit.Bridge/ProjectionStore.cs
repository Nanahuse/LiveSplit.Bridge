using System;
using System.Threading;
using LiveSplit.Bridge.Protocol.V2;

namespace LiveSplit.Bridge;

/// <summary>
/// A complete, immutable view of the detailed bridge state. The run, attempt and
/// runtime projections and their revisions always belong to the same generation,
/// so a reader can never observe a revision that does not match its projection.
/// </summary>
internal sealed class ProjectionSnapshot
{
    public ProjectionSnapshot(
        RunState run,
        AttemptState attempt,
        RuntimeState runtime,
        ulong runRevision,
        ulong attemptRevision,
        ulong runtimeRevision)
    {
        Run = run ?? throw new ArgumentNullException(nameof(run));
        Attempt = attempt ?? throw new ArgumentNullException(nameof(attempt));
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        RunRevision = runRevision;
        AttemptRevision = attemptRevision;
        RuntimeRevision = runtimeRevision;
    }

    public RunState Run { get; }

    public AttemptState Attempt { get; }

    public RuntimeState Runtime { get; }

    public ulong RunRevision { get; }

    public ulong AttemptRevision { get; }

    public ulong RuntimeRevision { get; }
}

/// <summary>
/// Holds the currently published projection snapshot. Publishing a new snapshot
/// is a single atomic reference swap; readers always see a completed snapshot.
/// </summary>
internal sealed class ProjectionStore
{
    private ProjectionSnapshot current;

    public ProjectionStore(ProjectionSnapshot initial)
    {
        current = initial ?? throw new ArgumentNullException(nameof(initial));
    }

    public ProjectionSnapshot Current => Volatile.Read(ref current);

    public void Commit(ProjectionSnapshot snapshot)
    {
        Volatile.Write(ref current, snapshot ?? throw new ArgumentNullException(nameof(snapshot)));
    }
}
