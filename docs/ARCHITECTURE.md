# CodeReach.Mcp — Architecture

Dated 2026-11-10. This describes the design the [plan](PLAN.md) builds. Where the code and this file
disagree, the code is right and this file is a bug.

## 1 Shape

```
            MCP client (stdio)                 shell
                   │                             │
            ┌──────▼──────┐               ┌──────▼──────┐
            │  MCP door   │               │  CLI door   │      CodeReach.Mcp (process host)
            └──────┬──────┘               └──────┬──────┘
                   └──────────┬──────────────────┘
                       tool registry  ── one record per tool: name, description, schema, handler
                              │
                       query engine   ── callers, callees, usages, trace, routes, impact, overview
                              │                                           CodeReach.Core (pure)
                         CodeGraph    ── immutable snapshot, generation-stamped
                              ▲
                     graph builder    ── joins facts, resolves dispatch, routes, entry points
                              ▲
                     document facts   ── persisted: the only thing the indexer produces
  ════════════════════════════╪══════════════════════════════════════════════════════════
                       Roslyn indexer ── MSBuildWorkspace, semantic model      CodeReach.Roslyn
```

Dependencies point one way: `Mcp → Roslyn → Core`, and `Mcp → Core`. Core references no Roslyn package,
so the wall is enforced by the compiler.

## 2 The facts model

The indexer's only output is **facts**: plain records with string identities. Everything else is derived
from facts by pure code. This is what makes the index persistable, incrementally updatable and testable
without a compiler.

**Symbol identity.** A symbol's id is its Roslyn documentation comment id on the original definition. Source
symbols also carry their assembly, because documentation ids alone collide across projects (two top-level
`Program`s, linked files): `M:[Shop.Application]Shop.Orders.OrderService.CancelAsync(System.Guid)`.
File-local types add their file. Ids are stable across runs and unambiguous across overloads. Code inside lambdas, local functions and accessors belongs to the
enclosing member. The one exception is a lambda passed as a minimal API handler: it gets its own node,
because it is an entry point and nothing calls it.

**Facts per document**

| Fact | Content |
|---|---|
| Declaration | Symbol id, kind, display name, containing type, accessibility, modifiers, file and line span |
| Reference | From-symbol id, to-symbol id, kind, file, line, and for calls the static type of the receiver |
| Endpoint mapping | Minimal API: handler id, verb, template, where the route prefix comes from, auth calls in the chain |
| Group argument | A route-builder value passed to a method parameter, with the prefix known at that point |

Reference kinds: `invoke`, `construct`, `methodGroup`, `read`, `write`, `nameof`, `typeRef`, `attribute`,
`baseCall`. A reference is recorded for every bound name in the document: source symbols and metadata
symbols alike, so usages of framework members can be counted. Namespaces, locals, parameters, type
parameters and predefined keywords are not recorded.

**Facts per type** (attached to the declaring document): constructed base types and interfaces, the
implementation chosen for every interface member, the member each override replaces, attributes relevant
to routing and authorization with their evaluated constructor arguments and their source text.

**Facts per project:** name, path, project references, test-project verdict and its evidence, language,
binding error count.

## 3 Dispatch

A call site is stored as it was bound: target `m` and receiver static type `R`. The graph builder then
computes the set of definitions that call can reach at run time.

- Static, non-virtual, `base.` and constructor calls reach exactly `m`.
- Interface member `m`: for every concrete source type `C` assignable to `R`, the implementation `C` chose
  for `m`.
- Virtual or abstract `m`: `m` itself unless abstract, every override declared in a subtype of `R`, and the
  nearest definition `R` inherits.

Assignability compares constructed type keys (`IRepository<Order>`); when either side contains type
parameters, or the receiver's interface is variant, it falls back to original definitions. This is class-hierarchy analysis narrowed by receiver
type: sound for code the compiler can see, and an over-approximation when one interface has several
implementations. Every hop that crosses dispatch carries a label naming the member crossed and the number
of candidates, so an over-approximation is visible rather than silent.

