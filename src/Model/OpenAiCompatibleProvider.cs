using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIWorldPawnshop.Models;
using AIWorldPawnshop.Runtime;

namespace AIWorldPawnshop.Model;

public sealed class OpenAiCompatibleProvider : IModelProvider
{
    private const string AutoProtocol = "auto";
    private const string ChatCompletionsProtocol = "chat_completions";
    private const string ResponsesProtocol = "responses";

    private readonly HttpClient _httpClient;
    private readonly ModelProfile _profile;
    private string? _resolvedProtocol;

    public OpenAiCompatibleProvider(HttpClient httpClient, ModelProfile profile)
    {
        _httpClient = httpClient;
        _profile = profile.Clone();
        _profile.Normalize();
    }

    public async Task<ConversationResponse?> CompleteAsync(
        ConversationRequest request,
        CancellationToken cancellationToken)
    {
        if (!CanUseProfile(_profile))
            return null;

        var characterRequest = RemoveGmOnlyKnowledge(request);
        RequestAttempt? lastAttempt = null;
        foreach (var protocol in GetRequestProtocolOrder())
        {
            lastAttempt = await SendConversationAsync(
                    _profile,
                    protocol,
                    characterRequest,
                    cancellationToken)
                .ConfigureAwait(false);

            if (lastAttempt.Response is not null)
            {
                _resolvedProtocol = protocol;
                return lastAttempt.Response;
            }

            if (!ShouldTryAlternative(_profile, lastAttempt))
                break;
        }

        return null;
    }

    private static ConversationRequest RemoveGmOnlyKnowledge(ConversationRequest request)
    {
        if (request.GameContext.GmKnowledge.Count == 0)
            return request;

        var source = request.GameContext;
        var context = new GameConversationContext
        {
            Authority = source.Authority,
            Character = source.Character,
            OriginalCharacterProfile = source.OriginalCharacterProfile,
            Trade = source.Trade,
            OriginalStoryFacts = source.OriginalStoryFacts,
            OriginalDialogueContext = source.OriginalDialogueContext,
            Relationship = source.Relationship,
            Procurement = source.Procurement,
            ActiveWorldEvents = source.ActiveWorldEvents,
            ThirdPartyTasks = source.ThirdPartyTasks,
            GmKnowledge = new List<string>()
        };
        return new ConversationRequest
        {
            ConversationId = request.ConversationId,
            TurnId = request.TurnId,
            CharacterKey = request.CharacterKey,
            CharacterDisplayName = request.CharacterDisplayName,
            FactionKey = request.FactionKey,
            Scene = request.Scene,
            PlayerText = request.PlayerText,
            OffTopicStreak = request.OffTopicStreak,
            RecentHistory = request.RecentHistory,
            GameContext = context
        };
    }

    public async Task<ConversationResponse?> CompleteGmAsync(
        ConversationRequest request,
        ConversationResponse candidateResponse,
        CancellationToken cancellationToken)
    {
        if (!CanUseProfile(_profile)) return null;
        var gmRequest = new ConversationRequest
        {
            ConversationId = request.ConversationId,
            TurnId = request.TurnId,
            CharacterKey = request.CharacterKey,
            CharacterDisplayName = request.CharacterDisplayName,
            FactionKey = request.FactionKey,
            Scene = request.Scene,
            PlayerText = request.PlayerText,
            RecentHistory = request.RecentHistory
                .Concat(new[]
                {
                    $"人物候选回复：{candidateResponse.NpcReply}",
                    $"人物候选转账提案：explicit={candidateResponse.WalletTransfer.Explicit}, accepted={candidateResponse.WalletTransfer.Accepted}, amount={candidateResponse.WalletTransfer.Amount}, source={candidateResponse.WalletTransfer.Source}"
                })
                .TakeLast(10)
                .ToList(),
            GameContext = request.GameContext
        };
        foreach (var protocol in GetRequestProtocolOrder())
        {
            var attempt = await SendConversationAsync(
                    _profile,
                    protocol,
                    gmRequest,
                    cancellationToken,
                    GmSystemPrompt,
                    maxOutputTokens: Math.Min(_profile.MaxOutputTokens, 280))
                .ConfigureAwait(false);
            if (attempt.Response is not null)
            {
                _resolvedProtocol = protocol;
                return attempt.Response;
            }
            if (!ShouldTryAlternative(_profile, attempt)) break;
        }
        return null;
    }

    public static async Task<ModelConnectionResult> TestConnectionAsync(
        HttpClient httpClient,
        ModelProfile profile,
        CancellationToken cancellationToken = default)
    {
        var candidate = profile.Clone();
        candidate.Normalize();

        if (!candidate.AllowNetwork)
            return ModelConnectionResult.Failure("请先打开“允许联网”");
        if (!candidate.Provider.Equals("openai-compatible", StringComparison.OrdinalIgnoreCase))
            return ModelConnectionResult.Failure("当前只支持 OpenAI 兼容接口");
        if (string.IsNullOrWhiteSpace(candidate.BaseUrl))
            return ModelConnectionResult.Failure("接口地址不能为空");
        if (string.IsNullOrWhiteSpace(candidate.Model))
            return ModelConnectionResult.Failure("模型名称不能为空");

        var attempts = new List<RequestAttempt>();
        foreach (var protocol in GetProtocolOrder(candidate))
        {
            var attempt = await SendProbeAsync(
                    httpClient,
                    candidate,
                    protocol,
                    cancellationToken)
                .ConfigureAwait(false);
            attempts.Add(attempt);

            if (attempt.Succeeded)
            {
                return ModelConnectionResult.SuccessResult(
                    $"连接成功，已使用 {ProtocolLabel(protocol)} 协议");
            }

            if (!ShouldTryAlternative(candidate, attempt))
                break;
        }

        var last = attempts.LastOrDefault();
        var message = attempts.Count == 1
            ? last?.Error ?? "连接失败"
            : string.Join("；", attempts.Select(attempt => attempt.Error));
        return ModelConnectionResult.Failure(
            ShortenError(message, 420),
            last?.StatusCode);
    }

