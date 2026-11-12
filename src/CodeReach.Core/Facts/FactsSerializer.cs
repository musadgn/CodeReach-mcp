using System.Text;

namespace CodeReach.Core.Facts;

/// <summary>
/// Compact binary form of <see cref="IndexFacts"/>. Strings are written once and referred to by number, which
/// is what keeps half a million references small and quick to load.
/// </summary>
public static class FactsSerializer
{
    /// <summary>Bumped on any change to the facts model or this layout. Older files are discarded, never migrated.</summary>
    public const int FormatVersion = 3;

    private const uint Magic = 0x43524658; // "CRFX"

    public static void Write(Stream stream, IndexFacts facts)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        var strings = new Dictionary<string, int>(StringComparer.Ordinal);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        Text(facts.SolutionPath);
        Text(facts.Root);
        List(facts.Projects, p =>
        {
            Text(p.Name);
            Text(p.Path);
            writer.Write(p.IsTest);
            Text(p.TestEvidence);
            List(p.References, Text);
            writer.Write(p.Documents);
            writer.Write(p.Skipped);
            Optional(p.LoadFailure);
            Text(p.SkipDetail);
        });
        List(facts.Documents, d =>
        {
            Text(d.Path);
            Text(d.Project);
            Text(d.ShapeHash);
            writer.Write(d.Errors);
            List(d.Symbols, s =>
            {
                Text(s.Id);
                writer.Write((byte)s.Kind);
                Text(s.Name);
                Text(s.QName);
                Text(s.Display);
                Optional(s.TypeId);
                writer.Write(s.Line);
                writer.Write(s.StartLine);
                writer.Write(s.EndLine);
                writer.Write((ushort)s.Flags);
            });
            List(d.References, r =>
            {
                Text(r.From);
                Text(r.To);
                writer.Write((byte)r.Kind);
                writer.Write(r.Line);
                writer.Write((byte)r.Dispatch);
                writer.Write(r.TargetAbstract);
                Optional(r.Receiver);
            });
            List(d.Types, t =>
            {
                Text(t.Id);
                writer.Write(t.IsInterface);
                writer.Write(t.IsAbstract);
                writer.Write(t.IsGeneric);
                List(t.Bases, Text);
                List(t.Impls, i =>
                {
                    Text(i.InterfaceMember);
                    Text(i.Impl);
                });
            });
            List(d.Overrides, o =>
            {
                Text(o.Member);
                Text(o.Overridden);
            });
            List(d.Endpoints, e =>
            {
                Text(e.Handler);
                Text(e.Verb);
                Text(e.Template);
                writer.Write(e.Minimal);
                writer.Write(e.Line);
                List(e.Auth, Auth);
                Prefix(e.Prefix);
            });
            List(d.GroupArgs, g =>
            {
                Text(g.Callee);
                writer.Write(g.Ordinal);
                Text(g.Prefix);
                List(g.Auth, Auth);
                Prefix(g.Via);
            });
            List(d.EntryPoints, e =>
            {
                Text(e.Symbol);
                Text(e.Kind);
                Text(e.Label);
            });
            List(d.Services, v =>
            {
                Text(v.Service);
                Optional(v.Implementation);
                Text(v.Lifetime);
                Text(v.Method);
                writer.Write(v.Line);
                Text(v.From);
                Text(v.Detail);
            });
            List(d.Dependencies, v =>
            {
                Text(v.Constructor);
                writer.Write(v.Ordinal);
                Text(v.Type);
                writer.Write(v.Many);
            });
        });
        List(facts.LoadWarnings, Text);

        void Text(string value)
        {
            if (strings.TryGetValue(value, out var index))
            {
                writer.Write7BitEncodedInt(index);
                return;
            }

            writer.Write7BitEncodedInt(strings.Count);
            strings[value] = strings.Count;
            writer.Write(value);
        }

        void Optional(string? value)
        {
            writer.Write(value is not null);
            if (value is not null)
            {
                Text(value);
            }
        }

        void List<T>(IReadOnlyCollection<T> items, Action<T> write)
        {
            writer.Write7BitEncodedInt(items.Count);
            foreach (var item in items)
            {
                write(item);
            }
        }

        void Auth(AuthFact auth)
        {
            writer.Write((byte)auth.Kind);
            Text(auth.Name);
            Text(auth.Args);
            Text(auth.Scope);
        }

        void Prefix(PrefixParam? prefix)
        {
            writer.Write(prefix is not null);
            if (prefix is not null)
            {
                Text(prefix.Method);
                writer.Write(prefix.Ordinal);
            }
        }
    }

    /// <summary>Reads facts, or returns null when the stream is not a snapshot of this format version.</summary>
    public static IndexFacts? Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != Magic || reader.ReadInt32() != FormatVersion)
        {
            return null;
        }

        var strings = new List<string>();
        var solution = Text();
        var root = Text();
        var projects = List(() => new ProjectFact(Text(), Text(), reader.ReadBoolean(), Text(), List(Text), reader.ReadInt32(), reader.ReadInt32(), Optional(), Text()));
        var documents = List(() => new DocumentFacts(
            Text(), Text(), Text(), reader.ReadInt32(),
            List(() => new SymbolFact(
                Text(), (SymKind)reader.ReadByte(), Text(), Text(), Text(), Optional(), reader.ReadInt32(), reader.ReadInt32(),
                reader.ReadInt32(), (SymFlags)reader.ReadUInt16())),
            List(() => new ReferenceFact(
                Text(), Text(), (RefKind)reader.ReadByte(), reader.ReadInt32(), (Dispatch)reader.ReadByte(), reader.ReadBoolean(), Optional())),
            List(() => new TypeFact(
                Text(), reader.ReadBoolean(), reader.ReadBoolean(), reader.ReadBoolean(), List(Text), List(() => new ImplFact(Text(), Text())))),
            List(() => new OverrideFact(Text(), Text())),
            List(() => new EndpointFact(Text(), Text(), Text(), reader.ReadBoolean(), reader.ReadInt32(), List(Auth), Prefix())),
            List(() => new GroupArgFact(Text(), reader.ReadInt32(), Text(), List(Auth), Prefix())),
            List(() => new EntryPointFact(Text(), Text(), Text())),
            List(() => new ServiceFact(Text(), Optional(), Text(), Text(), reader.ReadInt32(), Text(), Text())),
            List(() => new DependencyFact(Text(), reader.ReadInt32(), Text(), reader.ReadBoolean()))));
        return new IndexFacts(solution, root, projects, documents, List(Text));

        string Text()
        {
            var index = reader.Read7BitEncodedInt();
            if (index == strings.Count)
            {
                strings.Add(reader.ReadString());
            }

            return strings[index];
        }

        string? Optional() => reader.ReadBoolean() ? Text() : null;

        T[] List<T>(Func<T> read)
        {
            var items = new T[reader.Read7BitEncodedInt()];
            for (var i = 0; i < items.Length; i++)
            {
                items[i] = read();
            }

            return items;
        }

        AuthFact Auth() => new((AuthKind)reader.ReadByte(), Text(), Text(), Text());

        PrefixParam? Prefix() => reader.ReadBoolean() ? new PrefixParam(Text(), reader.ReadInt32()) : null;
    }
}
