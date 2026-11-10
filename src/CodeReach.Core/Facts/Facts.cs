namespace CodeReach.Core.Facts;

public enum SymKind : byte { Type, Method, Constructor, Property, Field, Event, Handler }

public enum RefKind : byte { Invoke, Construct, MethodGroup, Read, Write, NameOf, TypeRef, Attribute, BaseCall }

public enum Dispatch : byte { None, Interface, Virtual }

public enum AuthKind : byte { Authorize, AllowAnonymous, Custom }

[Flags]
public enum SymFlags : ushort
{
    None = 0,
    Static = 1,
    Abstract = 2,
    Virtual = 4,
    Override = 8,
    Public = 16,
    External = 32,
}

/// <summary>A declared symbol. <see cref="Id"/> is the documentation comment id of the original definition.</summary>
public sealed record SymbolFact(
    string Id, SymKind Kind, string Name, string QName, string Display, string? TypeId, int Line, int StartLine, int EndLine, SymFlags Flags);

/// <summary>
/// One bound name. <see cref="Receiver"/> is the type key of the static receiver type for instance member
/// access, or null when it is the member's own containing type.
/// </summary>
public sealed record ReferenceFact(
    string From, string To, RefKind Kind, int Line, Dispatch Dispatch, bool TargetAbstract, string? Receiver);

public sealed record ImplFact(string InterfaceMember, string Impl);

/// <summary><see cref="Bases"/> holds the type keys of every base class and interface, constructed.</summary>
public sealed record TypeFact(string Id, bool IsInterface, bool IsAbstract, bool IsGeneric, string[] Bases, ImplFact[] Impls);

public sealed record OverrideFact(string Member, string Overridden);

public sealed record AuthFact(AuthKind Kind, string Name, string Args, string Scope);

/// <summary>A route prefix that arrives through a parameter of <see cref="Method"/>.</summary>
public sealed record PrefixParam(string Method, int Ordinal);

public sealed record EndpointFact(
    string Handler, string Verb, string Template, bool Minimal, int Line, AuthFact[] Auth, PrefixParam? Prefix);

/// <summary>A route builder passed as argument <see cref="Ordinal"/> of <see cref="Callee"/>.</summary>
public sealed record GroupArgFact(string Callee, int Ordinal, string Prefix, AuthFact[] Auth, PrefixParam? Via);

public sealed record EntryPointFact(string Symbol, string Kind, string Label);

/// <summary>
/// One dependency injection registration call. <see cref="Service"/> and <see cref="Implementation"/> are
/// type definition ids; <see cref="Implementation"/> is null when a factory's result type is unknown.
/// <see cref="From"/> is the symbol whose code makes the call.
/// </summary>
public sealed record ServiceFact(
    string Service, string? Implementation, string Lifetime, string Method, int Line, string From, string Detail);

/// <summary>
/// A constructor parameter: what the type asks the container for. <see cref="Type"/> is a type definition id;
/// <see cref="Many"/> marks <c>IEnumerable&lt;T&gt;</c>, where <see cref="Type"/> is the element type.
/// </summary>
public sealed record DependencyFact(string Constructor, int Ordinal, string Type, bool Many);

public sealed record DocumentFacts(
    string Path,
    string Project,
    string ShapeHash,
    int Errors,
    SymbolFact[] Symbols,
    ReferenceFact[] References,
    TypeFact[] Types,
    OverrideFact[] Overrides,
    EndpointFact[] Endpoints,
    GroupArgFact[] GroupArgs,
    EntryPointFact[] EntryPoints,
    ServiceFact[] Services,
    DependencyFact[] Dependencies);

public sealed record ProjectFact(
    string Name, string Path, bool IsTest, string TestEvidence, string[] References, int Documents, int Skipped, string? LoadFailure);

/// <summary>Everything the indexer produces. Paths are relative to <see cref="Root"/> with forward slashes.</summary>
public sealed record IndexFacts(
    string SolutionPath, string Root, ProjectFact[] Projects, DocumentFacts[] Documents, string[] LoadWarnings);
