using System.Collections.Concurrent;
using CodeReach.Core;
using CodeReach.Core.Facts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.FileSystemGlobbing;

namespace CodeReach.Roslyn;

public sealed record IndexProgress(string Stage, int Done, int Total);

/// <summary>Absolute paths of source files that changed on disk since the facts were extracted.</summary>
public sealed record SourceChanges(IReadOnlyList<string> Changed, IReadOnlyList<string> Added, IReadOnlyList<string> Removed);

/// <summary>Loads a solution with MSBuild and extracts facts. The only place a semantic model exists.</summary>
public sealed class RoslynIndexer : IDisposable
{
    /// <summary>Bumped whenever extraction would produce different facts for the same source.</summary>
    public const int ExtractorVersion = 3;

    private static readonly string[] TestFrameworkAssemblies =
    [
        "xunit.core.dll", "xunit.v3.core.dll", "xunit.assert.dll", "xunit.v3.assert.dll", "nunit.framework.dll",
        "Microsoft.VisualStudio.TestPlatform.TestFramework.dll", "TUnit.Core.dll",
    ];

    private readonly MSBuildWorkspace _workspace;
    private readonly CodeReachConfig _config;
    private readonly ConcurrentQueue<string> _warnings = new();
    private readonly Matcher? _exclude;
    private SymbolIds _ids;
    private Solution _solution;

    private RoslynIndexer(MSBuildWorkspace workspace, Solution solution, string solutionPath, CodeReachConfig config)
    {
        _workspace = workspace;
        _solution = solution;
        _config = config;
        SolutionPath = solutionPath;
        Root = Path.GetDirectoryName(solutionPath)!;
        _ids = new SymbolIds(Relative);
        if (config.Exclude.Length > 0)
        {
            _exclude = new Matcher(StringComparison.OrdinalIgnoreCase);
            _exclude.AddIncludePatterns(config.Exclude);
        }
    }

    public string SolutionPath { get; }

    public string Root { get; }

    /// <summary>How many documents the last indexing or update actually re-extracted.</summary>
    public int LastExtracted { get; private set; }

    /// <summary>Directories of the indexed projects, for finding source files that were added on disk.</summary>
    public IReadOnlyList<string> ProjectDirectories => [.. SelectProjects().Select(p => Path.GetDirectoryName(p.FilePath)).OfType<string>().Distinct()];

    internal Solution Solution => _solution;

    internal SymbolIds SymbolIds => _ids;

    public static async Task<RoslynIndexer> OpenAsync(string solutionPath, CodeReachConfig config, IProgress<IndexProgress>? progress, CancellationToken cancel)
    {
        solutionPath = Path.GetFullPath(solutionPath);
        var workspace = MSBuildWorkspace.Create();
        var warnings = new List<string>();
        workspace.RegisterWorkspaceFailedHandler(failure =>
        {
            lock (warnings)
            {
                warnings.Add(failure.Diagnostic.Message);
            }
        });
        progress?.Report(new IndexProgress("loading solution", 0, 0));
        var isProject = solutionPath.EndsWith("proj", StringComparison.OrdinalIgnoreCase);
        var solution = isProject
            ? (await workspace.OpenProjectAsync(solutionPath, cancellationToken: cancel).ConfigureAwait(false)).Solution
            : await workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancel).ConfigureAwait(false);
        var indexer = new RoslynIndexer(workspace, solution, solutionPath, config);
        lock (warnings)
        {
            foreach (var warning in warnings.Distinct())
            {
                indexer._warnings.Enqueue(warning);
            }
        }

