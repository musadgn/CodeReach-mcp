# CodeReach

<!-- mcp-name: io.github.musadgn/codereach-mcp -->

**Which endpoints and jobs reach this code, and what does your diff affect.**

CodeReach is an MCP server for ASP.NET Core and C# solutions. It loads your solution with Roslyn, builds a
call graph from the compiler's own semantic model, keeps it on disk, and answers questions an agent (or
you, from a shell) would otherwise answer with grep and guesswork:

- What calls this method, **through its interface**, all the way up to a controller action, a minimal API
  handler or a background service?
- Which endpoints exist, with their full route and **authorization**, including your own permission
  attributes?
- I changed these lines. **Which endpoints and jobs does that touch?**

Status: 0.1.0, not yet published to NuGet. The commands below that use `dnx` work once it is.

## Why not grep, or a tree-sitter index

Text and syntax tools cannot see what the compiler sees. In a layered application
(controller, service interface, service, repository interface, repository) they stop at the first
interface, count comments and strings, attach calls to the wrong overload, and report a shared generic
base method as every repository's hotspot. CodeReach binds every name with Roslyn, so:

- a call through `IOrderService` reaches `OrderService`, and says so;
- `PermissionKeys.Orders.Delete` and `PermissionKeys.Products.Delete` are different symbols;
- `OrderRepository.GetByIdAsync` counts only calls an `OrderRepository` can receive, even though the
  method lives in `Repository<T>`;
- tests are excluded because their **project** references a test framework, not because of a name;
- every tool reads one graph, so a caller count, a usage count and a trace never disagree.

## Run it

Requires the .NET 10 SDK, and a solution that builds.

```json
{
  "mcpServers": {
    "codereach": {
      "command": "dnx",
      "args": ["CodeReach.Mcp", "--yes"]
    }
  }
}
```

The server indexes the single `.slnx`, `.sln` or `.csproj` in its working directory. To point it
elsewhere, append `"--", "--solution", "path/to/App.slnx"` to `args`.

From a clone of this repository:

```bash
dotnet run --project src/CodeReach.Mcp -- call trace_to_entrypoints '{"symbol":"OrderRepository.HardDeleteAsync"}' --solution samples/SampleShop
```

```text
entry points that reach OrderRepository.HardDeleteAsync(Guid)
4 entry points (3 endpoint, 1 hosted-service), 1 unrooted tops; 10 symbols visited. Hidden: 2 call sites in test projects (include_tests=true shows them).

[endpoint] DELETE /api/Orders/{id:guid}   auth: HasPermission(PermissionKeys.Orders.Delete = "orders.delete") [action]; Authorize [base ShopControllerBase]
  OrdersController.Delete(Guid)  src/Shop.Api/Controllers/OrdersController.cs:17
  -> OrderPurger.PurgeAsync(Guid)  called at src/Shop.Api/Controllers/OrdersController.cs:19  (bound to IOrderPurger.PurgeAsync(Guid))
  -> OrderRepository.HardDeleteAsync(Guid)  called at src/Shop.Application/Orders/OrderService.cs:47  (bound to IOrderRepository.HardDeleteAsync(Guid))

[hosted-service] OrderPurgeService
  OrderPurgeService.ExecuteAsync(CancellationToken)  src/Shop.Infrastructure/Jobs/OrderPurgeService.cs:9
  -> OrderPurgeService.PurgeExpiredAsync()  called at src/Shop.Infrastructure/Jobs/OrderPurgeService.cs:13
  ...
```

CodeReach is a plain MCP server. It installs no hooks, skills or agent profiles, never edits your client
configuration, and writes nothing into the solution it analyses. Its index lives in your user cache
directory.

## Tools

| Tool | Answers |
|---|---|
| `trace_to_entrypoints` | Every endpoint, hosted service and other root that reaches a symbol, with one call path each |
| `impact` | A git diff (uncommitted changes, a base ref, or diff text) mapped to the endpoints and jobs it affects |
| `routes` | Endpoints with verb, full route template, handler and authorization; `auth=none` lists the unprotected ones |
| `callers` / `callees` | The call relation with interface and virtual dispatch resolved, to a chosen depth |
| `find_usages` | Every reference bound to a symbol, by kind (call, read, write, `nameof`, attribute); works for framework members |
| `find_symbol` | Declarations by name |
| `entrypoints` | All roots a trace can stop at |
| `architecture` | Projects, how much each really uses the others, most-called methods, most-implemented interfaces |
| `status` / `reindex` | Index state and freshness; force a refresh |

A symbol is named as `Type.Member`, as `path/File.cs:line`, or with an id from an earlier answer. An
ambiguous name is refused with the candidates listed. Every tool is also a shell command:
`codereach call <tool> '{json}'`, exiting 0 when answered, 1 on a finding, 2 when it could not run.

## What every answer tells you

- **How much the default filters hid.** "Hidden: 7 call sites in test projects."
- **Whether the analysis was complete.** The last line is `analysis: complete`, or `PARTIAL` with the
  projects or documents that failed to load or bind. A zero is only printed for a complete analysis.
- **Whether the index is current.** A first line starting with `STALE` means files changed and the
  refresh is still running.
- **What it could not see.** A call chain that ends where nothing calls it is reported as *unrooted*
  (dependency injection, reflection, or dead code), not silently dropped.

## Staying fresh

The first index of a large solution takes about as long as a build. After that the index is a file:
a server that starts on unchanged sources answers in milliseconds and never loads Roslyn. When files
change, a body-only edit re-extracts that one document; a change to declarations re-extracts the project
and the projects that depend on it. Freshness is checked on every call and does not rely on file
watching.

## Configuration

Optional `codereach.json` next to the solution:

```jsonc
{
  // Attributes that mean authorization but implement no framework contract.
  "authAttributes": ["HasPermission"],
  // Fluent endpoint methods that mean authorization, besides RequireAuthorization.
  "authEndpointMethods": ["RequirePermission"],
  // Source that should not be indexed.
  "exclude": ["**/Migrations/**"],
  // Extra entry points: a method that implements an interface member, derives from a base type, or carries an attribute.
  "entryPoints": [
    { "kind": "command-handler", "implements": "MyApp.ICommandHandler`1", "method": "HandleAsync" }
  ]
}
```

Attributes that implement the framework's own authorization contracts are recognised without
configuration, and so are MediatR handlers, MassTransit consumers, Quartz jobs, middleware, SignalR hubs
and Azure Functions.

## What it does not model

- Implicit user-defined conversions, and the calls behind `foreach`, `using`, `await`, deconstruction and
  collection initializers.
- Reflection, and constructors invoked by a dependency injection container.
- Calls through an interface with several implementations are reported for every implementation the
  receiver's static type allows, and each hop says how many candidates there were.
- Global authorization (a fallback policy, a global authorize filter) is detected and reported next to
  the route list, not applied to individual endpoints.
- Conventional (non-attribute) MVC routes are listed as `(conventional)`.
- C# only. Razor Pages, gRPC and SignalR are reachable through entry-point rules but are not listed as
  routes.

## Development

```bash
dotnet build
dotnet test --project tests/CodeReach.Tests
```

The tests index `samples/SampleShop`, a small fictional layered application that contains every trap
listed above, and compare the result with Roslyn's own reference finder, with complete hand-audited route
tables, and with a from-scratch index after each kind of incremental edit. Design and plan are in
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and [docs/PLAN.md](docs/PLAN.md).

## License

MIT
