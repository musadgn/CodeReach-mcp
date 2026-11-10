using CodeReach.Core.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Xunit;

namespace CodeReach.Tests;

/// <summary>
/// Compares the index with Roslyn's own reference finder, which answers the same questions by a different
/// search. Documentation comment references are left out on both sides: the index does not record them.
/// </summary>
[Collection(SampleCollection.Name)]
public sealed class OracleTests(SampleFixture sample)
{
    [Fact]
    public async Task Usages_of_every_source_member_equal_the_reference_finder()
    {
        var graph = sample.Graph;
        var solution = sample.Indexer.Solution;
        var examined = 0;
        var locations = 0;
        var differences = new List<string>();
        foreach (var (id, symbol) in await SourceSymbolsAsync())
        {
            var expected = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var reference in await SymbolFinder.FindReferencesAsync(symbol, solution, TestContext.Current.CancellationToken))
            {
                // The finder files `new T()` and `[T]` under the constructor only; the index also counts them as uses of T.
                var definition = reference.Definition;
                var ofThisType = symbol is INamedTypeSymbol && definition is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor
                    && SymbolEqualityComparer.Default.Equals(constructor.ContainingType, symbol);
                if (sample.Indexer.SymbolIds.IdOf(definition) != id && !ofThisType)
                {
                    continue;
                }

                // Implicit locations are target-typed `new(...)`: a real constructor call, but not a mention of the type name.
                var implicitToo = symbol is IMethodSymbol { MethodKind: MethodKind.Constructor };
                foreach (var location in reference.Locations.Where(l => (implicitToo || !l.IsImplicit) && Indexed(l.Document) && !InDocumentation(l)))
                {
                    expected.Add(Site(location.Document.FilePath!, location.Location));
                }
            }

            var actual = new SortedSet<string>(graph.RowsTo(id).Select(r => $"{r.File}:{r.Fact.Line}"), StringComparer.Ordinal);
            examined += expected.Count > 0 ? 1 : 0;
            locations += expected.Count;
            if (!expected.SetEquals(actual))
            {
                differences.Add($"{id}\n    only oracle: {string.Join(", ", expected.Except(actual))}\n    only index:  {string.Join(", ", actual.Except(expected))}");
            }
        }

        Assert.True(examined >= 60, $"only {examined} symbols had references to compare");
        Assert.True(locations >= 150, $"only {locations} reference locations were compared");
        Assert.True(differences.Count == 0, $"{differences.Count} symbols differ:\n" + string.Join("\n", differences));
    }

    [Fact]
    public async Task Callers_are_a_subset_of_the_cascading_caller_finder()
    {
        var graph = sample.Graph;
        var solution = sample.Indexer.Solution;
        var examined = 0;
        var narrowed = 0;
        foreach (var (id, symbol) in await SourceSymbolsAsync())
        {
            if (symbol is not IMethodSymbol { MethodKind: MethodKind.Ordinary })
            {
                continue;
            }

            var oracle = (await SymbolFinder.FindCallersAsync(symbol, solution, TestContext.Current.CancellationToken))
                .Select(c => sample.Indexer.SymbolIds.IdOf(c.CallingSymbol)).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var mine = graph.Callers(id).Where(e => e.Row.Fact.Kind == Core.Facts.RefKind.Invoke).Select(e => e.Row.Fact.From).ToHashSet(StringComparer.Ordinal);
            examined += mine.Count;
            narrowed += oracle.Except(mine).Count();
            var extra = mine.Except(oracle).Where(from => !from.StartsWith(Core.Facts.Ids.HandlerPrefix, StringComparison.Ordinal)).ToList();
            Assert.True(extra.Count == 0, $"{id} has callers the oracle does not know: {string.Join(", ", extra)}");
        }

        Assert.True(examined >= 60, $"only {examined} caller relations were compared");

        // The oracle cascades through generic interfaces regardless of receiver type; the index narrows by it.
        Assert.True(narrowed > 0, "receiver narrowing never removed a cascaded caller, so the sample no longer exercises it");
    }

    private static string Site(string path, Location location) =>
        $"{Path.GetRelativePath(SampleFixture.SampleDirectory, path).Replace('\\', '/')}:{location.GetLineSpan().StartLinePosition.Line + 1}";

    private static bool InDocumentation(ReferenceLocation location) =>
        location.Location.SourceTree!.GetRoot().FindTrivia(location.Location.SourceSpan.Start, findInsideTrivia: false).HasStructure;

    private bool Indexed(Document document) =>
        sample.Facts.Documents.Any(d => d.Path == Path.GetRelativePath(SampleFixture.SampleDirectory, document.FilePath!).Replace('\\', '/'));

    private async Task<List<(string Id, ISymbol Symbol)>> SourceSymbolsAsync()
    {
        var result = new Dictionary<string, ISymbol>(StringComparer.Ordinal);
        foreach (var project in sample.Indexer.Solution.Projects)
        {
            var compilation = (await project.GetCompilationAsync(TestContext.Current.CancellationToken))!;
            var pending = new Stack<INamespaceOrTypeSymbol>([compilation.Assembly.GlobalNamespace]);
            while (pending.Count > 0)
            {
                foreach (var member in pending.Pop().GetMembers())
                {
                    if (member is INamespaceOrTypeSymbol container)
                    {
                        pending.Push(container);
                    }

                    var tracked = member is INamedTypeSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol
                        or IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.Constructor or MethodKind.UserDefinedOperator };
                    if (tracked && !member.IsImplicitlyDeclared && sample.Indexer.SymbolIds.IdOf(member) is { } id && sample.Graph.IsDeclared(id))
                    {
                        result.TryAdd(id, member);
                    }
                }
            }
        }

        return result.Select(p => (p.Key, p.Value)).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
    }
}
