using CodeReach.Core.Graph;

namespace CodeReach.Core.Queries;

/// <summary>One step of a trace: the symbol, and the call that links it to the step below it.</summary>
public sealed record TraceHop(string Symbol, CallEdge? Calls);

public sealed record TraceResult(
    IReadOnlyList<IReadOnlyList<TraceHop>> Rooted,
    IReadOnlyList<IReadOnlyList<TraceHop>> Unrooted,
    int Visited,
    int HiddenTestSites,
    bool Truncated);

/// <summary>Reachability queries. All of them go through <see cref="CodeGraph.Callers"/>.</summary>
public static class Reach
{
    public static (List<CallEdge> Shown, int HiddenTestSites) Callers(CodeGraph graph, Selection selection, bool includeTests)
    {
        var seen = new HashSet<int>();
        var all = selection.Symbols.SelectMany(s => graph.Callers(s.Id, selection.Through)).Where(e => seen.Add(e.Row.Index)).ToList();
        var shown = includeTests ? all : all.Where(e => !e.Row.IsTest).ToList();
        return (shown, all.Count - shown.Count);
    }

    /// <summary>
    /// Walks callers upward until entry points. A path that ends at a symbol nothing calls is unrooted:
    /// reach beyond it is invisible to the compiler (dependency injection, reflection) or does not exist.
    /// </summary>
    public static TraceResult Trace(CodeGraph graph, Selection selection, bool includeTests, int maxDepth)
    {
        var parents = new Dictionary<string, (string Child, CallEdge Edge)>(StringComparer.Ordinal);
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        var rooted = new List<string>();
        var unrooted = new List<string>();
        var hidden = 0;
        var truncated = false;
        foreach (var start in selection.Symbols)
        {
            if (depth.TryAdd(start.Id, 0))
            {
                Classify(start.Id);
            }
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var edges = graph.Callers(current, depth[current] == 0 ? selection.Through : null);
            var visible = includeTests ? edges : edges.Where(e => !e.Row.IsTest).ToList();
            hidden += edges.Count - visible.Count;
            var callers = visible.Where(e => e.Row.Fact.From != current).ToList();
            if (callers.Count == 0)
            {
                unrooted.Add(current);
                continue;
            }

            if (depth[current] >= maxDepth || depth.Count >= Limits.MaxTraceNodes)
            {
                truncated = true;
                continue;
            }

            foreach (var edge in callers)
            {
                var caller = edge.Row.Fact.From;
                if (depth.TryAdd(caller, depth[current] + 1))
                {
                    parents[caller] = (current, edge);
                    Classify(caller);
                }
            }
        }

        return new TraceResult(rooted.Select(Path).ToList(), unrooted.Select(Path).ToList(), depth.Count, hidden, truncated);

        void Classify(string id)
        {
            if (graph.IsEntryPoint(id))
            {
                rooted.Add(id);
            }
            else
            {
                queue.Enqueue(id);
            }
        }

        IReadOnlyList<TraceHop> Path(string top)
        {
            var hops = new List<TraceHop>();
            var current = top;
            while (parents.TryGetValue(current, out var link))
            {
                hops.Add(new TraceHop(current, link.Edge));
                current = link.Child;
            }

            hops.Add(new TraceHop(current, null));
            return hops;
        }
    }
}
