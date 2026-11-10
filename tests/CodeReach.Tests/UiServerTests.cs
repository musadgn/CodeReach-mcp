using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CodeReach.Mcp;
using Xunit;

namespace CodeReach.Tests;

/// <summary>The UI door over real HTTP on the loopback interface: what it refuses, and that it answers from the registry.</summary>
[Collection(SampleCollection.Name)]
public sealed class UiServerTests(SampleFixture sample)
{
    [Fact]
    public async Task The_page_is_served_with_a_strict_content_security_policy()
    {
        await using var server = UiServer.Start(new FixedHost(sample.Graph, SampleFixture.Config));
        using var client = new HttpClient();

        var page = await client.GetAsync(server.Origin + "/", TestContext.Current.CancellationToken);
        var script = await client.GetAsync(server.Origin + "/app.js", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("script-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.StartsWith("http://127.0.0.1:", server.Url, StringComparison.Ordinal);
        Assert.Contains("/#t=", server.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_calls_without_the_token_from_another_origin_or_for_another_host_are_refused()
    {
        await using var server = UiServer.Start(new FixedHost(sample.Graph, SampleFixture.Config));
        using var client = new HttpClient();
        var cancel = TestContext.Current.CancellationToken;

        var noToken = await client.PostAsJsonAsync(server.Origin + "/api/call", new { tool = "routes" }, cancel);
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);

        using var wrongToken = Call(server, "routes", token: new string('0', server.Token.Length));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(wrongToken, cancel)).StatusCode);

        using var otherOrigin = Call(server, "routes");
        otherOrigin.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(otherOrigin, cancel)).StatusCode);

        using var rebound = Call(server, "routes");
        rebound.Headers.Host = "rebound.example:" + server.Port;
        // HttpListener's own prefix match refuses it (404) before the server's Host check (421) would.
        var reboundResponse = await client.SendAsync(rebound, cancel);
        Assert.Contains(reboundResponse.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MisdirectedRequest });
        Assert.DoesNotContain("\"data\"", await reboundResponse.Content.ReadAsStringAsync(cancel), StringComparison.Ordinal);

        using var ok = Call(server, "routes");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(ok, cancel)).StatusCode);
    }

    [Fact]
    public async Task Structured_answers_agree_with_the_text_answers()
    {
        await using var server = UiServer.Start(new FixedHost(sample.Graph, SampleFixture.Config));
        using var client = new HttpClient();
        var cancel = TestContext.Current.CancellationToken;

        using var routesRequest = Call(server, "routes", """{"limit":1000}""");
        var routes = (await (await client.SendAsync(routesRequest, cancel)).Content.ReadFromJsonAsync<JsonObject>(cancel))!;
        Assert.Equal(18, routes["data"]!["total"]!.GetValue<int>());
        Assert.Equal(18, routes["data"]!["endpoints"]!.AsArray().Count);
        Assert.Equal(7, routes["data"]!["none"]!.GetValue<int>());
        Assert.Contains("18 of 18 endpoints", routes["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(routes["data"]!["index"]!["complete"]!.GetValue<bool>());

        using var traceRequest = Call(server, "trace_to_entrypoints", """{"symbol":"OrderRepository.HardDeleteAsync"}""");
        var trace = (await (await client.SendAsync(traceRequest, cancel)).Content.ReadFromJsonAsync<JsonObject>(cancel))!;
        Assert.Equal(4, trace["data"]!["rootedTotal"]!.GetValue<int>());
        Assert.Contains("4 entry points (3 endpoint, 1 hosted-service)", trace["text"]!.GetValue<string>(), StringComparison.Ordinal);
        var firstPath = trace["data"]!["rooted"]![0]!["hops"]!.AsArray();
        Assert.Equal("OrderRepository.HardDeleteAsync(Guid)", firstPath[^1]!["display"]!.GetValue<string>());

        using var refused = Call(server, "callers", """{"symbol":"Delete"}""");
        var refusal = (await (await client.SendAsync(refused, cancel)).Content.ReadFromJsonAsync<JsonObject>(cancel))!;
        Assert.True(refusal["isError"]!.GetValue<bool>());
        Assert.StartsWith("AMBIGUOUS_SYMBOL:", refusal["text"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    private static HttpRequestMessage Call(UiServer server, string tool, string args = "{}", string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, server.Origin + "/api/call")
        {
            Content = JsonContent.Create(new JsonObject { ["tool"] = tool, ["args"] = JsonNode.Parse(args) }),
        };
        request.Headers.Add("X-CodeReach-Token", token ?? server.Token);
        return request;
    }
}
