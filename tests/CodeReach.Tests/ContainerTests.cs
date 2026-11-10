using System.Text.Json;
using System.Text.RegularExpressions;
using CodeReach.Core.Facts;
using CodeReach.Core.Graph;
using CodeReach.Core.Tools;
using Xunit;

namespace CodeReach.Tests;

/// <summary>Dependency injection and type hierarchy, against the sample's audited registrations.</summary>
[Collection(SampleCollection.Name)]
public sealed partial class ContainerTests(SampleFixture sample)
{
    // Every registration in the sample, audited by hand: lifetime, service, implementation, file:line.
    private static readonly string[] Audited =
    [
        "singleton IOrderRepository OrderRepository src/Shop.Api/Program.cs:13",
        "singleton IProductRepository ProductRepository src/Shop.Api/Program.cs:14",
        "scoped IOrderService OrderService src/Shop.Api/Program.cs:15",
        "scoped IOrderPurger OrderPurger src/Shop.Api/Program.cs:16",
        "scoped IProductService ProductService src/Shop.Api/Program.cs:17",
        "scoped INotifier EmailNotifier src/Shop.Api/Program.cs:18",
        "singleton PricingCalculator DiscountedPricingCalculator src/Shop.Api/Program.cs:19",
        "singleton OrderPurgeService OrderPurgeService src/Shop.Api/Program.cs:20",
        "singleton IClock SystemClock src/Shop.Infrastructure/DependencyInjection/ServiceRegistration.cs:16",
        "scoped IRepository<T> InMemoryStore<T> src/Shop.Infrastructure/DependencyInjection/ServiceRegistration.cs:17",
        "singleton INotifier SmsNotifier src/Shop.Infrastructure/DependencyInjection/ServiceRegistration.cs:18",
        "transient IAuditSink FileAuditSink src/Shop.Infrastructure/DependencyInjection/ServiceRegistration.cs:19",
        "transient AuditTrail AuditTrail src/Shop.Infrastructure/DependencyInjection/ServiceRegistration.cs:20",
        "singleton ChannelReader<string> ChannelReader<string> src/Shop.Infrastructure/DependencyInjection/ServiceRegistration.cs:23",
        "singleton ChannelReader<int> ChannelReader<int> src/Shop.Infrastructure/DependencyInjection/ServiceRegistration.cs:24",
        "singleton AuditFlushJob AuditFlushJob src/Shop.Infrastructure/DependencyInjection/ServiceRegistration.cs:25",
    ];

    private CodeGraph Graph => sample.Graph;

    [Fact]
    public void Registrations_match_the_audited_list()
    {
        var actual = Graph.Registrations
            .Select(r => $"{r.Fact.Lifetime} {Name(r.Fact.Service)} {Name(r.Fact.Implementation!)} {r.Location}")
            .ToArray();

        Assert.Equal(Audited, actual);
    }