A query can name an inherited member through a concrete type (`OrderRepository.GetByIdAsync` where the
method lives in `Repository<T>`). It then keeps only call sites whose receiver can be that type, which is
what prevents a shared generic base from reporting every repository's callers.

**Caller relation (the one definition every tool uses).** Site `s` calls definition `D` when `D` is the
bound target of `s`, or `D` is in the reachable set of `s`, or `D` is an interface member and the bound
target of `s` implements it. A call bound to an override is not a call of the member it overrides.

**Known gaps.** Not modelled, and therefore missing from every answer: implicit user-defined conversions,
the pattern-based calls behind `foreach`, `using`, `await` and deconstruction, collection-initializer `Add`
calls, reflection, and constructors invoked by a dependency injection container.

## 4 Routes, authorization, entry points

**Controllers.** A controller is a public, concrete class that derives from `ControllerBase`, or carries
`[ApiController]` or `[Controller]`, or follows the `*Controller` naming convention, and is not marked
`[NonController]`. Actions are its public instance methods not marked `[NonAction]`. Verbs and templates
come from attribute data (so constants are already evaluated), are inherited from base controllers,
combined as the framework combines them, and have `[controller]`, `[action]` and `[area]` tokens replaced.

**Minimal APIs.** Calls to the framework's `Map*` extension methods. The route prefix is resolved from the
fluent chain, from local variables, and across one method boundary through group-argument facts. A prefix
that cannot be resolved is written as unresolved in the template.

**Authorization.** For each endpoint, in order: attributes on the action, the controller and its bases;
for minimal APIs, `RequireAuthorization` / `AllowAnonymous` calls on the endpoint and its groups. An
attribute counts as authorization when it implements the framework's authorization contracts, or when its
name is listed in configuration. Arguments are shown as source text and evaluated value together
(`PermissionKeys.Orders.Delete = "orders.delete"`). An endpoint with none is reported as `auth: none`,
and anything that could still protect it globally (a fallback policy, a global authorize filter,
`MapControllers().RequireAuthorization()`) is detected and reported beside it. Attribute lookup follows the
override chain of the action, as the framework does.

**Entry points** are graph nodes with a kind: controller action, minimal API handler, hosted service
(`ExecuteAsync`, `StartAsync`, `StopAsync` of types implementing `IHostedService`), program main, and any
configured root rule (a method matched by implemented interface member, base type or attribute).

**Trace.** Breadth-first upward over the caller relation from the queried symbol. A path ends at the first
entry point it meets. A path that ends at a symbol with no callers that is not an entry point is reported
as **unrooted**, so reach lost to reflection or dependency injection is shown rather than dropped.

## 5 Index lifecycle

```
 start ──► snapshot on disk? ──no──► BUILDING ──► FRESH
                │ yes                                ▲  │ file change (sweep or watcher)
                ▼                                    │  ▼
          stamps match? ──yes──► FRESH (Roslyn never loaded)     STALE ──► re-extract ──► FRESH
                │ no
                ▼
              STALE (answers stamped; refresh starts)
```

- **Snapshot.** One versioned binary file per solution in the OS cache directory: all facts plus a stamp
  (size, modified time, content hash) for every source file and build input (project files, imported props
  and targets, the assets file, `global.json`). Stamps are taken before Roslyn reads the files, so a
  concurrent edit can only make the index look stale, never falsely fresh. The snapshot is also keyed by
  the configuration hash and the extractor version. Written to a temporary file and
  renamed. A version mismatch discards it.
- **Freshness** does not depend on the watcher. Each query triggers a stat sweep, throttled to once every
  two seconds; a changed stamp is confirmed by content hash so a branch switch that restores identical
  content stays fresh. The watcher exists only to start refreshes early.