        return indexer;
    }

    public void Dispose() => _workspace.Dispose();

    public Task<IndexFacts> IndexAllAsync(IProgress<IndexProgress>? progress, CancellationToken cancel) =>
        ExtractAsync(null, _ => true, progress, cancel);

    /// <summary>
    /// Applies file changes to the loaded solution and re-extracts only what they can have affected: the changed
    /// documents when no declaration shape moved, otherwise their projects and every project that depends on them.
    /// </summary>
    public async Task<IndexFacts> UpdateAsync(IndexFacts previous, SourceChanges changes, IProgress<IndexProgress>? progress, CancellationToken cancel)
    {
        var known = previous.Documents.ToDictionary(d => d.Path, StringComparer.Ordinal);
        var reshaped = new HashSet<ProjectId>();
        var touched = new HashSet<string>(StringComparer.Ordinal);
        // A workspace opened after the change already reflects it, so the owning project is marked either way.
        foreach (var path in changes.Removed)
        {
            foreach (var id in _solution.GetDocumentIdsWithFilePath(path))
            {
                _solution = _solution.RemoveDocument(id);
            }

            if (OwnerOf(path) is { } owner)
            {
                reshaped.Add(owner.Id);
            }
        }

        foreach (var path in changes.Added)
        {
            if (OwnerOf(path) is not { } owner)
            {
                continue;
            }

            reshaped.Add(owner.Id);
            if (_solution.GetDocumentIdsWithFilePath(path).IsEmpty)
            {
                var text = SourceText.From(await File.ReadAllTextAsync(path, cancel).ConfigureAwait(false), System.Text.Encoding.UTF8);
                _solution = _solution.AddDocument(DocumentId.CreateNewId(owner.Id), Path.GetFileName(path), text, filePath: path);
            }
        }

        foreach (var path in changes.Changed)
        {
            foreach (var id in _solution.GetDocumentIdsWithFilePath(path))
            {
                var text = SourceText.From(await File.ReadAllTextAsync(path, cancel).ConfigureAwait(false), System.Text.Encoding.UTF8);
                _solution = _solution.WithDocumentText(id, text);
                var document = _solution.GetDocument(id)!;
                if (!Included(document))
                {
                    continue;
                }

                var relative = Relative(path);
                touched.Add(relative);
                var root = await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false);
                if (!known.TryGetValue(relative, out var before) || root is null || ShapeHasher.Hash(root) != before.ShapeHash)
                {
                    reshaped.Add(id.ProjectId);
                }
            }
        }

        var graph = _solution.GetProjectDependencyGraph();
        var affected = reshaped.SelectMany(id => graph.GetProjectsThatTransitivelyDependOnThisProject(id).Append(id)).ToHashSet();
        _ids = new SymbolIds(Relative);
        return await ExtractAsync(previous, document => affected.Contains(document.Project.Id) || touched.Contains(Relative(document.FilePath!)), progress, cancel)
            .ConfigureAwait(false);
    }

    /// <summary>Extracts the selected documents and keeps the previous facts of every other one that still exists.</summary>
    private async Task<IndexFacts> ExtractAsync(IndexFacts? previous, Func<Document, bool> selected, IProgress<IndexProgress>? progress, CancellationToken cancel)
    {
        var projects = SelectProjects();
        var kept = previous?.Documents.ToDictionary(d => d.Path, StringComparer.Ordinal) ?? [];
        var done = 0;
        var projectFacts = new List<ProjectFact>();
        var documents = new ConcurrentBag<DocumentFacts>();
        foreach (var project in projects)
        {
            cancel.ThrowIfCancellationRequested();
            var included = project.Documents.Where(Included).ToList();
            var targets = included.Where(d => selected(d) || !kept.ContainsKey(Relative(d.FilePath!))).ToList();
            foreach (var document in included.Except(targets))
            {
                documents.Add(kept[Relative(document.FilePath!)]);
            }

            if (targets.Count > 0)
            {
                var compilation = await project.GetCompilationAsync(cancel).ConfigureAwait(false);
                if (compilation is null)
                {
                    projectFacts.Add(Describe(project, 0, 0, "no compilation was produced", string.Empty));
                    continue;
                }

                var context = new ExtractionContext(compilation, _config, _ids, Root);
                await Parallel.ForEachAsync(targets, cancel, async (document, token) =>
                {
                    documents.Add(await ExtractAsync(document, project.Name, context, token).ConfigureAwait(false));
                    var current = Interlocked.Increment(ref done);
                    if (current % 100 == 0)
                    {
                        progress?.Report(new IndexProgress("extracting " + project.Name, current, 0));
                    }
                }).ConfigureAwait(false);
            }

            projectFacts.Add(Describe(project, included.Count, project.DocumentIds.Count - included.Count, null, SkipSummary(project)));
        }

        LastExtracted = done;
        return new IndexFacts(
            SolutionPath, Root,
            [.. projectFacts.OrderBy(p => p.Name, StringComparer.Ordinal)],
            [.. documents.OrderBy(d => d.Path, StringComparer.Ordinal)],
            [.. _warnings.Distinct().OrderBy(w => w, StringComparer.Ordinal)]);
    }

    /// <summary>The project a new source file belongs to under default globbing: the nearest project directory above it.</summary>
    private Project? OwnerOf(string path) => SelectProjects()
        .Where(p => p.FilePath is not null && path.StartsWith(Path.GetDirectoryName(p.FilePath)! + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        .OrderByDescending(p => p.FilePath!.Length).FirstOrDefault();

    internal async Task<DocumentFacts> ExtractAsync(Document document, string project, ExtractionContext context, CancellationToken cancel)
    {
        var tree = await document.GetSyntaxTreeAsync(cancel).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No syntax tree for {document.FilePath}");
        var root = await tree.GetRootAsync(cancel).ConfigureAwait(false);
        var model = context.Compilation.GetSemanticModel(tree);
        var path = Relative(document.FilePath!);
        var walker = new FactWalker(model, context, path, cancel);
        walker.Visit(root);
        var errors = model.GetDiagnostics(cancellationToken: cancel).Count(d => d.Severity == DiagnosticSeverity.Error);
        return new DocumentFacts(
            path, project, ShapeHasher.Hash(root), errors, [.. walker.Symbols], [.. walker.References], [.. walker.Types],
            [.. walker.Overrides], [.. walker.Endpoints], [.. walker.GroupArgs], [.. walker.EntryPoints],
            [.. walker.Services], [.. walker.Dependencies]);
    }

    /// <summary>C# projects, one per project file: a multi-targeted project is indexed for its first target only.</summary>
    private List<Project> SelectProjects()
    {
        var chosen = new List<Project>();
        var graph = _solution.GetProjectDependencyGraph();
        foreach (var id in graph.GetTopologicallySortedProjects())
        {
            var project = _solution.GetProject(id)!;
            if (project.Language != LanguageNames.CSharp)
            {
                _warnings.Enqueue($"{project.Name} is {project.Language} and was skipped");
                continue;
            }

            if (chosen.Any(p => p.FilePath == project.FilePath))
            {
                _warnings.Enqueue($"{project.Name} is another target framework of an indexed project and was skipped");
                continue;
            }

            chosen.Add(project);
        }

        return chosen;
    }

    private ProjectFact Describe(Project project, int documents, int skipped, string? failure, string skipDetail)
    {
        var evidence = project.MetadataReferences.OfType<PortableExecutableReference>()
            .Select(r => Path.GetFileName(r.FilePath) ?? string.Empty)
            .FirstOrDefault(file => TestFrameworkAssemblies.Contains(file, StringComparer.OrdinalIgnoreCase));
        var configured = _config.TestProjects.Contains(project.Name, StringComparer.OrdinalIgnoreCase);
        var references = project.ProjectReferences.Select(r => _solution.GetProject(r.ProjectId)?.Name).OfType<string>()
            .Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();
        return new ProjectFact(
            project.Name, Relative(project.FilePath ?? project.Name), evidence is not null || configured,
            evidence is not null ? "references " + evidence : configured ? "listed in testProjects" : string.Empty,
            references, documents, skipped, failure, skipDetail);
    }

    private bool Included(Document document) => SkipReason(document) is null;

    /// <summary>Why a document is not indexed, or null when it is.</summary>
    private string? SkipReason(Document document)
    {
        if (document.FilePath is null || !document.SupportsSyntaxTree)
        {
            return "no source file";
        }

        var path = Relative(document.FilePath);
        if (path.StartsWith("..", StringComparison.Ordinal))
        {
            return "outside the solution folder";
        }

        var segments = path.Split('/');
        if (segments.Contains("obj") || segments.Contains("bin"))
        {
            return "generated (obj/bin)";
        }

        if (_exclude?.Match(path).HasMatches == true)
        {
            var pattern = _config.Exclude.FirstOrDefault(p =>
            {
                var single = new Matcher(StringComparison.OrdinalIgnoreCase);
                single.AddInclude(p);
                return single.Match(path).HasMatches;
            });
            return "excluded by " + (pattern ?? "configuration");
        }

        return null;
    }

    private string SkipSummary(Project project) => string.Join(", ", project.Documents
        .Select(SkipReason).OfType<string>()
        .GroupBy(r => r, StringComparer.Ordinal)
        .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => $"{g.Count()} {g.Key}"));

    private string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');
}