    [Fact]
    public void Every_registration_call_in_the_source_text_is_recorded()
    {
        // Oracle that searches differently: the call names in the source text, outside comments.
        var calls = Directory.EnumerateFiles(Path.Combine(SampleFixture.SampleDirectory, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(File.ReadLines)
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Sum(line => RegistrationCall().Matches(line).Count);

        Assert.True(calls >= 10, "the oracle found too few calls to mean anything");
        Assert.Equal(calls, Graph.Registrations.Count);
    }

    [Fact]
    public void A_generic_helper_is_recorded_at_each_call_with_its_type_argument()
    {
        var job = Assert.Single(Graph.Registrations, r => Name(r.Fact.Service) == "AuditFlushJob");

        Assert.Equal("AddHostedService", job.Fact.Method);
        Assert.Equal("through AddJob<AuditFlushJob>, src/Shop.Infrastructure/DependencyInjection/ServiceRegistration.cs:29", job.Fact.Detail);
        Assert.DoesNotContain(Graph.Registrations, r => r.Fact.Line == 29);
    }

    [Fact]
    public void Constructor_dependencies_are_recorded_for_explicit_and_primary_constructors()
    {
        string[] Of(string type) => [.. Graph.Dependencies
            .Where(d => Name(d.Consumer) == type).OrderBy(d => d.Fact.Ordinal).Select(d => Name(d.Fact.Type))];

        Assert.Equal(["IOrderRepository", "IOrderPurger", "INotifier"], Of("OrderService"));
        Assert.Equal(["IOrderRepository", "IOrderPurger"], Of("OrderPurgeService"));
        Assert.Equal(["IAuditSink", "IClock", "IReportExporter"], Of("AuditTrail"));
    }

    [Fact]
    public async Task Overview_reports_the_captive_dependency_the_unregistered_service_and_no_false_duplicate()
    {
        var result = await Call("di_registrations", "{}");

        Assert.False(result.IsError, result.Text);
        Assert.True(result.Finding);
        Assert.Contains("Captive dependencies (a singleton holding a scoped service for the app's lifetime): 1", result.Text, StringComparison.Ordinal);
        Assert.Contains("OrderPurgeService (singleton, hosted service, src/Shop.Api/Program.cs:20) takes IOrderPurger (scoped", result.Text, StringComparison.Ordinal);
        Assert.Contains("no registration found (interfaces and abstract types asked for by container-built types): 1", result.Text, StringComparison.Ordinal);
        Assert.Contains("  IReportExporter  src/Shop.Infrastructure/DependencyInjection/ServiceRegistration.cs:57", result.Text, StringComparison.Ordinal);

        // The keyed SmsNotifier does not replace the plain EmailNotifier registration.
        Assert.Contains("Registered more than once (one injection gets the last; IEnumerable<T> gets all): 0", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_focused_answer_lists_registrations_implementations_and_consumers()
    {
        var result = await Call("di_registrations", """{"symbol":"OrderPurgeService"}""");

        Assert.True(result.Finding);
        Assert.Contains("singleton, hosted service    OrderPurgeService  src/Shop.Api/Program.cs:20", result.Text, StringComparison.Ordinal);
        Assert.Contains("    IOrderPurger: scoped at src/Shop.Api/Program.cs:16", result.Text, StringComparison.Ordinal);
        Assert.Contains("CAPTIVE DEPENDENCY: OrderPurgeService is singleton, hosted service", result.Text, StringComparison.Ordinal);

        var exporter = await Call("di_registrations", """{"symbol":"AuditTrail"}""");
        Assert.Contains("    IReportExporter: NO REGISTRATION FOUND", exporter.Text, StringComparison.Ordinal);
        Assert.False(exporter.Finding);
    }

    [Fact]
    public async Task Type_hierarchy_separates_direct_from_indirect_and_names_what_each_member_implements()
    {
        var notifier = await Call("type_hierarchy", """{"symbol":"INotifier"}""");
        Assert.Contains("Derived and implementing types: 3", notifier.Text, StringComparison.Ordinal);
        Assert.Contains("abstract class  NotifierBase  src/Shop.Application/Notifications/Notifiers.cs:11\n", notifier.Text, StringComparison.Ordinal);
        Assert.Contains("class           EmailNotifier  src/Shop.Application/Notifications/Notifiers.cs:26  (indirect)", notifier.Text, StringComparison.Ordinal);
        Assert.Contains("method      INotifier.SendAsync(string)  :8  implemented by NotifierBase.SendAsync(string)", notifier.Text, StringComparison.Ordinal);

        var repository = await Call("type_hierarchy", """{"symbol":"IRepository"}""");
        Assert.Contains("InMemoryStore<T>", repository.Text, StringComparison.Ordinal);
        Assert.Contains("Registered in dependency injection:", repository.Text, StringComparison.Ordinal);

        var external = await Call("type_hierarchy", """{"symbol":"BackgroundService"}""");
        Assert.Contains("class           OrderPurgeService  src/Shop.Infrastructure/Jobs/OrderPurgeService.cs:7", external.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_member_is_answered_with_its_type()
    {
        var result = await Call("type_hierarchy", """{"symbol":"NotifierBase.Format"}""");

        Assert.Contains("type hierarchy of NotifierBase (abstract class)", result.Text, StringComparison.Ordinal);
        Assert.Contains("is a member; showing its type NotifierBase", result.Text, StringComparison.Ordinal);
        Assert.Contains("NotifierBase.Format(string)  :21  overridden by EmailNotifier.Format(string)", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_type_wins_over_a_member_with_the_same_name()
    {
        // The clash is real: the general resolver refuses the bare name.
        var clash = await Call("callers", """{"symbol":"AuditFlushJob"}""");
        Assert.StartsWith("AMBIGUOUS_SYMBOL:", clash.Text, StringComparison.Ordinal);

        var hierarchy = await Call("type_hierarchy", """{"symbol":"AuditFlushJob"}""");
        var registrations = await Call("di_registrations", """{"symbol":"AuditFlushJob"}""");

        Assert.StartsWith("type hierarchy of AuditFlushJob (class)", hierarchy.Text, StringComparison.Ordinal);
        Assert.StartsWith("dependency injection for AuditFlushJob", registrations.Text, StringComparison.Ordinal);
        Assert.Contains("    AuditTrail: transient at src/Shop.Infrastructure/DependencyInjection/ServiceRegistration.cs:20", registrations.Text, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\.(Try)?Add(Keyed)?(Scoped|Transient|Singleton|HostedService)\b")]
    private static partial Regex RegistrationCall();

    private string Name(string key) => key.Contains('<', StringComparison.Ordinal) ? Ids.DisplayOfKey(key) : Graph.Node(key).Fact.Display;

    private Task<ToolResult> Call(string tool, string arguments) =>
        ToolRegistry.CallAsync(new FixedHost(Graph, SampleFixture.Config), tool, JsonDocument.Parse(arguments).RootElement, TestContext.Current.CancellationToken);
}