- **Incremental re-extraction.** The declaration shape of a document is every token outside method bodies,
  accessor bodies and top-level statements: initializers, attributes, usings, default values and enum
  values are all shape. A change that leaves the shape untouched re-extracts that document. A change to declarations, or an added or removed file, re-extracts the
  project and every project that depends on it. A project or solution file change reloads the workspace.
- **Queries during refresh** wait briefly for the fresh graph. If it is not ready they answer from the
  previous graph with a `STALE` first line naming what changed. With no previous graph they refuse with
  `INDEX_BUILDING` and progress.
- **Memory.** A fresh snapshot is served without loading Roslyn. The workspace is loaded when something
  changed and released after an idle period.

## 6 Tool surface

Eleven coarse tools, each one record in the registry. Both doors loop over the registry, so MCP and CLI
cannot drift.

| Tool | Answers |
|---|---|
| `status` | Solution, projects, test verdicts, counts, index state and age, config in effect, load failures |
| `reindex` | Forces a refresh or a full rebuild; returns before/after counts |
| `find_symbol` | Declarations by name pattern and kind |
| `callers` / `callees` | The caller relation, to a chosen depth, grouped by type |
| `find_usages` | Every reference to any symbol, by kind |
| `trace_to_entrypoints` | Paths from a symbol up to endpoints, jobs and other roots, plus unrooted tops |
| `routes` | Endpoints with verb, template, handler and authorization; filterable |
| `entrypoints` | All roots by kind |
| `impact` | A base ref or diff text → changed symbols → affected endpoints and jobs |
| `architecture` | Projects, dependency edges weighted by real references, layers, hotspots |

**Addressing.** A symbol argument is a documentation id, a qualified-name suffix
(`OrderService.CancelAsync`), or a `file:line` location. Overloads of one member and the members of one
dispatch family are merged and the merge is stated. Anything else that matches more than one symbol is
refused with the candidates listed.

**Answers** are compact text with a measured header line (totals, and how many rows the default filters
hid), locations as `path:line`, and a named drill-down wherever output was truncated. Every answer carries
the index generation it was computed from.

**Completion state.** Every answer is `complete`, `partial` or `failed`. A project that failed to load, or a
document with binding errors, makes every answer that could have touched it `partial` and names it. A count
of zero is printed only for a complete analysis. The CLI door maps this to exit codes: 0 clean, 1 finding,
2 could not run.

**Refusals** use a closed list of codes: `INDEX_BUILDING`, `SOLUTION_NOT_FOUND`, `SOLUTION_LOAD_FAILED`,
`SYMBOL_NOT_FOUND`, `AMBIGUOUS_SYMBOL`, `VALIDATION_FAILED`, `GIT_FAILED`, `UNKNOWN_TOOL`, `INTERNAL`.

**Limits** live in one module: row limits, depth caps, path caps, input sizes. Tool inputs are validated
against them before any work; `impact` passes a validated ref to `git` as an argument vector, never
through a shell.

**Instructions** sent at connect time are generated from the live configuration and index summary, capped
in length, never hand-written.

## 7 Configuration

Optional `codereach.json` beside the solution, or `--config`. Authored by the user; the tool never writes
it. Fields: `solution`, `exclude` (globs such as `**/Migrations/**`), `testProjects`, `authAttributes`,
`authEndpointMethods`, `entryPoints` (root rules). Generated code (`<auto-generated>` headers, `*.g.cs`,
`obj/`) is excluded without configuration.

## 8 Testing

- **Fixture:** the fictional `samples/SampleShop` solution, indexed once per test run.
- **Oracle:** Roslyn's own `SymbolFinder` answers the caller and usage questions by a different search; the
  index must agree with it for every source method, with a floor on how many it compared.
- **Completeness:** routes and entry points are compared with complete audited lists.
- **Invariants:** the cross-tool relations in the plan, checked on every symbol of the fixture.
- **Determinism:** two indexing runs, and a run after incremental updates, must produce identical facts.
- **Wire:** a test launches the built server and drives it with the SDK's stdio client.
