# CodeReach.Mcp — Plan

Status: rev 2, phases 0 to 6 BUILT and verified locally on 2026-11-10; phases 7 and 8 not started.
Survived one adversarial review; section 3 lists what it killed. Nothing is committed or pushed yet.
Baseline: empty repository. Owner decisions are marked **[DECIDE]** and are never made by an agent.
Companion: [ARCHITECTURE.md](ARCHITECTURE.md) describes the design this plan builds.

## 1 Vision

A Roslyn MCP server for ASP.NET Core / C# solutions that answers one question well:
**which endpoints and jobs reach this code, and what does your diff affect.**

It combines two things existing tools offer only separately: compiler accuracy (every edge comes from the
Roslyn semantic model, never from text or heuristics) and a persistent, self-refreshing graph that answers
in milliseconds.

```
 .slnx/.sln ──► Roslyn indexer ──► document facts ──► CodeGraph (immutable snapshot)
                 (slow, rare)        (persisted)            │
                                                            ▼
                                 one query engine ──► tool registry ──► MCP door (stdio)
                                                                   └──► CLI door
```

## 2 Research verdicts

| Subject | Finding | Verdict |
|---|---|---|
| tree-sitter + heuristic indexers | Chains stop at interfaces, calls bind to the wrong symbol, tests leak, routes missed, counts disagree between tools | Reject the approach. Keep the product shape: instant answers, persistent index, watcher, overview, diff → impact |
| RoslynCodeLens.Mcp 2.18.2 and similar browsers | Accurate per-symbol lookups, but no automatic interface-crossing trace to entry points, no route + auth listing, no diff → endpoints | Different product. CodeReach is reach-first, not a general symbol browser |
| `Microsoft.CodeAnalysis.Workspaces.MSBuild` 5.9.0 | Loads `.slnx` on SDK 10.0.302 with its own out-of-process build host; `Microsoft.Build.Locator` must NOT be referenced (build error MSBL001) | Adopt, without the locator |
| `ModelContextProtocol` 2.4.0 (official C# SDK) | stdio hosting, tool errors as `IsError` results, `McpClient` + `StdioClientTransport` for wire tests | Adopt |
| .NET tool packaging | `PackAsTool` + package type `McpServer` + `.mcp/server.json`; clients run `dnx CodeReach.Mcp --yes` | Adopt |
| Package id | `CodeReach.Mcp` and `CodeReach` are unregistered on nuget.org (checked 2026-11-10) | Available |

## 3 What review changed (rev 1 → rev 2)

A second agent red-teamed rev 1 with throwaway Roslyn experiments. RAN = it executed a counterexample.

| Killed in rev 1 | Evidence | Rev 2 |
|---|---|---|
| Ids are bare documentation ids | RAN: two top-level `Program`s, two `file class`es and linked files collide | Source symbols carry their assembly: `M:[Assembly]Ns.Type.Member(...)`; `file` types add their file; one target framework is indexed per project |
| Phase 1 gate "callers equal `SymbolFinder`" | RAN: the oracle cascades through generic interfaces that receiver narrowing rightly excludes | Usages must equal the oracle per definition; callers must be a subset of the cascaded oracle, every difference explained by a named rule |
| Caller relation clause "bound target implements or overrides D" | RAN: `derived.V()` was reported as a caller of `Base.V`, which never runs | The clause keeps interface implementation only; overrides are dropped from it |
| Type-key equality for assignability | RAN: variant interfaces (`IHandler<in T>`) lost edges | Variant receivers compare by definition; keys are built from type ids, so tuple names and nullability cannot differ |
| "Body-only" incremental rule left the shape undefined | RAN: a constant initializer changed an attribute argument in another file | Shape is every token outside method, accessor and top-level statement bodies: initializers, attributes, usings, defaults and enum values are all declaration |
| Stamps cover "source and project files" | READ: added files, imported props, assets file, config and extractor version were not covered | Stamp set is the enumerated source set plus build inputs; snapshot identity includes config hash and extractor version; stamps are taken before Roslyn reads; near-simultaneous edits are re-hashed |
| "`auth: none` is a finding" | RAN: attributes on an overridden base action were missed; global policies were not considered | Attribute lookup walks the override chain; a fallback policy, a global authorize filter or `MapControllers().RequireAuthorization()` is detected and reported beside every "none declared" |
| Name-walk extraction | RAN: operators, indexers and target-typed `new` were missed | Those are bound explicitly. Still not modelled, and listed as known gaps: implicit user-defined conversions, and the pattern calls behind `foreach`, `using`, `await` and deconstruction |
| Wire-test gate listed 11 tools two phases early | READ | The doors ship after `impact`; the gate covers every registered tool |
| "Byte-identical snapshots" | READ: snapshots hold stamps and times | Determinism compares a canonical hash of the facts |
| Invariant "callers ⊆ usages" | INFERRED: a call bound to the interface is not a stored usage of the implementation | Restated: directly bound callers ⊆ usages |
| Trace drops symbols whose callers form a cycle | INFERRED | Reported as a third outcome next to rooted and unrooted |

Accepted without change: local functions and lambdas were already attributed to the enclosing member;
handler lambdas already had synthetic ids.

## 4 Locked decisions

1. **Semantic only.** Every edge is produced by binding a syntax node with the Roslyn semantic model. No
   text matching decides an edge. The only name-based rules are the ones ASP.NET Core itself applies (the
   `*Controller` convention) and names the user lists in configuration.
2. **One model.** All tools read the same immutable `CodeGraph` through the same query functions. A caller
   count, a usage count and a trace hop are the same rows seen three ways; they cannot disagree.
3. **Dispatch is resolved, and labelled.** Calls through an interface or a virtual member are expanded to the
   implementations a receiver of that static type can reach, and each hop says what it crossed.
4. **Tests are a property of the project, not of a name.** A project is a test project when it references
   xUnit, NUnit, MSTest or TUnit (or is listed in config). Tests are excluded by default in every tool, and
   every answer states how many rows that hid.
5. **Plain MCP server.** No hooks, skills, agent profiles or installers. Nothing writes to the client's
   configuration or into the analysed repository.
6. **Read-only towards the analysed solution.** The index lives in the OS cache directory.
7. **Core has no Roslyn reference.** Graph and queries are a pure library; the wall is the project reference.
8. **Refusals are `CODE: reason + remedy`**, never an empty success. Stale answers are stamped as stale.
9. **Nothing from any private codebase enters the repository**: no names, code, examples or fixtures. All
   tests run on the fictional sample solution in `samples/`.
10. **Stack:** .NET 10, C#, MIT, xUnit v3. No dependency beyond Roslyn, the MCP SDK and Microsoft.Extensions.
11. **"Could not run" is never "found nothing".** Every result carries a completion state: `complete`,
    `partial` (some project or document failed to load or bind; the answer names them) or `failed`. A zero is
    only ever printed for a complete analysis. On the CLI this is the exit code: 0 clean, 1 finding, 2 could
    not run.
12. **One registry, many doors.** MCP tools and CLI commands are the same registry entries. A door converts
    its transport to a request and a result back; it holds no query logic, so two doors cannot disagree.
13. **Layer walls are tests.** Besides the project-reference wall, an architecture test fails the build when
    a namespace in one layer references a layer it must not.

## 5 Components

Detail and rationale are in ARCHITECTURE.md; this section fixes scope and gates.

| Component | Responsibility | Boundary honesty |
|---|---|---|
| `CodeReach.Core` | Facts model, graph build, dispatch resolution, symbol resolver, queries, tool registry, limits | Knows nothing of Roslyn, MCP or the file system beyond paths |
| `CodeReach.Roslyn` | Load workspace, extract per-document facts, detect routes, auth, entry points, test projects | The only place a `SemanticModel` exists |
| `CodeReach.Mcp` | Process host: index lifecycle, snapshot store, freshness, watcher, MCP door, CLI door | No analysis logic; both doors loop over the registry |
| `samples/SampleShop` | Fictional layered ASP.NET solution used as the test fixture | Deliberately contains every trap in section 6 |

Tool set (11, coarse): `status`, `reindex`, `find_symbol`, `callers`, `callees`, `find_usages`,
`trace_to_entrypoints`, `routes`, `entrypoints`, `impact`, `architecture`.

## 6 Traps the fixture must contain

Each is a defect observed in other tools or a known Roslyn subtlety; each gets a named test.

- Controller → interface → service → interface → repository, plus a hosted service reaching the same repository.
- A generic base repository whose method is inherited by many repositories (false hotspot trap).
- Two nested constant classes with identically named members (wrong-symbol trap).
- A test project calling production methods (leak trap), detected by reference, with a non-test class named `*Tests`.
- Method names repeated in comments, strings and `nameof` (text-count trap).
- A migrations-style folder of generated noise, excluded by config.
- Overloads, virtual/override, explicit interface implementation, extension methods, lambdas, method groups,
  property accessors, constructors, `base.` calls.
- Controllers with route tokens, inherited routes, multiple verbs, a custom permission attribute fed by
  constants, `[AllowAnonymous]`, and an action with no auth at all.
- Minimal APIs: direct maps, nested groups, a group passed into an extension method, `RequireAuthorization`.

## 7 Phases

Estimates are focused working sessions. A STATUS line is appended in place when a phase ships.

| # | Scope | Est. | Exit gate (measurable) |
|---|---|---|---|
| 0 | Repo scaffold, three projects, walls, sample solution | 0.5 | `dotnet build` clean with warnings as errors; sample solution builds; the wall test fails on a planted crossing |
| 1 | Facts extraction, graph, dispatch, symbol resolver; `find_symbol`, `callers`, `callees`, `find_usages` | 1.5 | On the sample: caller and usage sets equal Roslyn `SymbolFinder` for every source method (oracle searches differently), with a non-vacuity floor; every trap in section 6 that concerns calls has a passing named test |
| 2 | Routes, auth, entry points, `trace_to_entrypoints`, `routes`, `entrypoints` | 1 | Sample route table matches a complete hand-audited list (no missing, no extra); trace from the sample repository method reaches exactly the expected actions, minimal API handlers and hosted service |
| 3 | `impact` from base ref or diff text | 0.5 | In a temp git repo copy of the sample: editing one repository method yields exactly the expected endpoints and job; a comment-only edit outside any symbol yields none |
| 4 | Tool registry, MCP door, CLI door, `status`, `architecture`, generated instructions | 0.5 | A test drives the built server over real stdio with `McpClient`: lists every registered tool, calls each, checks one refusal code |
| 5 | Snapshot store, freshness sweep, watcher, incremental re-extraction, `reindex` | 1.5 | Warm start answers from snapshot without loading Roslyn; a body edit and a signature edit on the sample are each reflected, and the incremental facts hash equals that of a from-scratch index |
| 6 | Packaging: tool package, `server.json`, README, local `dnx` run | 0.5 | Package installed from a local feed into a temp tool path runs against the sample over stdio |
| 7 | **[DECIDE]** publish to NuGet, MCP Registry entry, first GitHub release | — | Owner go-ahead only |
| 8 | Semantic ratchet (section 7a). Starts only after phases 1 to 5 are verified on the local large solution | 1.5 | On the sample: each counter kind equals a hand-audited count; growth exits 1, a counter that cannot run exits 2 and leaves the baseline untouched, `--check` never writes and reports a shrink as a finding |
| 9 | Built for feature work (section 7b): `type_hierarchy`, `di_registrations`, MCP prompts, task-oriented instructions | 1 | On the sample: registrations equal a hand-audited list and a text oracle's count; the planted captive dependency and unregistered service are reported and nothing else; prompts name only real tools and parameters and are served over stdio. On the large solution: registration count equals an independent count |
| 10 | Local UI (section 7c), after the owner has used 0.2.0 | 1.5 | `codereach ui` serves on 127.0.0.1 only, random port, one-time token; every screen's numbers equal the matching tool's answer |

STATUS 2026-11-10 (what verified each phase; the suite is `dotnet test --project tests/CodeReach.Tests`, xUnit v3 on the sample; CI runs it on Ubuntu, Windows and macOS):

| # | Status | Verified by |
|---|---|---|
| 0 | shipped | Solution build with warnings as errors, 0 warnings; `WallTests` (includes a check that the wall check itself reports a real reference) |
| 1 | shipped | `OracleTests`: usages equal Roslyn `SymbolFinder` per definition for every source member (192 locations compared), callers are a subset of the cascading finder; `CallGraphTests`, one test per trap. The oracle found one real gap (method groups passed as `Delegate`), now fixed |
| 2 | shipped | `RouteTests`: route table equals the audited list of 18; trace from the repository finds exactly 3 endpoints, 1 hosted service and 1 unrooted top |
| 3 | shipped | `ToolTests`: impact of a repository edit and of an edit outside any declaration, from diff text. The `git diff` path itself is unverified by a test |
| 4 | shipped | `WireTests`: the built server driven over stdio by the SDK client; lists all 11 tools, calls three, checks a refusal |
| 5 | shipped | `IncrementalTests`: after a body edit, a new member, a constant change, a file added and a file removed, the incremental facts hash equals a from-scratch index; a fresh snapshot is served with no workspace loaded. The file watcher and the idle release are unverified by a test |
| 6 | shipped | Packed to a local feed, installed with `dotnet tool install --tool-path` into a temp directory, CLI call and a raw JSON-RPC handshake (initialize, tools/list, tools/call) run against the sample. `dnx CodeReach.Mcp --yes -- version` run against nuget.org after release |
| 7 | shipped 0.1.0 | NuGet `CodeReach.Mcp` 0.1.0 (trusted publishing), GitHub release `v0.1.0`, MCP Registry `io.github.musadgn/codereach-mcp` 0.1.0. The registry step failed in CI (description over 100 characters, NuGet base URL must be the v3 index); both fixed and published by hand, and the release workflow now checks the length |
| 10 | built, unreleased | `UiServerTests` over real HTTP: strict CSP on the page; refused without the token, with a wrong token, from another origin and for another host (HttpListener's own prefix match refuses a rebound host with 404 before the server's 421 check); routes and trace data equal their text answers. Screens checked by headless Chrome screenshots of the sample (health report, trace, routes, DI, architecture) and of the large solution (every call under 0.5 s; 619 routes in 12 ms). Unverified: phone-width layout (headless Chrome will not render narrower than about 500 px), and binding 127.0.0.1 as a non-administrator on Windows (CI runners are administrators). Design: blueprint, command bar first, chosen by the owner |
| 9 | built, unreleased | `ContainerTests` (audited list of 16 registrations, text oracle, generic helper, constructor dependencies, captive and unregistered findings, a type/member name clash), `PromptTests`, `WireTests` (prompts over stdio). Suite: 58 tests. Large solution: 379 registrations, equal to the independent count (347 calls in the text, minus one helper line, plus its 33 call sites); 0 captive dependencies and 0 unregistered services, spot-checked on a job that uses `IServiceScopeFactory` |

**CI** (approved 2026-11-10): `ci.yml` builds with warnings as errors, tests on three operating systems,
checks formatting and smoke-tests the packed tool over stdio; `release.yml` publishes on a version tag after
the owner approves the `release` environment; Dependabot groups Roslyn, Microsoft.Extensions and xunit updates.

**Cost constraint (owner, 2026-11-10).** CodeReach is free and runs entirely on the user's machine: index in
memory and in the user's cache folder, any UI on a loopback port served by the tool itself. No hosted
service, account or paid infrastructure, ever.

### 7a Semantic ratchet (planned, not started)

Counters are graph queries, never regex: endpoints with no authorization (configurable attribute list);
layer-boundary violations under configurable layer rules; calls to a configured banned or deprecated API
outside allow-listed methods; direct use of a concrete type where an interface exists. A committed baseline
file holds each count and counts may only shrink. Growth is a finding (exit 1). A counter that could not
run is exit 2, never 0 or 1, and never rewrites the baseline. A shrink tightens the baseline locally;
`--check` never writes and treats a shrink as a finding so the tighter file gets committed. Exposed as
`codereach ratchet [--check]` and as MCP tools through the same registry. Counter kinds are a compiled-in
list of self-describing entries; nothing named in a config file is ever executed.

### 7b Built for feature work (phase 9)

What an agent needs while building a feature in a large .NET codebase that generic tools get wrong. Basic
navigation (text search, file outline, reading a file) is deliberately not built: every agent has it.

- `type_hierarchy`: bases, every derived or implementing type marked direct or indirect, members with what
  each implements or overrides, and the type's registrations.
- `di_registrations`: every registration call (lifetime methods, TryAdd, keyed, AddHostedService,
  AddHttpClient, AddDbContext; generic, typeof, factory and instance forms) and every constructor parameter.
  Services are keyed by constructed type, so `ChannelReader<A>` and `ChannelReader<B>` are different services,
  while an open generic registration satisfies any construction. A generic helper in the same project that
  registers its own type parameter is recorded at each of its call sites with the type argument substituted.
  Findings: captive dependencies (a singleton whose constructor takes a service registered only as scoped),
  interfaces and abstract types injected into container-built types with no registration, and services
  registered more than once (keyed registrations excluded). Not seen, and said so: assembly scanning, a
  library's own Add* methods, registrations outside the solution, helpers in another project.
  Known gap: a body-only edit to a generic registration helper does not re-extract the files that call it.
- Prompts: `review-diff`, `before-change`, `add-feature`, `audit-auth`, `explain-endpoint`, served through MCP
  prompts so every client gets them without installing anything.
- Instructions: a task-to-tool guide (before changing X call Y), generated, under the length cap.

### 7c Local UI (phase 10)

`codereach ui` opens a page on 127.0.0.1 with a random port and a one-time token in the URL, served by the
tool from files inside the package (no npm, no build step). Screens in order of value: routes with auth
filters, trace from a symbol to its entry points, impact of the working tree or a branch, architecture, and a
small graph of one trace. Every screen calls the same tool registry, so the UI cannot disagree with the tools.

Local validation (not in the repo, not in CI): after phases 1, 2 and 5 the server is run read-only against a
large private solution and compared with ground truths obtained by grep. Results are recorded in the
measurement appendix as numbers only.

## 8 Verification standards

- Every "works" names what verified it: unit suite on the sample, the `SymbolFinder` oracle, the stdio wire
  test, or the local large-solution run. Anything else is reported as unverified.
- Soundness is paired with completeness: route and entry-point tests compare against complete lists.
- Non-vacuity: every property test asserts a floor on the number of positives it examined.
- Cross-tool invariants are tests: directly bound callers ⊆ usages; first trace hop ⊆ callers; route handlers ⊆ entry points;
  impact endpoints ⊆ routes; hidden-row counts plus shown-row counts equal the unfiltered total.
- Determinism: indexing the sample twice yields the same canonical facts hash; document order must not matter.
- Blast radius: the facts model and snapshot format are shared by every component. A change to either bumps
  the snapshot format version, and old snapshots are discarded, never migrated.

## 9 Measurement appendix

| What | Number | How measured |
|---|---|---|
| Large solution: open | 2.9 s | Throwaway spike, `MSBuildWorkspace.OpenSolutionAsync`, 5 projects, 3,372 documents, 0 load failures |
| Large solution: compile all projects with diagnostics | 54.9 s wall, 176.8 s CPU | Same spike, `GetCompilationAsync` + `GetDiagnostics`, 0 errors |
| Large solution: memory | 2.18 GB peak RSS, 1.67 GB working set at end | `/usr/bin/time -l` on the spike |
| Analysed repository after every run | 0 changed files | `git status --porcelain` before and after |
| Large solution: cold index (open + extract 2,807 documents, 548 excluded) | 38.6 s wall, 142 s CPU, 1.50 GB peak RSS | `/usr/bin/time -l codereach call status`, Release build |
| Large solution: index content | 29,691 symbols, 489,860 references, 619 endpoints | `status` |
| Large solution: snapshot | 18.9 MB | file size in the cache directory |
| Large solution: warm start, one trace query, whole process | 0.92 s wall, 256 MB peak RSS, snapshot load 352 ms, workspace not loaded | `/usr/bin/time -l codereach call trace_to_entrypoints` |
| Sample: cold 2.5 s, warm 11 ms, body edit refresh 2.1 s with 1 document re-extracted | | `status` and `reindex` on a copy of the sample |

Ground truths on the large solution (grep by hand against the tool, differences read in the source):

| Question | Tool | Grep | Explanation of the difference |
|---|---|---|---|
| Trace from a repository delete method | 1 hosted service, 4-step chain through two interfaces | same chain | none |
| Callers of a cache invalidation method | 17 call sites in 6 types | "18 in 7" | grep counted the implementation's own declaration |
| Usages of one framework extension method outside migrations | 137 in 40 files | 132 in 41 files | grep counted 3 comment-only files and 1 commented line, and missed the overload that takes arguments (9 calls) |
| Controller endpoints | 618 | 612 verb attributes | 6 actions answer on more than one route or verb |

Not measured: incremental latency and idle memory on the large solution (it must not be modified).

## 10 Risks and mitigations

| Risk | Mitigation |
|---|---|
| Cold index of a large solution takes about a minute and about 2 GB | Persisted snapshot serves instantly; cold or stale state is stated in every answer; the workspace is released after an idle period |
| Class-hierarchy dispatch over-approximates (an interface with several implementations) | Receiver static type narrows candidates; each hop is labelled with the member it crossed and how many implementations exist. Narrowing by DI registrations is deferred |
| Reach that the compiler cannot see: reflection, DI-invoked constructors, message buses, source generators | Trace reports "unrooted" tops (callers with no caller that are not entry points) instead of dropping them; entry-point rules are configurable |
| Minimal API route prefixes that flow through parameters | Resolved across one call boundary via recorded group-argument facts; anything unresolved is marked unresolved in the template, never guessed |
| File watcher misses events (macOS, network drives) | Freshness never depends on it: each query runs a throttled stat sweep; the watcher only makes refresh proactive |
| Build host and tool packaging interplay | Phase 6 gate runs the packed tool, not the build output |
| Solutions that fail to load fully (missing SDK, unrestored packages) | `status` lists workspace failures and per-project error counts; a project with binding errors is flagged in every answer that touches it |

## 11 Cut / non-goals

- General-purpose symbol browsing, refactoring, diagnostics, code generation.
- Languages other than C# (VB and F# projects are listed and skipped).
- Runtime analysis, DI container resolution, reflection modelling (deferred, see risks).
- A shared daemon across client sessions: each stdio process owns its workspace; they share only the snapshot.
- HTTP transport, authentication, remote hosting.
- Razor Pages, MVC views, gRPC and SignalR as first-class route kinds in v1: reachable through configurable
  entry-point rules, not listed as routes.

## 12 Open

- **[DECIDE]** Package id `CodeReach.Mcp`, registry name `io.github.musadgn/codereach-mcp`, version `0.1.0`.
- **[DECIDE]** Publish, registry submission, GitHub release (phase 7).
- **[DECIDE]** CI plan (GitHub Actions matrix, release workflow, dependabot): on hold, see section 7.
- OPEN: default idle period before the workspace is released (now 20 minutes, `Limits.IdleUnloadMinutes`).
  Evidence that settles it: reload cost against retained memory on a large solution that may be edited.
- OPEN: `dnx CodeReach.Mcp` end to end. Not run, because it would seed the NuGet cache with an unpublished
  0.1.0. Settled by one run against nuget.org's integration feed or after the first publish.
