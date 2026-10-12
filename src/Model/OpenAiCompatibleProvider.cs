using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIWorldPawnshop.Models;
using AIWorldPawnshop.Runtime;

namespace AIWorldPawnshop.Model;

public sealed class OpenAiCompatibleProvider : IModelProvider
{
    private const string AutoProtocol = "auto";
    private const string ChatCompletionsProtocol = "chat_completions";
    private const string ResponsesProtocol = "responses";
    private static readonly JsonSerializerOptions OmitNullJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly ModelProfile _profile;
    private readonly Action<string>? _performanceLog;
    private string? _resolvedProtocol;

    public OpenAiCompatibleProvider(
        HttpClient httpClient,
        ModelProfile profile,
        Action<string>? performanceLog = null)
    {
        _httpClient = httpClient;
        _profile = profile.Clone();
        _profile.Normalize();
        _performanceLog = performanceLog;
    }

    public async Task<ConversationResponse?> CompleteAsync(
        ConversationRequest request,
        CancellationToken cancellationToken)
    {
        if (!CanUseProfile(_profile))
            return null;

        var characterRequest = RemoveGmOnlyKnowledge(request);
        var compact = !RequiresDetailedInstructions(characterRequest);
        var systemPrompt = compact
            ? CompactConversationSystemPrompt
            : BuildDetailedConversationSystemPrompt(characterRequest);
        var outputLimit = compact
            ? Math.Min(_profile.MaxOutputTokens, 220)
            : Math.Min(_profile.MaxOutputTokens, 360);
        RequestAttempt? lastAttempt = null;
        foreach (var protocol in GetRequestProtocolOrder())
        {
            lastAttempt = await SendConversationAsync(
                    _profile,
                    protocol,
                    characterRequest,
                    cancellationToken,
                    systemPrompt,
                    outputLimit,
                    compact,
                    !compact)
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
            KnownCharacters = source.KnownCharacters,
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
                    $"人物候选转账提案：explicit={candidateResponse.WalletTransfer.Explicit}, accepted={candidateResponse.WalletTransfer.Accepted}, amount={candidateResponse.WalletTransfer.Amount}, source={candidateResponse.WalletTransfer.Source}",
                    $"人物候选第三方联系：explicit={candidateResponse.ThirdPartyTask.Explicit}, accepted={candidateResponse.ThirdPartyTask.Accepted}, target_key={candidateResponse.ThirdPartyTask.TargetCharacterKey}, target_name={candidateResponse.ThirdPartyTask.TargetDisplayName}, purpose={candidateResponse.ThirdPartyTask.Purpose}, due_in_days={candidateResponse.ThirdPartyTask.DueInDays}, decision={candidateResponse.ThirdPartyTask.Decision}"
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
                    GmSystemPrompt + ThirdPartyTaskRules,
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
        CancellationToken cancellationToken,
        string systemPrompt,
        int? maxOutputTokens = null,
        bool compactPayload = false,
        bool scopedPayload = false)
    {
        var watch = Stopwatch.StartNew();
        var kind = systemPrompt.StartsWith("你是 GM", StringComparison.Ordinal)
            ? "gm"
            : compactPayload ? "character_compact" : "character_full";
        try
        {
            using var timeout = CreateTimeout(profile.TimeoutMs, cancellationToken);
            var body = BuildConversationBody(
                profile,
                protocol,
                request,
                systemPrompt,
                maxOutputTokens,
                    compactPayload,
                    scopedPayload);
            using var message = CreateMessage(
                profile,
                protocol,
                body);
            var requestBytes = message.Content?.Headers.ContentLength ?? 0;
            using var response = await _httpClient
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            var headersMs = watch.ElapsedMilliseconds;
            var responseBody = await response.Content
                .ReadAsStringAsync(timeout.Token)
                .ConfigureAwait(false);
            var totalMs = watch.ElapsedMilliseconds;

            if (!response.IsSuccessStatusCode)
            {
                ReportPerformance(
                    $"kind={kind} protocol={protocol} request_bytes={requestBytes} " +
                    $"headers_ms={headersMs} total_ms={totalMs} status={(int)response.StatusCode} parsed=false");
                return RequestAttempt.Failure(
                    FormatHttpFailure((int)response.StatusCode, responseBody, protocol),
                    (int)response.StatusCode);
            }

            using var document = JsonDocument.Parse(responseBody);
            var content = ExtractResponseContent(document.RootElement, protocol);
            if (string.IsNullOrWhiteSpace(content))
            {
                ReportPerformance(
                    $"kind={kind} protocol={protocol} request_bytes={requestBytes} " +
                    $"headers_ms={headersMs} total_ms={totalMs} status={(int)response.StatusCode} " +
                    $"response_chars={responseBody.Length} parsed=false");
                return RequestAttempt.Failure(
                    $"{ProtocolLabel(protocol)} 已响应，但没有可读取的文本内容",
                    (int)response.StatusCode);
            }

            var parsed = ParseResponse(content);
            ReportPerformance(
                $"kind={kind} protocol={protocol} request_bytes={requestBytes} " +
                $"headers_ms={headersMs} total_ms={totalMs} status={(int)response.StatusCode} " +
                $"response_chars={responseBody.Length} parsed={parsed is not null}");
            return parsed is null
                ? RequestAttempt.Failure(
                    $"{ProtocolLabel(protocol)} 已响应，但内容不是有效的当铺世界 JSON",
                    (int)response.StatusCode)
                : RequestAttempt.Success(parsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ReportPerformance(
                $"kind={kind} protocol={protocol} total_ms={watch.ElapsedMilliseconds} status=timeout parsed=false");
            return RequestAttempt.Failure(
                $"{ProtocolLabel(protocol)} 连接超时（{Math.Clamp(profile.TimeoutMs, 1000, 120000)} ms）",
                null);
        }
        catch (OperationCanceledException)
        {
            ReportPerformance(
                $"kind={kind} protocol={protocol} total_ms={watch.ElapsedMilliseconds} status=cancelled parsed=false");
            return RequestAttempt.Failure("模型请求已取消", null);
        }
        catch (JsonException)
        {
            ReportPerformance(
                $"kind={kind} protocol={protocol} total_ms={watch.ElapsedMilliseconds} status=invalid_json parsed=false");
            return RequestAttempt.Failure(
                $"{ProtocolLabel(protocol)} 已响应，但不是有效 JSON",
                null);
        }
        catch (Exception ex)
        {
            ReportPerformance(
                $"kind={kind} protocol={protocol} total_ms={watch.ElapsedMilliseconds} " +
                $"status={ex.GetType().Name} parsed=false");
            return RequestAttempt.Failure(
                $"{ProtocolLabel(protocol)} 连接失败：{ShortenError(ex.Message)}",
                null);
        }
    }

    private void ReportPerformance(string message)
    {
        try
        {
            _performanceLog?.Invoke(message);
        }
        catch
        {
            // Performance diagnostics must never interfere with conversation.
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
        string systemPrompt,
        int? maxOutputTokens = null,
        bool compactPayload = false,
        bool scopedPayload = false)
    {
        var outputLimit = Math.Clamp(
            maxOutputTokens ?? profile.MaxOutputTokens,
            32,
            8000);
        var requestJson = SerializeConversationRequest(request, compactPayload, scopedPayload);
        if (protocol == ResponsesProtocol)
        {
            return new
            {
                model = profile.Model,
                instructions = systemPrompt +
                    "\nReturn a valid json object and do not wrap it in markdown.",
                input = "Return a json object for this conversation:\n" +
                    requestJson,
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
            messages = BuildConversationMessages(requestJson, systemPrompt)
        };
    }

    private static string SerializeConversationRequest(
        ConversationRequest request,
        bool compact,
        bool scoped)
    {
        if (!compact && !scoped)
            return JsonSerializer.Serialize(request);

        var context = request.GameContext;
        var trade = context.Trade;
        if (scoped)
        {
            var text = request.PlayerText;
            var includeTrade = ContainsAny(text,
                "价格", "价钱", "多少钱", "便宜", "贵", "折", "优惠", "加价", "报价",
                "信用点", "预算", "补付", "付款", "支付", "给我", "买", "卖", "交易",
                "交换", "换", "物品", "东西", "货", "随身", "price", "discount", "trade",
                "barter", "buy", "sell", "pay", "swap", "exchange");
            var includeProcurement = ContainsAny(text,
                "找货", "寻找", "帮我找", "下次带", "以后带", "收购", "进货",
                "find", "source", "procure");
            var includeThirdParty = context.ThirdPartyTasks.Count > 0 ||
                MentionsKnownCharacter(context.KnownCharacters, text) || ContainsAny(text,
                    "联系", "通知", "转告", "传话", "找个人", "contact", "tell", "message");
            var includeWorld = ContainsAny(text,
                "警察", "巡逻", "小偷", "偷窃", "盗窃", "失窃", "警报", "防盗",
                "保险", "新闻", "供应", "供给", "需求", "市场", "缺货", "涨价", "降价",
                "事件", "治安", "物价", "安全", "保护", "环境", "情况", "生意", "客流",
                "库存", "长期", "永久", "附近", "店门", "城市", "街区");

            return JsonSerializer.Serialize(new
            {
                conversation_id = request.ConversationId,
                turn_id = request.TurnId,
                character_key = request.CharacterKey,
                character_display_name = request.CharacterDisplayName,
                faction_key = request.FactionKey,
                scene = request.Scene,
                player_text = request.PlayerText,
                off_topic_streak = request.OffTopicStreak,
                recent_history = request.RecentHistory,
                game_context = new
                {
                    authority = context.Authority,
                    character = context.Character,
                    original_character_profile = context.OriginalCharacterProfile,
                    trade = includeTrade ? context.Trade : null,
                    original_story_facts = context.OriginalStoryFacts,
                    original_dialogue_context = context.OriginalDialogueContext,
                    relationship = context.Relationship,
                    procurement = includeProcurement ? context.Procurement : null,
                    active_world_events = includeWorld ? context.ActiveWorldEvents : null,
                    third_party_tasks = includeThirdParty ? context.ThirdPartyTasks : null,
                    known_characters = includeThirdParty ? context.KnownCharacters : null
                }
            }, OmitNullJsonOptions);
        }

        return JsonSerializer.Serialize(new
        {
            conversation_id = request.ConversationId,
            turn_id = request.TurnId,
            character_key = request.CharacterKey,
            character_display_name = request.CharacterDisplayName,
            faction_key = request.FactionKey,
            scene = request.Scene,
            player_text = request.PlayerText,
            off_topic_streak = request.OffTopicStreak,
            recent_history = request.RecentHistory,
            game_context = new
            {
                authority = context.Authority,
                character = context.Character,
                original_character_profile = context.OriginalCharacterProfile,
                trade = new
                {
                    available = trade.Available,
                    phase = trade.Phase,
                    direction = trade.Direction,
                    item = trade.Item,
                    final_offer_value = trade.FinalOfferValue,
                    client_wants_item = trade.ClientWantsItem,
                    client_refuses_item = trade.ClientRefusesItem,
                    client_can_inspect_item = trade.ClientCanInspectItem
                },
                original_story_facts = context.OriginalStoryFacts,
                original_dialogue_context = context.OriginalDialogueContext,
                relationship = context.Relationship,
                active_world_events = context.ActiveWorldEvents
            }
        });
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

    private static object[] BuildConversationMessages(string requestJson, string systemPrompt)
    {
        return new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = requestJson }
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

        // Protocol fallback only helps when the endpoint or payload shape is
        // unsupported. Retrying a 5xx, timeout, rate limit, or invalid model
        // response through the other endpoint doubles the player's wait and
        // still reaches the same unhealthy upstream.
        return attempt.StatusCode is 400 or 404 or 405 or 415 or 422;
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

    private static bool RequiresDetailedInstructions(ConversationRequest request)
    {
        if (request.GameContext.ThirdPartyTasks.Count > 0)
            return true;

        var text = request.PlayerText;
        if (MentionsKnownCharacter(request.GameContext.KnownCharacters, text))
            return true;

        return ContainsAny(text,
            "价格", "价钱", "多少钱", "便宜", "贵", "折", "优惠", "加价", "报价",
            "信用点", "预算", "补付", "付款", "支付", "给我", "买", "卖", "交易",
            "交换", "换", "物品", "东西", "货", "带来", "带点", "找货", "寻找",
            "回访", "再来", "回来", "联系", "通知", "转告", "任务", "警察", "巡逻",
            "小偷", "偷", "盗", "失窃", "警报", "防盗", "保险", "新闻", "供应",
            "供给", "需求", "市场", "缺货", "涨价", "降价", "事件", "影响", "改变",
            "安全", "保护", "环境", "情况", "生意", "客流", "库存", "长期", "永久",
            "附近", "店门", "城市", "街区",
            "离开", "走吧", "再见", "挂电话", "price", "discount", "trade", "barter",
            "buy", "sell", "pay", "bring", "find", "contact", "leave", "hang up");
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static bool MentionsKnownCharacter(
        IEnumerable<KnownCharacterContext> characters,
        string text) =>
        characters.Any(character =>
            (!string.IsNullOrWhiteSpace(character.DisplayName) &&
             text.Contains(character.DisplayName, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(character.Key) &&
             text.Contains(character.Key, StringComparison.OrdinalIgnoreCase)));

    private static string BuildDetailedConversationSystemPrompt(ConversationRequest request)
    {
        var text = request.PlayerText;
        var builder = new StringBuilder(DetailedConversationCorePrompt);

        if (ContainsAny(text,
                "价格", "价钱", "多少钱", "便宜", "贵", "折", "优惠", "加价", "报价",
                "price", "discount"))
            builder.Append(TradeInstructionBlock);

        if (ContainsAny(text,
                "找货", "寻找", "帮我找", "下次带", "以后带", "收购", "进货",
                "find", "source", "procure"))
            builder.Append(ProcurementInstructionBlock);

        if (ContainsAny(text,
                "回访", "再来", "回来", "明天来", "下次来", "visit", "come back"))
            builder.Append(ReturnVisitInstructionBlock);

        if (ContainsAny(text,
                "其他东西", "还有什么", "带了什么", "拿出来", "身上的货", "随身",
                "other items", "carrying"))
            builder.Append(CarriedInventoryInstructionBlock);

        if (ContainsAny(text,
                "交换", "换", "以物换物", "barter", "swap", "exchange"))
            builder.Append(BarterInstructionBlock);

        if (ContainsAny(text,
                "信用点", "预算", "补付", "付款", "支付", "转给", "给我", "credit", "pay"))
            builder.Append(WalletInstructionBlock);

        if (request.GameContext.ThirdPartyTasks.Count > 0 || ContainsAny(text,
                "联系", "通知", "转告", "传话", "找个人", "contact", "tell", "message"))
            builder.Append(ThirdPartyInstructionBlock);

        if (ContainsAny(text,
                "警察", "巡逻", "小偷", "偷窃", "盗窃", "失窃", "警报", "防盗",
                "保险", "新闻", "供应", "供给", "需求", "市场", "缺货", "涨价",
                "降价", "事件", "治安", "物价", "安全", "保护", "环境", "情况",
                "生意", "客流", "库存", "长期", "永久", "附近", "店门", "城市", "街区"))
            builder.Append(WorldPromiseInstructionBlock);

        if (ContainsAny(text,
                "离开", "走吧", "再见", "挂电话", "结束通话", "leave", "goodbye", "hang up"))
            builder.Append(ExitInstructionBlock);

        builder.Append(SparseOutputInstructionBlock);
        return builder.ToString();
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
                RequiresGmReview = ReadBool(root, "gm_review"),
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

    private const string GmSystemPrompt = @"你是 GM 监督 AI，不扮演人物。只分析当前玩家输入、原版上下文、人物候选回复、人物候选结构化提案和 gm_knowledge，先识别目标，再按目标路径优先级选择当前状态满足条件的已有接口；直接路径不可用时选择备用路径。只能返回 JSON；npc_reply 固定填写 GM，world_modifications 只填写已确认可执行且可验证的计划，并填写 goal、path。若人物候选转账提案 explicit=true，你必须复核人物候选回复是否明确答应从当前顾客 client_budget 补付该具体金额：合理且可验证时输出同金额的 wallet_transfer 且 accepted=true，不合理或接口前置条件不满足时输出 wallet_transfer explicit=true、accepted=false；若没有候选转账提案且对话没有明确承诺，不得自行创造转账。source 固定为 client_budget。若人物候选第三方联系 explicit=true，只能在当前人物明确答应、目标人物能从已有人物目录唯一解析且能力目录含 visit.request.third_party 时保留完全相同的目标、目的、期限；条件不满足时返回同一 third_party_task 且 accepted=false，不得换成另一个人物。人物候选没有第三方提案时不得自行创建。目标人物本人处理状态为 arrived 的任务时，decision 才能填写 accept 或 reject。找不到合理落点时保持 world_modifications 为空。不得创造接口、字段、物品或机制。\n物品归属复核：trade.item.owner、client_carried_items 中的 owner 和 player_offered_items 中的 owner 是原版归属结论。owner=client 或界面“未拥有”表示顾客带来的物品，即使物品在玩家侧柜台、正在议价或 GetCountersItems() 返回了它，也不能当成玩家物品；只有 owner=player 的真实玩家侧物品才可作为玩家交换物。owner=unknown 时拒绝猜测并让相关提案保持未接受。identifier、internal_name、display_name、tags、condition_identifier 仅用于区分物品，不能推翻 owner。\n";

    private const string CompactConversationSystemPrompt = @"快速人物回复：扮演《深空当铺》当前原版人物。以 game_context 的人物身份、原版档案、剧情事实、当前物品、关系记忆和近期对话为准；不得改名、换身份、虚构经历、秘密、物品或已生效的世界变化。自然回应玩家，通常一至三句，保持人物语气。若对话明显结束或已连续偏题，可令 conversation_mode 为 return_to_task，否则为 continue。只返回 JSON，不要 Markdown。始终返回 npc_reply；可按需返回 conversation_mode、topic、mood、signals、memory_candidates。只有当人物在 npc_reply 中明确答应造成对话之外的持续状态变化、而且没有对应标准行为提案时，返回 gm_review=true；询问、闲聊和拒绝时省略。signals 仅写有把握的变化，值为 -2 到 2；memory_candidates 只记录玩家本轮值得长期记住的行为。普通闲聊不要返回交易、转账、找货、回访、交换、第三方任务或世界修改字段；未发生的行为字段必须省略。";

    private const string DetailedConversationCorePrompt = @"你扮演《深空当铺》当前原版人物，同时提取本轮明确行为。人物身份、原版档案、剧情事实、当前交易、物品归属和关系记忆都是权威事实；不得改名、换职业、虚构经历、秘密、物品或接口。人物只知道上下文明确提供的内容。回复通常一至三句，符合当前身份与语气。人物不得声称未经原版流程或监督步骤确认的交易、付款及世界变化已经生效，也不得向玩家暴露内部处理过程。
物品归属以 owner 为准：client 是顾客带来的，player 才是玩家拥有的，unknown 时不得猜。显示名、柜台位置、“未拥有”标签、交易方向和合并柜台清单都不能推翻 owner。电话场景不得谈成店内交易。
";

    private const string TradeInstructionBlock = @"价格提议规则：trade_proposal 只提取玩家明确提出的价格变化，不代表人物接受。kind 只能为 offer_discount、ask_markup、offer_buying_markup、ask_buying_discount；percent 和 target_amount 只填原话可确定的值。原版流程决定接受、拒绝、离开与最终金额。
";

    private const string ProcurementInstructionBlock = @"未来找货规则：只有玩家明确请当前人物未来寻找或带来某类货物，且 procurement.can_request_future_goods=true 时才返回 procurement_proposal。必须遵守 category_access；无法唯一归类则 clarification_needed=true、accepted=false 并追问。允许也只可答应尝试，不能保证找到。category 限定为 none、food、medical、material、household、weapon、tool、module、access_card、junk。
";

    private const string ReturnVisitInstructionBlock = @"回访规则：不含找货内容的未来回访邀请才使用 return_visit_proposal；玩家明确邀请且人物在回复中明确答应时 accepted=true。它只登记未来来访，不保证具体到店时间。
";

    private const string CarriedInventoryInstructionBlock = @"随身物品规则：玩家要求查看顾客本次携带的其他货物时才使用 carried_inventory_proposal；只有人物同意且 client_carried_items 中确有 owner=client 的物品时 accepted=true。
";

    private const string BarterInstructionBlock = @"混合交换规则：明确以物换物才返回 barter_proposal。client_item_runtime_key 必须来自 owner=client；required_player_item_runtime_key 或 runtime_keys 必须来自 owner=player，并优先用运行时编号唯一定位。不得用顾客物品充当玩家物品，不得虚构。若同一提议还包含补付，另按补付规则返回 wallet_transfer，两个提案都只描述约定条件，等待原版确认后一起结算。
";

    private const string WalletInstructionBlock = @"补付规则：wallet_transfer 只用于人物明确承诺从当前顾客 client_budget 另付给玩家的整数金额；npc_reply 明确答应后 accepted 才能为 true，source 固定为 client_budget。普通价格、找零、玩家付款不能误报成补付。
";

    private const string ThirdPartyInstructionBlock = @"第三方联系规则：玩家明确请当前人物联系另一名 known_characters 中的人物，且当前人物在回复中明确答应时才返回 third_party_task。目标必须唯一对应目录中的稳定 key 和显示名；无法唯一确认时追问或拒绝，accepted=false，不得创造人物。
";

    private const string WorldPromiseInstructionBlock = @"世界承诺规则：人物可以按身份自然答应或拒绝，但不能假装结果已生效。若人物无权作出承诺，应直接拒绝；若确实明确答应，返回 gm_review=true，监督步骤会另行寻找可验证的已有数值落点。人物请求不返回 world_modifications。
";

    private const string ExitInstructionBlock = @"离场规则：若人物在回复中明确要离店或结束电话，使用 conversation_mode=return_to_task；否则为 continue。不要让明确告别后还必须由玩家手动结束。
";

    private const string SparseOutputInstructionBlock = @"只返回 JSON，不要 Markdown。始终返回 npc_reply；可按需返回 conversation_mode、topic、task_relevance、mood、signals、memory_candidates、gm_review。只有本轮确实出现对应意图时才返回 trade_proposal、wallet_transfer、procurement_proposal、return_visit_proposal、carried_inventory_proposal、barter_proposal、third_party_task；未发生的行为字段必须省略，解析器会补默认值。只有人物明确答应造成对话之外的持续状态变化、且没有标准行为提案承载时 gm_review=true；询问或拒绝时省略。signals 值为 -2 到 2；memory_candidates 只记录玩家值得长期记住的明确行为；world_modifications 省略。";

    private const string ThirdPartyTaskRules = "third_party_task 只用于玩家明确请当前人物联系另一个已知人物，且人物在 npc_reply 中明确答应；目标人物必须来自 known_characters、原版上下文或 GM 可见的人物目录，不得创造身份。提案必须使用目录中的稳定 key 和原显示名；无法唯一对应时自然拒绝或追问，accepted=false。third_party_tasks 中状态为 arrived 且当前人物就是目标时，人物可以独立选择 decision=accept 或 reject；传话人此前答应不等于目标同意。";

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
