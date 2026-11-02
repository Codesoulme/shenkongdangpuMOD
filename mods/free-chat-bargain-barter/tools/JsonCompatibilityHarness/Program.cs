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
        var content = Calls == 1
            ? "{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}"
            : "{\"choices\":[{\"message\":{\"content\":\"{\\\"npc_reply\\\":\\\"收到\\\"}\"}}]}";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
    }
}
