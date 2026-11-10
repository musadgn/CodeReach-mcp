namespace CodeReach.Core.Facts;

/// <summary>Pure string operations on documentation ids and type keys.</summary>
public static class Ids
{
    public const string HandlerPrefix = "L:";

    /// <summary>The original-definition id of a type key (<c>T:Ns.IRepo`1&lt;T:Ns.Order&gt;</c> gives <c>T:Ns.IRepo`1</c>).</summary>
    public static string DefOf(string typeKey)
    {
        var open = typeKey.IndexOf('<', StringComparison.Ordinal);
        return open < 0 ? typeKey.TrimEnd(VariantMark) : typeKey[..open];
    }

    /// <summary>Appended to a receiver key whose interface has variant type parameters.</summary>
    public const char VariantMark = '~';

    public static bool IsVariant(string typeKey) => typeKey.EndsWith(VariantMark);

    /// <summary>True when the key mentions a type parameter, so only its definition can be compared.</summary>
    public static bool IsOpen(string typeKey) => typeKey.Contains('!', StringComparison.Ordinal);

    /// <summary>The id of the type that declares a member id, or null for types and handlers.</summary>
    public static string? ContainingTypeOf(string memberId)
    {
        if (memberId.Length < 3 || memberId[1] != ':' || memberId[0] is 'T' or 'L')
        {
            return null;
        }

        var head = HeadOf(memberId);
        var dot = head.LastIndexOf('.');
        return dot < 0 ? null : string.Concat("T:", AssemblyTag(memberId), head.AsSpan(0, dot));
    }

    public static SymKind KindOf(string id) => id[0] switch
    {
        'T' => SymKind.Type,
        'P' => SymKind.Property,
        'F' => SymKind.Field,
        'E' => SymKind.Event,
        'L' => SymKind.Handler,
        _ => id.Contains("#ctor", StringComparison.Ordinal) ? SymKind.Constructor : SymKind.Method,
    };

    /// <summary>Qualified name without parameters or arity: <c>Ns.Type.Member</c>.</summary>
    public static string QNameOf(string id)
    {
        var head = HeadOf(id);
        var builder = new System.Text.StringBuilder(head.Length);
        for (var i = 0; i < head.Length; i++)
        {
            var c = head[i];
            if (c == '`')
            {
                while (i + 1 < head.Length && (head[i + 1] == '`' || char.IsDigit(head[i + 1])))
                {
                    i++;
                }

                continue;
            }

            builder.Append(c == '#' && !head.AsSpan(i).StartsWith("#ctor", StringComparison.Ordinal) && !head.AsSpan(i).StartsWith("#cctor", StringComparison.Ordinal) ? '.' : c);
        }

        return builder.ToString();
    }

    public static string NameOf(string id)
    {
        var qname = QNameOf(id);
        var dot = qname.LastIndexOf('.');
        return dot < 0 ? qname : qname[(dot + 1)..];
    }

    /// <summary>A readable name for a symbol that has no declaration in the index.</summary>
    public static string DisplayOf(string id)
    {
        var qname = QNameOf(id);
        var parts = qname.Split('.');
        var kind = KindOf(id);
        if (kind == SymKind.Type || parts.Length < 2)
        {
            return parts[^1];
        }

        return kind == SymKind.Constructor ? "new " + parts[^2] : parts[^2] + "." + parts[^1];
    }

    private static readonly Dictionary<string, string> Keywords = new(StringComparer.Ordinal)
    {
        ["T:System.String"] = "string",
        ["T:System.Int32"] = "int",
        ["T:System.Int64"] = "long",
        ["T:System.Boolean"] = "bool",
        ["T:System.Object"] = "object",
        ["T:System.Guid"] = "Guid",
        ["T:System.Decimal"] = "decimal",
        ["T:System.Double"] = "double",
        ["T:System.Byte"] = "byte",
    };

    /// <summary>A readable form of a type key: <c>T:Ns.IRepo`1&lt;T:Ns.Order&gt;</c> gives <c>IRepo&lt;Order&gt;</c>.</summary>
    public static string DisplayOfKey(string typeKey)
    {
        var open = typeKey.IndexOf('<', StringComparison.Ordinal);
        var name = DisplayOf(DefOf(typeKey));
        var close = typeKey.LastIndexOf('>');
        if (open < 0 || close < open)
        {
            return name;
        }

        var arguments = new List<string>();
        var depth = 0;
        var start = open + 1;
        for (var i = start; i < close; i++)
        {
            depth += typeKey[i] switch { '<' => 1, '>' => -1, _ => 0 };
            if (typeKey[i] == ',' && depth == 0)
            {
                arguments.Add(typeKey[start..i]);
                start = i + 1;
            }
        }

        arguments.Add(typeKey[start..close]);
        var shown = arguments.Select(a => a.Length > 0 && a[0] is '!' or '?' ? a[1..] : Keywords.GetValueOrDefault(a) ?? DisplayOfKey(a));
        return $"{name}<{string.Join(", ", shown)}>";
    }

    /// <summary>The assembly a source symbol id carries (<c>M:[Asm]Ns.T.M</c>), or null for metadata symbols.</summary>
    public static string? AssemblyOf(string id)
    {
        if (id.Length < 4 || id[2] != '[')
        {
            return null;
        }

        var close = id.IndexOf(']', StringComparison.Ordinal);
        return close < 0 ? null : id[3..close];
    }

    private static string AssemblyTag(string id)
    {
        var assembly = AssemblyOf(id);
        return assembly is null ? string.Empty : "[" + assembly + "]";
    }

    /// <summary>The id without kind prefix, assembly tag and parameter list.</summary>
    private static string HeadOf(string id)
    {
        var body = id.AsSpan(2);
        if (body.Length > 0 && body[0] == '[')
        {
            body = body[(body.IndexOf(']') + 1)..];
        }

        var paren = body.IndexOf('(');
        return (paren < 0 ? body : body[..paren]).ToString();
    }
}
