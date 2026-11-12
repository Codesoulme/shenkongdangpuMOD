using System.Net;
using AIWorldPawnshop.Model;
using AIWorldPawnshop.Models;
using AIWorldPawnshop.Storage;

var dataRoot = Path.Combine(Path.GetTempPath(), "FreeChatBargainBarter-json-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dataRoot);
try
{
    var sample = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Content", "Config.json"));
    File.Copy(sample, Path.Combine(dataRoot, "Config.json"));
    var store = new JsonFileStore(dataRoot);
    var config = store.LoadConfig();
    Check(config.GetActiveProfile().BaseUrl == "http://127.0.0.1:11434/v1", "old config URL was not read");
    config.GetActiveProfile().Name = "兼容测试";
    store.SaveConfig(config);
    var saved = File.ReadAllText(Path.Combine(dataRoot, "Config.json"));
    Check(saved.Contains("\"base_url\"") && saved.Contains("\"api_key\""), "config field names changed");
    Check(store.LoadConfig().GetActiveProfile().Name == "兼容测试", "config did not round-trip");

    using var handler = new StubHandler();
    using var client = new HttpClient(handler);
    var profile = new ModelProfile
    {
        BaseUrl = "http://localhost/v1",
        Model = "test-model",
        Protocol = "chat_completions",
        AllowNetwork = true
    };
    var probe = await OpenAiCompatibleProvider.TestConnectionAsync(client, profile);
    Check(probe.Succeeded, "connection probe failed: " + probe.Message);
    Check(probe.ElapsedMilliseconds >= 25, "successful probe did not include response wait time");

    using (var failureHandler = new ProbeTimingHandler(HttpStatusCode.Forbidden))
    using (var failureClient = new HttpClient(failureHandler))
    {
        var failure = await OpenAiCompatibleProvider.TestConnectionAsync(failureClient, profile);
        Check(!failure.Succeeded && failure.StatusCode == 403 && failure.ElapsedMilliseconds >= 25,
            "failed probe did not preserve HTTP status and elapsed time");
    }
    using (var fallbackHandler = new ProbeTimingHandler(HttpStatusCode.NotFound, HttpStatusCode.OK))
    using (var fallbackClient = new HttpClient(fallbackHandler))
    {
        var auto = profile.Clone();
        auto.Protocol = "auto";
        var fallback = await OpenAiCompatibleProvider.TestConnectionAsync(fallbackClient, auto);
        Check(fallback.Succeeded && fallbackHandler.Calls == 2 && fallback.ElapsedMilliseconds >= 60,
            "probe duration did not include both protocol attempts");
        auto.AllowNetwork = false;
        var disabled = await OpenAiCompatibleProvider.TestConnectionAsync(fallbackClient, auto);
        Check(!disabled.Succeeded && disabled.ElapsedMilliseconds is null && fallbackHandler.Calls == 2,
            "disabled networking must not send or report a fabricated latency");
    }
    var provider = new OpenAiCompatibleProvider(client, profile);
    var reply = await provider.CompleteAsync(new ConversationRequest
    {
        CharacterKey = "test-client",
        PlayerText = "你好"
    }, CancellationToken.None);
    Check(reply?.NpcReply == "收到", "conversation response was not parsed");
    Check(handler.Calls == 2, "unexpected request count");
    Console.WriteLine("JSON config and model request smoke passed.");
}
finally
{
    var tempRoot = Path.GetFullPath(Path.GetTempPath());
    var resolvedRoot = Path.GetFullPath(dataRoot);
    if (!resolvedRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(resolvedRoot).StartsWith("FreeChatBargainBarter-json-", StringComparison.Ordinal))
        throw new InvalidOperationException("Unexpected test directory; refusing cleanup.");
    Directory.Delete(dataRoot, recursive: true);
}

static void Check(bool passed, string message)
{
    if (!passed) throw new InvalidOperationException(message);
}

sealed class StubHandler : HttpMessageHandler
{
    public int Calls { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        if (!body.Contains("\"model\":\"test-model\""))
            throw new InvalidOperationException("request JSON was not serialized");
        Calls++;
        if (Calls == 1)
            await Task.Delay(40, cancellationToken);
        var content = Calls == 1
            ? "{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}"
            : "{\"choices\":[{\"message\":{\"content\":\"{\\\"npc_reply\\\":\\\"收到\\\"}\"}}]}";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
    }
}

sealed class ProbeTimingHandler : HttpMessageHandler
{
    private readonly Queue<HttpStatusCode> _statuses;
    public int Calls { get; private set; }

    public ProbeTimingHandler(params HttpStatusCode[] statuses) => _statuses = new(statuses);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Calls++;
        await Task.Delay(40, cancellationToken);
        var status = _statuses.Dequeue();
        var body = status == HttpStatusCode.OK
            ? (request.RequestUri!.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal)
                ? "{\"output_text\":\"OK\"}"
                : "{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}")
            : "{\"error\":{\"message\":\"test failure\"}}";
        return new HttpResponseMessage(status) { Content = new StringContent(body) };
    }
}