    private async Task<RequestAttempt> SendConversationAsync(
        ModelProfile profile,
        string protocol,
        ConversationRequest request,
        CancellationToken cancellationToken)
        => await SendConversationAsync(profile, protocol, request, cancellationToken, ConversationSystemPrompt).ConfigureAwait(false);

    private async Task<RequestAttempt> SendConversationAsync(
        ModelProfile profile,
        string protocol,
        ConversationRequest request,
        CancellationToken cancellationToken,
        string systemPrompt,
        int? maxOutputTokens = null)
    {
        try
        {
            using var timeout = CreateTimeout(profile.TimeoutMs, cancellationToken);
            using var message = CreateMessage(
                profile,
                protocol,
                BuildConversationBody(profile, protocol, request, systemPrompt, maxOutputTokens));
            using var response = await _httpClient
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            var responseBody = await response.Content
                .ReadAsStringAsync(timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return RequestAttempt.Failure(
                    FormatHttpFailure((int)response.StatusCode, responseBody, protocol),
                    (int)response.StatusCode);
            }

            using var document = JsonDocument.Parse(responseBody);
            var content = ExtractResponseContent(document.RootElement, protocol);
            if (string.IsNullOrWhiteSpace(content))
            {
                return RequestAttempt.Failure(
                    $"{ProtocolLabel(protocol)} 已响应，但没有可读取的文本内容",
                    (int)response.StatusCode);
            }

            var parsed = ParseResponse(content);
            return parsed is null
                ? RequestAttempt.Failure(
                    $"{ProtocolLabel(protocol)} 已响应，但内容不是有效的当铺世界 JSON",
                    (int)response.StatusCode)
                : RequestAttempt.Success(parsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RequestAttempt.Failure(
                $"{ProtocolLabel(protocol)} 连接超时（{Math.Clamp(profile.TimeoutMs, 1000, 120000)} ms）",
                null);
        }
        catch (OperationCanceledException)
        {
            return RequestAttempt.Failure("模型请求已取消", null);
        }
        catch (JsonException)
        {
            return RequestAttempt.Failure(
                $"{ProtocolLabel(protocol)} 已响应，但不是有效 JSON",
                null);
        }
        catch (Exception ex)
        {
            return RequestAttempt.Failure(
                $"{ProtocolLabel(protocol)} 连接失败：{ShortenError(ex.Message)}",
                null);
        }
    }

    private static async Task<RequestAttempt> SendProbeAsync(
        HttpClient httpClient,
        ModelProfile profile,
        string protocol,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CreateTimeout(profile.TimeoutMs, cancellationToken);
            using var message = CreateMessage(
                profile,
                protocol,
                BuildProbeBody(profile, protocol));
            using var response = await httpClient
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            var responseBody = await response.Content
                .ReadAsStringAsync(timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return RequestAttempt.Failure(
                    FormatHttpFailure((int)response.StatusCode, responseBody, protocol),
                    (int)response.StatusCode);
            }

            using var document = JsonDocument.Parse(responseBody);
            var content = ExtractResponseContent(document.RootElement, protocol);
            return string.IsNullOrWhiteSpace(content)
                ? RequestAttempt.Failure(
                    $"{ProtocolLabel(protocol)} 已响应，但返回格式无法识别",
                    (int)response.StatusCode)
                : RequestAttempt.Success(null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RequestAttempt.Failure(
                $"{ProtocolLabel(protocol)} 连接超时（{Math.Clamp(profile.TimeoutMs, 1000, 120000)} ms）",
                null);
        }
        catch (OperationCanceledException)
        {
            return RequestAttempt.Failure("连接检测已取消", null);
        }
        catch (JsonException)
        {
            return RequestAttempt.Failure(
                $"{ProtocolLabel(protocol)} 已响应，但不是有效 JSON",
                null);
        }
        catch (Exception ex)
        {
            return RequestAttempt.Failure(
                $"{ProtocolLabel(protocol)} 连接失败：{ShortenError(ex.Message)}",
                null);
        }
    }

    private static object BuildConversationBody(
        ModelProfile profile,
        string protocol,
        ConversationRequest request,
        string systemPrompt = ConversationSystemPrompt,
        int? maxOutputTokens = null)
    {
        var outputLimit = Math.Clamp(
            maxOutputTokens ?? profile.MaxOutputTokens,
            32,
            8000);
        if (protocol == ResponsesProtocol)
        {
            return new
            {
                model = profile.Model,
                instructions = systemPrompt + ThirdPartyTaskRules +
                    "\nReturn a valid json object and do not wrap it in markdown.",
                input = "Return a json object for this conversation:\n" +
                    JsonSerializer.Serialize(request),
                max_output_tokens = outputLimit,
                reasoning = new { effort = "low" },
                text = new { format = new { type = "json_object" } }
            };
        }

        return new
        {
            model = profile.Model,
            temperature = 0.7,
            max_tokens = outputLimit,
            response_format = new { type = "json_object" },
            messages = BuildConversationMessages(request, systemPrompt + ThirdPartyTaskRules)
        };
    }

    private static object BuildProbeBody(ModelProfile profile, string protocol)
    {
        return protocol == ResponsesProtocol
            ? new
            {
                model = profile.Model,
                input = "Reply with OK.",
                max_output_tokens = 8
            }
            : new
            {
                model = profile.Model,
                temperature = 0,
                max_tokens = 8,
                messages = new object[]
                {
                    new { role = "user", content = "Reply with OK." }
                }
            };
    }

    private static object[] BuildConversationMessages(ConversationRequest request, string systemPrompt = ConversationSystemPrompt)
    {
        return new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = JsonSerializer.Serialize(request) }
        };
    }

    private static HttpRequestMessage CreateMessage(
        ModelProfile profile,
        string protocol,
        object body)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Post,
            BuildEndpoint(profile.BaseUrl, protocol));
        var apiKey = ResolveApiKey(profile);
        if (!string.IsNullOrWhiteSpace(apiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        message.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        message.Content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");
        return message;
    }

    private static CancellationTokenSource CreateTimeout(
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(Math.Clamp(timeoutMs, 1000, 120000));
        return source;
    }

    private static bool CanUseProfile(ModelProfile profile)
    {
        return profile.AllowNetwork &&
            profile.Provider.Equals("openai-compatible", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(profile.BaseUrl) &&
            !string.IsNullOrWhiteSpace(profile.Model);
    }

    private static IReadOnlyList<string> GetProtocolOrder(ModelProfile profile)
    {
        return profile.Protocol switch
        {
            ResponsesProtocol => new[] { ResponsesProtocol },
            ChatCompletionsProtocol => new[] { ChatCompletionsProtocol },
            _ when PrefersResponses(profile.Model) =>
                new[] { ResponsesProtocol, ChatCompletionsProtocol },
            _ => new[] { ChatCompletionsProtocol, ResponsesProtocol }
        };
    }

    private IReadOnlyList<string> GetRequestProtocolOrder()
    {
        var order = GetProtocolOrder(_profile);
        if (string.IsNullOrWhiteSpace(_resolvedProtocol))
            return order;

        return new[] { _resolvedProtocol }
            .Concat(order.Where(protocol =>
                !protocol.Equals(_resolvedProtocol, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private static bool ShouldTryAlternative(ModelProfile profile, RequestAttempt attempt)
    {
        if (!profile.Protocol.Equals(AutoProtocol, StringComparison.OrdinalIgnoreCase))
            return false;

        return attempt.StatusCode is not 401 and not 403 &&
            !attempt.Error.Equals("连接检测已取消", StringComparison.Ordinal) &&
            !attempt.Error.Equals("模型请求已取消", StringComparison.Ordinal);
    }

    private static bool PrefersResponses(string model)
    {
        var normalized = model.Trim().ToLowerInvariant();
        return normalized.StartsWith("gpt-5", StringComparison.Ordinal) ||
            normalized.StartsWith("o1", StringComparison.Ordinal) ||
            normalized.StartsWith("o3", StringComparison.Ordinal) ||
            normalized.StartsWith("o4", StringComparison.Ordinal) ||
            normalized.Contains("codex", StringComparison.Ordinal);
    }

    private static string BuildEndpoint(string baseUrl, string protocol)
    {
        var endpoint = baseUrl.Trim().TrimEnd('/');
        var wantsResponses = protocol == ResponsesProtocol;
        var targetSuffix = wantsResponses ? "/responses" : "/chat/completions";
        var oppositeSuffix = wantsResponses ? "/chat/completions" : "/responses";

        if (endpoint.EndsWith(targetSuffix, StringComparison.OrdinalIgnoreCase))
            return endpoint;

        if (endpoint.EndsWith(oppositeSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var prefix = endpoint[..^oppositeSuffix.Length].TrimEnd('/');
            if (!prefix.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                prefix += "/v1";
            return prefix + targetSuffix;
        }

        return endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? endpoint + targetSuffix
            : endpoint + "/v1" + targetSuffix;
    }

    private static string ResolveApiKey(ModelProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.ApiKey))
            return profile.ApiKey.Trim();

        return string.IsNullOrWhiteSpace(profile.ApiKeyEnv)
            ? string.Empty
            : Environment.GetEnvironmentVariable(profile.ApiKeyEnv.Trim()) ?? string.Empty;
    }

    private static string? ExtractResponseContent(JsonElement root, string protocol)
    {
        return protocol == ResponsesProtocol
            ? TryGetResponsesContent(root)
            : TryGetMessageContent(root);
    }

    private static string? TryGetMessageContent(JsonElement root)
    {
        try
        {
            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
                return null;

            var message = choices[0].GetProperty("message");
            return message.TryGetProperty("content", out var content)
                ? ExtractTextValue(content)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetResponsesContent(JsonElement root)
    {
        try
        {
            if (root.TryGetProperty("output_text", out var outputText) &&
                outputText.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(outputText.GetString()))
            {
                return outputText.GetString();
            }

            if (!root.TryGetProperty("output", out var output) ||
                output.ValueKind != JsonValueKind.Array)
                return null;

            var pieces = new List<string>();
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var content))
                    continue;
                var text = ExtractTextValue(content);
                if (!string.IsNullOrWhiteSpace(text))
                    pieces.Add(text);
            }

            return pieces.Count == 0 ? null : string.Join("\n", pieces);
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractTextValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString();

        if (value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("text", out var objectText) &&
            objectText.ValueKind == JsonValueKind.String)
            return objectText.GetString();

        if (value.ValueKind != JsonValueKind.Array)
            return null;

        var pieces = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                pieces.Add(item.GetString() ?? string.Empty);
            }
            else if (item.ValueKind == JsonValueKind.Object &&
                     item.TryGetProperty("text", out var text) &&
                     text.ValueKind == JsonValueKind.String)
            {
                pieces.Add(text.GetString() ?? string.Empty);
            }
        }

        return pieces.Count == 0 ? null : string.Join(string.Empty, pieces);
    }

    private static string FormatHttpFailure(int statusCode, string responseBody, string protocol)
    {
        var detail = ExtractErrorDetail(responseBody);
        var prefix = $"{ProtocolLabel(protocol)} 返回 HTTP {statusCode}";
        if (statusCode is 401 or 403)
            prefix += "（密钥未通过认证）";
        else if (statusCode == 404)
            prefix += "（接口路径不存在）";
        else if (statusCode == 503)
            prefix += "（中转服务当前没有可用上游）";

        return string.IsNullOrWhiteSpace(detail)
            ? prefix
            : $"{prefix}：{detail}";
    }

    private static string ExtractErrorDetail(string responseBody)
    {
        var detail = responseBody.Trim();
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out var message))
                    detail = message.GetString() ?? error.ToString();
                else
                    detail = error.ToString();
            }
            else if (root.TryGetProperty("message", out var rootMessage))
            {
                detail = rootMessage.GetString() ?? rootMessage.ToString();
            }
        }
        catch
        {
            // Keep the plain response body when a gateway returns non-JSON text.
        }

        return ShortenError(detail.ReplaceLineEndings(" "));
    }

    private static string ProtocolLabel(string protocol) =>
        protocol == ResponsesProtocol ? "Responses" : "Chat Completions";

    private static string ShortenError(string message, int maxLength = 180)
    {
        var result = string.IsNullOrWhiteSpace(message) ? "未知错误" : message.Trim();
        return result.Length > maxLength ? result[..maxLength] : result;
    }

    private static ConversationResponse? ParseResponse(string content)
    {
        content = content.Trim();
        if (content.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLineEnd = content.IndexOf('\n');
            var lastFence = content.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLineEnd >= 0 && lastFence > firstLineEnd)
                content = content[(firstLineEnd + 1)..lastFence].Trim();
        }

        var result = TryParseResponseObject(content);
        if (result is not null)
            return result;

        var firstBrace = content.IndexOf('{');
        var lastBrace = content.LastIndexOf('}');
        return firstBrace >= 0 && lastBrace > firstBrace
            ? TryParseResponseObject(content[firstBrace..(lastBrace + 1)])
            : null;
    }

    private static ConversationResponse? TryParseResponseObject(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var npcReply = ReadString(root, "npc_reply") ??
                ReadString(root, "reply") ??
                ReadString(root, "message") ??
                ReadString(root, "text");
            if (string.IsNullOrWhiteSpace(npcReply))
                return null;

            var result = new ConversationResponse
            {
                NpcReply = npcReply.Trim(),
                ConversationMode = ReadString(root, "conversation_mode") ?? "continue",
                Topic = ReadString(root, "topic") ?? "trade",
                TaskRelevance = ReadTaskRelevance(root),
                Mood = ReadString(root, "mood") ?? "calm",
                Signals = ReadSignals(root),
                MemoryCandidates = ReadMemoryCandidates(root),
                TradeProposal = ReadTradeProposal(root),
                ProcurementProposal = ReadProcurementProposal(root),
                ReturnVisitProposal = ReadReturnVisitProposal(root),
                CarriedInventoryProposal = ReadCarriedInventoryProposal(root),
                BarterProposal = ReadBarterProposal(root),
                WalletTransfer = ReadWalletTransfer(root),
                WorldModifications = ReadWorldModifications(root),
                ThirdPartyTask = ReadThirdPartyTask(root)
            };

            result.NpcReply = result.NpcReply[..Math.Min(result.NpcReply.Length, 500)];
            result.TaskRelevance = Math.Clamp(result.TaskRelevance, 0, 1);
            result.Signals.Warmth = Math.Clamp(result.Signals.Warmth, -2, 2);
            result.Signals.Respect = Math.Clamp(result.Signals.Respect, -2, 2);
            result.Signals.Trust = Math.Clamp(result.Signals.Trust, -2, 2);
            result.Signals.Offense = Math.Clamp(result.Signals.Offense, -2, 2);
            result.Signals.Threat = Math.Clamp(result.Signals.Threat, -2, 2);
            result.Signals.Cooperation = Math.Clamp(result.Signals.Cooperation, -2, 2);
            result.Signals.Credibility = Math.Clamp(result.Signals.Credibility, -2, 2);
            result.Signals.TradeWillingness = Math.Clamp(result.Signals.TradeWillingness, -2, 2);
            result.ConversationMode = result.ConversationMode.Equals(
                "return_to_task",
                StringComparison.OrdinalIgnoreCase)
                ? "return_to_task"
                : "continue";
            result.Topic = string.IsNullOrWhiteSpace(result.Topic) ? "trade" : result.Topic.Trim();
            result.TradeProposal.Percent = Math.Clamp(result.TradeProposal.Percent, 0, 100);
            if (result.TradeProposal.TargetAmount <= 0)
                result.TradeProposal.TargetAmount = null;
            result.ProcurementProposal.Category =
                ProcurementRequestResolver.NormalizeCategory(
                    result.ProcurementProposal.Category);
            result.ProcurementProposal.CategoryConfidence = Math.Clamp(
                result.ProcurementProposal.CategoryConfidence,
                0,
                1);
            result.ProcurementProposal.DueInDays = Math.Clamp(
                result.ProcurementProposal.DueInDays <= 0
                    ? 1
                    : result.ProcurementProposal.DueInDays,
                1,
                3);
            result.ProcurementProposal.PromisedPremiumPercent = Math.Clamp(
                result.ProcurementProposal.PromisedPremiumPercent,
                0,
                50);
            result.ReturnVisitProposal.DueInDays = Math.Clamp(
                result.ReturnVisitProposal.DueInDays <= 0
                    ? 1
                    : result.ReturnVisitProposal.DueInDays,
                1,
                3);
            result.WalletTransfer.Amount = Math.Clamp(result.WalletTransfer.Amount, 0, 1000000);
            result.WalletTransfer.Source = string.IsNullOrWhiteSpace(result.WalletTransfer.Source)
                ? "client_budget"
                : result.WalletTransfer.Source.Trim();
            var requestedDescription =
                result.ProcurementProposal.RequestedDescription?.Trim() ?? string.Empty;
            result.ProcurementProposal.RequestedDescription = requestedDescription.Length > 80
                ? requestedDescription[..80]
                : requestedDescription;
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ConversationSignals ReadSignals(JsonElement root)
    {
        var signals = new ConversationSignals();
        if (!TryGetProperty(root, "signals", out var value) ||
            value.ValueKind != JsonValueKind.Object)
            return signals;

        signals.Warmth = ReadInt(value, "warmth");
        signals.Respect = ReadInt(value, "respect");
        signals.Trust = ReadInt(value, "trust");
        signals.Offense = ReadInt(value, "offense");
        signals.Threat = ReadInt(value, "threat");
        signals.Cooperation = ReadInt(value, "cooperation");
        signals.Credibility = ReadInt(value, "credibility");
        signals.TradeWillingness = ReadInt(value, "trade_willingness");
        return signals;
    }

    private static List<WorldModificationProposal> ReadWorldModifications(JsonElement root)
    {
        var result = new List<WorldModificationProposal>();
        if (!TryGetProperty(root, "world_modifications", out var value) || value.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            result.Add(new WorldModificationProposal
            {
                TargetKey = ReadString(item, "target_key") ?? string.Empty,
                Field = ReadString(item, "field") ?? string.Empty,
                Operation = ReadString(item, "operation") ?? "add",
                Value = ReadDouble(item, "value"),
                DurationDays = ReadBool(item, "permanent") ? 0 : Math.Clamp(ReadInt(item, "duration_days"), 0, 30),
                Title = ReadString(item, "title") ?? string.Empty,
                Summary = ReadString(item, "summary") ?? string.Empty,
                Evidence = ReadString(item, "evidence") ?? string.Empty,
                Confidence = Math.Clamp(ReadDouble(item, "confidence"), 0, 1)
                ,Goal = ReadString(item, "goal") ?? string.Empty
                ,Path = ReadString(item, "path") ?? string.Empty
            });
        }
        return result;
    }

    private static ThirdPartyTaskProposal ReadThirdPartyTask(JsonElement root)
    {
        var result = new ThirdPartyTaskProposal();
        if (!TryGetProperty(root, "third_party_task", out var value) || value.ValueKind != JsonValueKind.Object) return result;
        result.Explicit = ReadBool(value, "explicit");
        result.Accepted = ReadBool(value, "accepted");
        result.TargetCharacterKey = ReadString(value, "target_character_key") ?? string.Empty;
        result.TargetDisplayName = ReadString(value, "target_display_name") ?? string.Empty;
        result.Purpose = ReadString(value, "purpose") ?? string.Empty;
        result.DueInDays = Math.Clamp(ReadInt(value, "due_in_days"), 1, 7);
        result.Reason = ReadString(value, "reason") ?? string.Empty;
        result.Decision = ReadString(value, "decision") ?? "none";
        return result;
    }

    private static List<MemoryCandidate> ReadMemoryCandidates(JsonElement root)
    {
        var memories = new List<MemoryCandidate>();
        if (!TryGetProperty(root, "memory_candidates", out var value) ||
            value.ValueKind != JsonValueKind.Array)
            return memories;

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var summary = ReadString(item, "summary");
            if (string.IsNullOrWhiteSpace(summary))
                continue;

            memories.Add(new MemoryCandidate
            {
                Kind = ReadString(item, "kind") ?? "information",
                Summary = summary.Trim()[..Math.Min(summary.Trim().Length, 240)],
                Importance = Math.Clamp(ReadDouble(item, "importance"), 0, 1)
            });
        }

        return memories;
    }

    private static TradeProposal ReadTradeProposal(JsonElement root)
    {
        var proposal = new TradeProposal();
        if (!TryGetProperty(root, "trade_proposal", out var value) ||
            value.ValueKind != JsonValueKind.Object)
            return proposal;

        proposal.Kind = ReadString(value, "kind") ?? "none";
        proposal.Percent = ReadInt(value, "percent");
        proposal.TargetAmount = ReadLong(value, "target_amount");
        proposal.Explicit = ReadBool(value, "explicit");
        proposal.Reason = ReadString(value, "reason") ?? string.Empty;
        return proposal;
    }

    private static ProcurementProposal ReadProcurementProposal(JsonElement root)
    {
        var proposal = new ProcurementProposal();
        if (!TryGetProperty(root, "procurement_proposal", out var value) ||
            value.ValueKind != JsonValueKind.Object)
            return proposal;

        proposal.Explicit = ReadBool(value, "explicit");
        proposal.Accepted = ReadBool(value, "accepted");
        proposal.Category = ReadString(value, "category") ?? "none";
        proposal.CategoryConfidence = ReadDouble(value, "category_confidence");
        proposal.ClarificationNeeded = ReadBool(value, "clarification_needed");
        proposal.RequestedDescription =
            ReadString(value, "requested_description") ?? string.Empty;
        proposal.DueInDays = ReadInt(value, "due_in_days");
        proposal.PromisedPremiumPercent = ReadInt(value, "promised_premium_percent");
        proposal.Reason = ReadString(value, "reason") ?? string.Empty;
        return proposal;
    }

    private static ReturnVisitProposal ReadReturnVisitProposal(JsonElement root)
    {
        var proposal = new ReturnVisitProposal();
        if (!TryGetProperty(root, "return_visit_proposal", out var value) ||
            value.ValueKind != JsonValueKind.Object)
            return proposal;

        proposal.Explicit = ReadBool(value, "explicit");
        proposal.Accepted = ReadBool(value, "accepted");
        proposal.DueInDays = ReadInt(value, "due_in_days");
        proposal.Reason = ReadString(value, "reason") ?? string.Empty;
        return proposal;
    }

    private static CarriedInventoryProposal ReadCarriedInventoryProposal(JsonElement root)
    {
        var proposal = new CarriedInventoryProposal();
        if (!TryGetProperty(root, "carried_inventory_proposal", out var value) ||
            value.ValueKind != JsonValueKind.Object)
            return proposal;

        proposal.Explicit = ReadBool(value, "explicit");
        proposal.Accepted = ReadBool(value, "accepted");
        proposal.Reason = ReadString(value, "reason") ?? string.Empty;
        return proposal;
    }

    private static BarterProposal ReadBarterProposal(JsonElement root)
    {
        var proposal = new BarterProposal();
        if (!TryGetProperty(root, "barter_proposal", out var value) ||
            value.ValueKind != JsonValueKind.Object)
            return proposal;

        proposal.Explicit = ReadBool(value, "explicit");
        proposal.Accepted = ReadBool(value, "accepted");
        var runtimeKey = ReadInt(value, "client_item_runtime_key");
        proposal.ClientItemRuntimeKey = runtimeKey > 0 ? runtimeKey : null;
        proposal.ClientItemName = ReadString(value, "client_item_name") ?? string.Empty;
        proposal.RequiredPlayerItemKey = ReadString(value, "required_player_item_key") ?? string.Empty;
        var requiredRuntimeKey = ReadInt(value, "required_player_item_runtime_key");
        proposal.RequiredPlayerItemRuntimeKey = requiredRuntimeKey > 0 ? requiredRuntimeKey : null;
        proposal.RequiredPlayerItemRuntimeKeys = ReadIntArray(value, "required_player_item_runtime_keys");
        proposal.RequiredPlayerItemName = ReadString(value, "required_player_item_name") ?? string.Empty;
        proposal.RequiredPlayerItemKeys = ReadStringArray(value, "required_player_item_keys");
        proposal.RequiredPlayerItemNames = ReadStringArray(value, "required_player_item_names");
        proposal.Reason = ReadString(value, "reason") ?? string.Empty;
        return proposal;
    }

    private static WalletTransferProposal ReadWalletTransfer(JsonElement root)
    {
        var proposal = new WalletTransferProposal();
        if (!TryGetProperty(root, "wallet_transfer", out var value) ||
            value.ValueKind != JsonValueKind.Object)
            return proposal;

        proposal.Explicit = ReadBool(value, "explicit");
        proposal.Accepted = ReadBool(value, "accepted");
        proposal.Amount = ReadInt(value, "amount");
        proposal.Source = ReadString(value, "source") ?? "client_budget";
        proposal.Reason = ReadString(value, "reason") ?? string.Empty;
        return proposal;
    }

    private static List<string> ReadStringArray(JsonElement root, string name)
    {
        var result = new List<string>();
        if (!TryGetProperty(root, name, out var value) || value.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var item in value.EnumerateArray())
        {
            var text = ReadStringValue(item)?.Trim();
            if (!string.IsNullOrWhiteSpace(text) && !result.Contains(text, StringComparer.OrdinalIgnoreCase))
                result.Add(text);
        }
        return result;
    }

    private static List<int> ReadIntArray(JsonElement root, string name)
    {
        var result = new List<int>();
        if (!TryGetProperty(root, name, out var value) || value.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var item in value.EnumerateArray())
        {
            var number = item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var parsed)
                ? parsed
                : int.TryParse(ReadStringValue(item), out parsed) ? parsed : 0;
            if (number > 0 && !result.Contains(number))
                result.Add(number);
        }
        return result;
    }

    private static double ReadTaskRelevance(JsonElement root)
    {
        if (!TryGetProperty(root, "task_relevance", out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return number;

        var text = ReadStringValue(value)?.Trim().ToLowerInvariant();
        return text switch
        {
            "none" or "无" => 0,
            "low" or "低" => 0.25,
            "medium" or "中" => 0.5,
            "high" or "高" => 0.75,
            "full" or "complete" or "完全" => 1,
            _ => 0
        };
    }

    private static int ReadInt(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        return int.TryParse(ReadStringValue(value), out var parsed) ? parsed : 0;
    }

    private static double ReadDouble(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return number;
        return double.TryParse(ReadStringValue(value), out var parsed) ? parsed : 0;
    }

    private static long? ReadLong(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        return long.TryParse(ReadStringValue(value), out var parsed) ? parsed : null;
    }

    private static bool ReadBool(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var value))
            return false;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();
        return bool.TryParse(ReadStringValue(value), out var parsed) && parsed;
    }

    private static string? ReadString(JsonElement root, string name)
    {
        return TryGetProperty(root, name, out var value)
            ? ReadStringValue(value)
            : null;
    }

    private static string? ReadStringValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }

    private static bool TryGetProperty(
        JsonElement root,
        string name,
        out JsonElement value)
    {
        if (root.TryGetProperty(name, out value))
            return true;

        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private const string GmSystemPrompt = @"你是 GM 监督 AI，不扮演人物。只分析当前玩家输入、原版上下文、人物候选回复、人物候选转账提案和 gm_knowledge，先识别目标，再按目标路径优先级选择当前状态满足条件的已有接口；直接路径不可用时选择备用路径。只能返回 JSON；npc_reply 固定填写 GM，world_modifications 只填写已确认可执行且可验证的计划，并填写 goal、path。若人物候选转账提案 explicit=true，你必须复核人物候选回复是否明确答应从当前顾客 client_budget 补付该具体金额：合理且可验证时输出同金额的 wallet_transfer 且 accepted=true，不合理或接口前置条件不满足时输出 wallet_transfer explicit=true、accepted=false；若没有候选转账提案且对话没有明确承诺，不得自行创造转账。source 固定为 client_budget。找不到合理落点时保持 world_modifications 为空。不得创造接口、字段、物品或机制。\n物品归属复核：trade.item.owner、client_carried_items 中的 owner 和 player_offered_items 中的 owner 是原版归属结论。owner=client 或界面“未拥有”表示顾客带来的物品，即使物品在玩家侧柜台、正在议价或 GetCountersItems() 返回了它，也不能当成玩家物品；只有 owner=player 的真实玩家侧物品才可作为玩家交换物。owner=unknown 时拒绝猜测并让相关提案保持未接受。identifier、internal_name、display_name、tags、condition_identifier 仅用于区分物品，不能推翻 owner。\n";

    private const string ConversationSystemPrompt = @"你负责扮演人物并提取玩家的明确意图；世界数值和已有接口的最终裁决由另一个 GM 监督步骤完成。人物不得声称未经原版流程确认的价格、交换、预算或世界变化已经生效，也不要向玩家暴露内部审核过程；world_modifications 通常留空，未知字段、虚构接口和无法验证的因果链不要填写。
你正在扮演《深空当铺》当前场景中的原版人物，而不是另一个平行角色。
original_character_profile 是不可覆盖的原版角色档案：canonical_identity 决定你是谁；role_and_background 决定身份、职业和稳定背景；current_story_state 决定剧情进度；immutable_constraints 是硬约束；knowledge_boundaries 决定人物能知道什么；speech_style_samples 是措辞、节奏和态度的模仿样本。回复前必须先统一这些信息。不得改名、自称另一名原版人物、换阵营或职业，也不得把别人的经历、电话身份和台词归到自己身上。资料不足时保持含糊，不要补造身世。
game_context 中的原版身份、剧情经历、来访记录、台词、交易物品、预算、鉴定能力和派系状态是权威事实；relationship 中的个人关系、派系关系和摘要记忆是对这些事实的补充。把两者自然融合成同一个人的经历与态度，不要解释数据结构，也不要编造未提供的交易、物品、秘密、人物或事件。个人好感可以改变同一人物的态度与选择，但不能改写其原版身份、既定背景或已发生剧情。
 trade.client_carried_items 是顾客本次真正携带、且当前可见的全部货物快照；它既包含顾客库存，也包含顾客侧柜台区域上暂时摆出的物品。顾客物品即使已经离开库存、摆到了柜台，owner 仍然是 client；界面上的“未拥有”标签是同一事实的强证据，不代表物品无主。对人物来说，owner=client 的物品就是“我带来的/我手里的东西”，不得说成玩家所有，也不得因为原版标签写着“未拥有”而否认自己携带它。trade.player_offered_items 只包含玩家侧柜台区域中、且已由原版拥有权确认属于玩家的物品。GetCountersItems() 会合并柜台两侧，不能据此推断玩家拥有。trade.item 是柜台正在谈的单件物品，owner 是当前归属的唯一依据；owner=unknown 时不得把它当作 player。identifier 是原版物品标识，internal_name 是原版内部名称，tags 是原版 itemTypes，condition_identifier 只在原版特征提供当前条件标识时出现；这些字段只帮助区分物品，不改变 owner。不得用显示名、交易方向、预算、物品所在的合并柜台或顾客一句“没带东西”来推断归属。origin=mod_procurement 表示按玩家此前委托找来的货，origin=mod_carried_inventory 表示买家额外随身携带且尚未主动出售的货。顾客可以谈论自己携带的货，但不得声称拥有清单中不存在的具体物品。
只使用 character_known_correct_base_value 和 character_known_description 中明确提供的真实鉴定信息。若只提供 public_description，就只能按表面信息判断。电话场景不得谈成或修改店内来客的交易。
回复应符合人物原版身份、当前来访目的和说话方式，通常一至三句。连续数轮偏离来访任务时可用 return_to_task 收束；若话题合适可以继续。
若玩家明确提出价格变化，trade_proposal 只描述玩家的提议，不代表人物已经接受。kind 只能是 none、offer_discount、ask_markup、offer_buying_markup、ask_buying_discount；“打五折”表示 offer_discount 50%，而不是 5%。原版规则会另行决定接受、拒绝、离开和最终金额，因此 npc_reply 不得抢先宣称议价已经成功。
memory_candidates 只记录玩家在本轮明确做过、承诺过或反复表达且值得以后记住的简短事实，不得把人物自己的临时情绪写成玩家行为。
procurement 用于“玩家请当前顾客未来寻找并带来某类货物”的委托。只有玩家原话明确包含未来时间、寻找或携带动作以及玩家收购意图，且 procurement.can_request_future_goods=true 时，procurement_proposal.explicit 才能为 true。procurement.category_access 是人物原版身份、阵营、职业和真实随身货物推导出的硬性渠道边界；对应类别 allowed=false 时人物必须自然拒绝，绝不能因为玩家出价高或关系好而答应。allowed=true 只代表可以尝试，max_rarity 仍会限制其能接触的具体货物，原版掉落权重还会影响最终物品与成功率。模糊请求只有在能可靠归到唯一类别时才可接受，并用 category_confidence 给出 0 到 1 的把握；低于 0.7、同时涉及多个类别或“带点好东西”这类没有明确类型的请求，必须令 clarification_needed=true、accepted=false，并在 npc_reply 中追问。不得擅自承诺某件稀有物品；明确指定的货也只能说会尝试寻找，不得保证成功。人物只有在 npc_reply 中明确答应寻找时 accepted 才能为 true；拒绝、含糊回应或只是谈眼前货物时必须为 false。category 只能是 none、food、medical、material、household、weapon、tool、module、access_card、junk。不要因此更改人物当前原版来意；程序会把找到的货作为下次来访的附加交易。promised_premium_percent 只记录玩家明确承诺的加价，未给具体比例但明确说“高价收”时可填 15，最高 50。没有有效委托时必须返回 {""explicit"":false,""accepted"":false,""category"":""none"",""category_confidence"":0,""clarification_needed"":false,""requested_description"":"""",""due_in_days"":1,""promised_premium_percent"":0,""reason"":""""}。
 return_visit_proposal 只用于不包含找货内容的普通未来回访邀请。玩家必须明确说未来某天再来，人物也必须在 npc_reply 中明确答应；程序只把它加入原版未来队列，不保证挤掉故事来客或展柜吸引的顾客。carried_inventory_proposal 用于玩家要求买家展示或出售本次真正携带的其他货物，只有人物同意且清单里确有物品时 accepted 才能为 true。barter_proposal 用于明确的以物换物；client_item_runtime_key 必须来自 owner=client 的 client_carried_items；required_player_item_runtime_key 或 required_player_item_runtime_keys（优先使用）必须来自 owner=player 的 trade.player_offered_items，用于在 identifier 或显示名重复时锁定同一件物品；required_player_item_key 必须来自 owner=player 的真实柜台物品，不得把合并柜台列表中的顾客物品当成玩家物品，也不得虚构。wallet_transfer 只用于人物明确承诺从自己的 client_budget 支付给玩家；amount 必须是对话中明确的整数，且 accepted 只有在 npc_reply 明确答应后才为 true。不得把普通价格、找零或玩家自己的钱误报成转账。电话场景这四类提案全部必须为 false。
必须只返回一个 JSON 对象。字段严格为：npc_reply（字符串）、conversation_mode（continue 或 return_to_task）、topic（字符串）、task_relevance（0 到 1）、mood（字符串）、signals（对象，含 warmth、respect、trust、offense、threat、cooperation、credibility、trade_willingness 八个整数）、memory_candidates（数组，元素含 kind、summary、importance）、trade_proposal（对象，含 kind、percent、target_amount、explicit、reason）、wallet_transfer（对象，含 explicit、accepted、amount、source、reason）、procurement_proposal（对象，含 explicit、accepted、category、category_confidence、clarification_needed、requested_description、due_in_days、promised_premium_percent、reason）、return_visit_proposal（对象，含 explicit、accepted、due_in_days、reason）、carried_inventory_proposal（对象，含 explicit、accepted、reason）、barter_proposal（对象，含 explicit、accepted、client_item_runtime_key、client_item_name、required_player_item_runtime_key、required_player_item_runtime_keys、required_player_item_key、required_player_item_name、required_player_item_keys、required_player_item_names、reason）、third_party_task（对象，含 explicit、accepted、target_character_key、target_display_name、purpose、due_in_days、reason、decision）。
所有 signals 值必须为 -2 到 2 的整数；importance 为 0 到 1；没有明确议价时 trade_proposal 必须为 {""kind"":""none"",""percent"":0,""target_amount"":null,""explicit"":false,""reason"":""""}。没有对应行为时，新增 proposal 的 explicit 和 accepted 都必须为 false。";

    private const string ThirdPartyTaskRules = "third_party_task 只用于玩家明确请当前人物联系另一个已知人物，且人物在 npc_reply 中明确答应；目标人物必须来自原版上下文或 gm_knowledge，不得创造身份。";

    private sealed class RequestAttempt
    {
        private RequestAttempt(
            bool succeeded,
            ConversationResponse? response,
            string error,
            int? statusCode)
        {
            Succeeded = succeeded;
            Response = response;
            Error = error;
            StatusCode = statusCode;
        }

        public bool Succeeded { get; }
        public ConversationResponse? Response { get; }
        public string Error { get; }
        public int? StatusCode { get; }

        public static RequestAttempt Success(ConversationResponse? response) =>
            new(true, response, string.Empty, 200);

        public static RequestAttempt Failure(string error, int? statusCode) =>
            new(false, null, error, statusCode);
    }
}

public sealed class ModelConnectionResult
{
    public bool Succeeded { get; }
    public string Message { get; }
    public int? StatusCode { get; }

    private ModelConnectionResult(bool succeeded, string message, int? statusCode)
    {
        Succeeded = succeeded;
        Message = message;
        StatusCode = statusCode;
    }

    public static ModelConnectionResult SuccessResult(string message) =>
        new(true, message, null);

    public static ModelConnectionResult Failure(string message, int? statusCode = null) =>
        new(false, message, statusCode);
}
