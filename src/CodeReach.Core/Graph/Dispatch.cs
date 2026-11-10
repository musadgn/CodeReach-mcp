using CodeReach.Core.Facts;

namespace CodeReach.Core.Graph;

/// <summary>Dispatch resolution and the caller relation. Every tool goes through <see cref="Callers"/>.</summary>
public sealed partial class CodeGraph
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string To, string? Receiver, bool Abstract), IReadOnlyList<string>> _reachable = new();

    /// <summary>Can a value whose static type is <paramref name="receiver"/> be an instance of <paramref name="type"/>?</summary>
    public static bool Assignable(TypeNode type, string? receiver, string target)
    {
        string? definition;
        var loose = type.Fact.IsGeneric;
        if (receiver is null)
        {
            definition = Ids.ContainingTypeOf(target);
            loose = true;
        }
        else
        {
            if (type.Id == receiver || type.BaseKeys.Contains(receiver))
            {
                return true;
            }

            definition = Ids.DefOf(receiver);
            loose |= Ids.IsOpen(receiver) || Ids.IsVariant(receiver);
        }

        return loose && definition is not null && (type.Id == definition || type.BaseDefs.Contains(definition));
    }

    /// <summary>The definitions a reference can execute at run time.</summary>
    public IReadOnlyList<string> Reachable(RefRow row)
    {
        var fact = row.Fact;
        if (fact.Dispatch == Dispatch.None || fact.Kind is not (RefKind.Invoke or RefKind.MethodGroup or RefKind.Read or RefKind.Write))
        {
            return [fact.To];
        }

        return _reachable.GetOrAdd((fact.To, fact.Receiver, fact.TargetAbstract), key => Resolve(key.To, key.Receiver, key.Abstract, fact.Dispatch));
    }

    /// <summary>Every call of <paramref name="definition"/>, optionally only those a <paramref name="through"/> instance can receive.</summary>
    public List<CallEdge> Callers(string definition, TypeNode? through = null)
    {
        var edges = new List<CallEdge>();
        var seen = new HashSet<int>();
        foreach (var row in RowsTo(definition))
        {
            if (row.IsCall && Receives(through, row) && seen.Add(row.Index))
            {
                edges.Add(new CallEdge(row, definition, null));
            }
        }

        foreach (var up in UpClosure(definition))
        {
            foreach (var row in RowsTo(up))
            {
                if (row.IsCall && Receives(through, row) && !seen.Contains(row.Index) && Reachable(row).Contains(definition))
                {
                    seen.Add(row.Index);
                    edges.Add(new CallEdge(row, definition, up));
                }
            }
        }

        foreach (var (_, impl) in ImplementationsOf(definition))
        {
            foreach (var row in RowsTo(impl))
            {
                if (row.IsCall && seen.Add(row.Index))
                {
                    edges.Add(new CallEdge(row, definition, impl));
                }
            }
        }

        return edges;
    }

    /// <summary>The calls made by <paramref name="from"/>, expanded to the definitions they can reach.</summary>
    public List<CallEdge> Callees(string from)
    {
        var edges = new List<CallEdge>();
        foreach (var row in RowsFrom(from))
        {
            if (!row.IsCall)
            {
                continue;
            }

            var targets = Reachable(row);
            if (targets.Count == 0)
            {
                edges.Add(new CallEdge(row, row.Fact.To, null));
            }

            foreach (var target in targets)
            {
                edges.Add(new CallEdge(row, target, target == row.Fact.To ? null : row.Fact.To));
            }
        }

        return edges;
    }

    /// <summary>Transitive closure of <see cref="UpOf"/>, excluding the start.</summary>
    public IReadOnlyList<string> UpClosure(string id)
    {
        var result = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue(id);
        while (queue.Count > 0)
        {
            foreach (var up in UpOf(queue.Dequeue()))
            {
                if (up != id && !result.Contains(up))
                {
                    result.Add(up);
                    queue.Enqueue(up);
                }
            }
        }

        return result;
    }

    private static bool Receives(TypeNode? through, RefRow row) =>
        through is null || row.Fact.Kind is RefKind.Construct or RefKind.BaseCall || Assignable(through, row.Fact.Receiver, row.Fact.To);

    private IReadOnlyList<string> Resolve(string to, string? receiver, bool isAbstract, Dispatch dispatch)
    {
        var targets = new List<string>();
        if (dispatch == Dispatch.Interface)
        {
            foreach (var (type, impl) in ImplementationsOf(to))
            {
                if (type.IsConcrete && Assignable(type, receiver, to) && !targets.Contains(impl))
                {
                    targets.Add(impl);
                }
            }

            return targets;
        }

        if (!isAbstract)
        {
            targets.Add(to);
        }

        var queue = new Queue<string>(OverridersOf(to));
        while (queue.Count > 0)
        {
            var candidate = queue.Dequeue();
            foreach (var deeper in OverridersOf(candidate))
            {
                queue.Enqueue(deeper);
            }

            var owner = Ids.ContainingTypeOf(candidate);
            if (owner is not null && _types.TryGetValue(owner, out var type) && Assignable(type, receiver, to)
                && !targets.Contains(candidate) && (Node(candidate).Fact.Flags & SymFlags.Abstract) == 0)
            {
                targets.Add(candidate);
            }
        }

        return targets;
    }
}
