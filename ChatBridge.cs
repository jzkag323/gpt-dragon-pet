// ============================================================================
// ChatBridge.cs - 多源 AI 大脑统一对话桥接（零第三方包，原生 .NET HTTP + JSON）
// 核心原理：
//   1. 支持 4 类对话后端无缝切换：
//      - 在线 OpenAI 兼容预设（硅基流动 DeepSeek / 智谱免费 / DeepSeek / Gemini / OpenAI）
//      - 本机免费模型自动发现（探测 Ollama 11434 与 LM Studio 1234 端口列出可用模型）
//      - 本机 Codex CLI 免 Key 直连
//      - 自定义本地 Python HTTP 后门（POST /chat）
//   2. 从回复末尾解析 [mood:wave|curious|celebrate|sleep] 情绪标签联动桌宠动作；
//   3. 配置与多轮对话记忆自动持久化到 %LOCALAPPDATA%\GptDragonPet\。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

internal sealed class LocalModelInfo
{
    internal string Provider;
    internal string BaseUrl;
    internal string ModelId;
    internal string DisplayName;
}

internal sealed class ApiPreset
{
    internal readonly string Id;
    internal readonly string Title;
    internal readonly string Badge;
    internal readonly string BaseUrl;
    internal readonly string DefaultModel;
    internal readonly string KeyUrl;
    internal readonly string Description;

    internal ApiPreset(string id, string title, string badge, string baseUrl, string defaultModel, string keyUrl, string description)
    {
        Id = id;
        Title = title;
        Badge = badge;
        BaseUrl = baseUrl;
        DefaultModel = defaultModel;
        KeyUrl = keyUrl;
        Description = description;
    }
}

internal sealed class ChatConfig
{
    public string Provider = "openai";
    public string PresetId = "siliconflow";
    public string ApiBaseUrl = "https://api.siliconflow.cn/v1";
    public string ApiKey = "";
    public string Model = "deepseek-ai/DeepSeek-V3";
    public string LocalLlmBaseUrl = "http://127.0.0.1:11434/v1";
    public string LocalLlmModel = "";
    public string CodexModel = "";
    public string LocalEndpoint = "http://127.0.0.1:8765/chat";
    public int MaxHistoryTurns = 12;
    public string SystemPersona =
        "你是住在用户 Windows 桌面上的「GPT 龙娘」桌宠。你温柔、有点俏皮、爱陪伴，说话自然简短（通常 1~3 句话，像聊天一样，不要长篇大论除非用户要求详细解答）。" +
        "必要时可以在回复最末尾附带一个情绪动作标签，仅限 [mood:wave]、[mood:curious]、[mood:celebrate] 或 [mood:normal]。";
}

internal sealed class ChatMessage
{
    public string Role = "user";
    public string Content = "";
    public string Time = "";
}

internal sealed class ChatReply
{
    internal string Text;
    internal string Mood;
    internal string ProviderUsed;
}

internal sealed class ChatBridge
{
    internal static readonly ApiPreset[] Presets =
    {
        new ApiPreset("siliconflow", "硅基流动 · DeepSeek", "新用户免费额度 · 推荐",
            "https://api.siliconflow.cn/v1", "deepseek-ai/DeepSeek-V3", "https://cloud.siliconflow.cn/",
            "国内直连不卡顿，注册即送免费体验额度，直接畅聊满血 DeepSeek-V3 / R1。"),
        new ApiPreset("zhipu", "智谱 AI · GLM-4-Flash", "官方免费 API",
            "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash", "https://open.bigmodel.cn/",
            "国内大厂官方免费模型，注册登录即可生成免费 Key，几乎零门槛。"),
        new ApiPreset("deepseek", "DeepSeek 官方", "高性价比",
            "https://api.deepseek.com/v1", "deepseek-chat", "https://platform.deepseek.com/api_keys",
            "DeepSeek 官方开放平台，对话自然聪明、价格极低。"),
        new ApiPreset("gemini", "Google Gemini", "免费层额度",
            "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.5-flash", "https://aistudio.google.com/apikey",
            "Google AI Studio 提供每日免费额度，响应快速。"),
        new ApiPreset("openai", "OpenAI ChatGPT", "官方接口",
            "https://api.openai.com/v1", "gpt-4o-mini", "https://platform.openai.com/api-keys",
            "OpenAI 官方 GPT 系列接口（也可配合中转地址使用）。")
    };

    private static readonly Regex MoodTagRegex = new Regex(@"\[mood\s*:\s*([a-zA-Z_]+)\]", RegexOptions.IgnoreCase);
    private readonly CodexBridge codex;
    private readonly string dataDir;
    private readonly string configPath;
    private readonly string memoryPath;
    private readonly object sync = new object();
    private ChatConfig config;
    private List<ChatMessage> history;

    static ChatBridge()
    {
        try
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072 | (SecurityProtocolType)768;
        }
        catch (NotSupportedException) { }
    }

    internal ChatBridge(CodexBridge codex)
        : this(codex, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GptDragonPet"))
    {
    }

    internal ChatBridge(CodexBridge codex, string dataDir)
    {
        this.codex = codex;
        this.dataDir = dataDir;
        configPath = Path.Combine(dataDir, "chat_config.json");
        memoryPath = Path.Combine(dataDir, "chat_memory.json");
        config = LoadConfig();
        history = LoadHistory();
    }

    internal static ApiPreset FindPreset(string id)
    {
        foreach (var preset in Presets)
            if (String.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase)) return preset;
        return null;
    }

    internal ChatConfig GetConfig()
    {
        lock (sync)
        {
            return new ChatConfig
            {
                Provider = config.Provider,
                PresetId = config.PresetId,
                ApiBaseUrl = config.ApiBaseUrl,
                ApiKey = config.ApiKey,
                Model = config.Model,
                LocalLlmBaseUrl = config.LocalLlmBaseUrl,
                LocalLlmModel = config.LocalLlmModel,
                CodexModel = config.CodexModel,
                LocalEndpoint = config.LocalEndpoint,
                MaxHistoryTurns = config.MaxHistoryTurns,
                SystemPersona = config.SystemPersona
            };
        }
    }

    internal void SaveConfig(ChatConfig next)
    {
        if (next == null) return;
        lock (sync)
        {
            config = NormalizeConfig(next);
            WriteJsonFile(configPath, config);
        }
    }

    internal ChatMessage[] GetHistory()
    {
        lock (sync)
        {
            return history.Select(m => new ChatMessage { Role = m.Role, Content = m.Content, Time = m.Time }).ToArray();
        }
    }

    internal void ClearHistory()
    {
        lock (sync)
        {
            history.Clear();
            WriteJsonFile(memoryPath, history);
        }
    }

    internal bool IsConfiguredReady(bool codexAvailable)
    {
        var cfg = GetConfig();
        if (String.Equals(cfg.Provider, "codex", StringComparison.OrdinalIgnoreCase))
            return codexAvailable || (codex != null && codex.HasLocalCodex());
        if (String.Equals(cfg.Provider, "local_llm", StringComparison.OrdinalIgnoreCase))
            return !String.IsNullOrWhiteSpace(cfg.LocalLlmModel);
        if (String.Equals(cfg.Provider, "http", StringComparison.OrdinalIgnoreCase))
            return !String.IsNullOrWhiteSpace(cfg.LocalEndpoint);
        string key = (cfg.ApiKey ?? "").Trim();
        if (key.Length == 0) key = (Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "").Trim();
        return key.Length > 0 || IsLocalhost(cfg.ApiBaseUrl);
    }

    internal string ProviderDisplayName(string provider)
    {
        if (String.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase))
        {
            var cfg = GetConfig();
            var preset = FindPreset(cfg.PresetId);
            if (preset != null && String.Equals(cfg.ApiBaseUrl, preset.BaseUrl, StringComparison.OrdinalIgnoreCase))
                return preset.Title;
            return "在线 API";
        }
        if (String.Equals(provider, "local_llm", StringComparison.OrdinalIgnoreCase)) return "本机免费模型";
        if (String.Equals(provider, "http", StringComparison.OrdinalIgnoreCase)) return "本地 HTTP 后门";
        return "本机 Codex (免 Key)";
    }

    internal string ActiveBrainSummary()
    {
        var cfg = GetConfig();
        if (String.Equals(cfg.Provider, "local_llm", StringComparison.OrdinalIgnoreCase))
        {
            return String.IsNullOrWhiteSpace(cfg.LocalLlmModel)
                ? "本机免费模型（未选择模型）"
                : "本机免费模型 · " + cfg.LocalLlmModel;
        }
        if (String.Equals(cfg.Provider, "http", StringComparison.OrdinalIgnoreCase))
            return "本地 Python 后门 · " + cfg.LocalEndpoint;
        if (String.Equals(cfg.Provider, "codex", StringComparison.OrdinalIgnoreCase))
            return "本机 Codex（免 API Key）";
        var preset = FindPreset(cfg.PresetId);
        string label = preset != null ? preset.Title : "自定义 OpenAI 兼容 API";
        string key = (cfg.ApiKey ?? "").Trim();
        if (key.Length == 0) key = (Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "").Trim();
        return label + (key.Length > 0 || IsLocalhost(cfg.ApiBaseUrl) ? " · 已配置" : " · 待填入 Key");
    }

    internal LocalModelInfo[] DiscoverLocalModels(CancellationToken token)
    {
        var found = new List<LocalModelInfo>();
        var serializer = JsonValue.CreateSerializer();

        // 1. Ollama (default port 11434)
        string ollamaJson = TryGetJson("http://127.0.0.1:11434/api/tags", 1200, token);
        if (!String.IsNullOrWhiteSpace(ollamaJson))
        {
            var map = JsonValue.Parse(serializer, ollamaJson);
            foreach (var item in JsonValue.Array(map, "models"))
            {
                var entry = JsonValue.Map(item);
                string name = JsonValue.String(entry, "name");
                if (String.IsNullOrWhiteSpace(name)) continue;
                found.Add(new LocalModelInfo
                {
                    Provider = "Ollama",
                    BaseUrl = "http://127.0.0.1:11434/v1",
                    ModelId = name,
                    DisplayName = "Ollama · " + name
                });
            }
        }

        // 2. LM Studio (default port 1234)
        string lmJson = TryGetJson("http://127.0.0.1:1234/v1/models", 1200, token);
        if (!String.IsNullOrWhiteSpace(lmJson))
        {
            var map = JsonValue.Parse(serializer, lmJson);
            foreach (var item in JsonValue.Array(map, "data"))
            {
                var entry = JsonValue.Map(item);
                string id = JsonValue.String(entry, "id");
                if (String.IsNullOrWhiteSpace(id)) continue;
                found.Add(new LocalModelInfo
                {
                    Provider = "LM Studio",
                    BaseUrl = "http://127.0.0.1:1234/v1",
                    ModelId = id,
                    DisplayName = "LM Studio · " + id
                });
            }
        }

        return found.ToArray();
    }

    private static string TryGetJson(string url, int timeoutMs, CancellationToken token)
    {
        try
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Accept = "application/json";
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            using (token.Register(() => { try { request.Abort(); } catch { } }))
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
            {
                return reader.ReadToEnd();
            }
        }
        catch (WebException) { return null; }
        catch (IOException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    internal ChatReply Send(string userInput, CancellationToken token)
    {
        string text = (userInput ?? "").Trim();
        if (text.Length == 0) throw new InvalidOperationException("请输入想说的话。");

        ChatConfig current;
        List<ChatMessage> snapshot;
        lock (sync)
        {
            current = GetConfig();
            int maxMessages = Math.Max(2, current.MaxHistoryTurns * 2);
            snapshot = history.Count > maxMessages
                ? history.Skip(history.Count - maxMessages).ToList()
                : new List<ChatMessage>(history);
        }

        ChatReply reply;
        if (String.Equals(current.Provider, "openai", StringComparison.OrdinalIgnoreCase))
            reply = SendViaOpenAi(current, snapshot, text, token);
        else if (String.Equals(current.Provider, "local_llm", StringComparison.OrdinalIgnoreCase))
            reply = SendViaLocalLlm(current, snapshot, text, token);
        else if (String.Equals(current.Provider, "http", StringComparison.OrdinalIgnoreCase))
            reply = SendViaLocalHttp(current, snapshot, text, token);
        else
            reply = SendViaCodex(current, snapshot, text, token);

        reply.ProviderUsed = ProviderDisplayName(current.Provider);
        string now = DateTime.Now.ToString("HH:mm");
        lock (sync)
        {
            history.Add(new ChatMessage { Role = "user", Content = text, Time = now });
            history.Add(new ChatMessage { Role = "assistant", Content = reply.Text, Time = DateTime.Now.ToString("HH:mm") });
            int limit = Math.Max(10, current.MaxHistoryTurns * 4);
            if (history.Count > limit) history = history.Skip(history.Count - limit).ToList();
            WriteJsonFile(memoryPath, history);
        }
        return reply;
    }

    internal string TestConnection(ChatConfig testConfig, CancellationToken token)
    {
        ChatConfig cfg = NormalizeConfig(testConfig);
        if (String.Equals(cfg.Provider, "codex", StringComparison.OrdinalIgnoreCase))
        {
            string exe = codex.LocateExecutable(token);
            if (String.IsNullOrEmpty(exe))
                throw new InvalidOperationException("未找到本机 Codex。你可以切换到免费在线 API（如硅基流动 DeepSeek / 智谱免费版）或本机 Ollama 模型。");
            return "已检测到本机 Codex：" + exe;
        }
        if (String.Equals(cfg.Provider, "local_llm", StringComparison.OrdinalIgnoreCase))
        {
            var localLlmReply = SendViaLocalLlm(cfg, new List<ChatMessage>(), "请只回复：连接成功", token);
            return "本机模型连通正常，回复：" + TrimPreview(localLlmReply.Text, 40);
        }
        if (String.Equals(cfg.Provider, "openai", StringComparison.OrdinalIgnoreCase))
        {
            var reply = SendViaOpenAi(cfg, new List<ChatMessage>(), "请只回复：连接成功", token);
            return "API 连通正常，回复：" + TrimPreview(reply.Text, 40);
        }
        var localReply = SendViaLocalHttp(cfg, new List<ChatMessage>(), "ping", token);
        return "本地后门连通正常，回复：" + TrimPreview(localReply.Text, 40);
    }

    private ChatReply SendViaLocalLlm(ChatConfig cfg, List<ChatMessage> snapshot, string userInput, CancellationToken token)
    {
        if (String.IsNullOrWhiteSpace(cfg.LocalLlmModel))
            throw new InvalidOperationException("请先点击「扫描本机模型」并选择一个本地模型（如 deepseek-r1）。");
        var adapter = new ChatConfig
        {
            Provider = "openai",
            ApiBaseUrl = String.IsNullOrWhiteSpace(cfg.LocalLlmBaseUrl) ? "http://127.0.0.1:11434/v1" : cfg.LocalLlmBaseUrl,
            ApiKey = "local",
            Model = cfg.LocalLlmModel,
            MaxHistoryTurns = cfg.MaxHistoryTurns,
            SystemPersona = cfg.SystemPersona
        };
        return SendViaOpenAi(adapter, snapshot, userInput, token);
    }

    private ChatReply SendViaCodex(ChatConfig cfg, List<ChatMessage> snapshot, string userInput, CancellationToken token)
    {
        string executable = codex.LocateExecutable(token);
        if (String.IsNullOrEmpty(executable))
            throw new InvalidOperationException("未检测到本机 Codex。点击上方「选择大脑」即可改用硅基流动 DeepSeek、智谱免费版或本机 Ollama 模型。");

        string prompt = BuildCodexPrompt(cfg.SystemPersona, snapshot, userInput);
        Directory.CreateDirectory(dataDir);
        string outputFile = Path.Combine(dataDir, "codex-reply-" + Guid.NewGuid().ToString("N") + ".txt");

        var args = new StringBuilder();
        args.Append("exec --ephemeral --skip-git-repo-check --sandbox read-only --color never");
        if (!String.IsNullOrWhiteSpace(cfg.CodexModel))
            args.Append(" -m ").Append(QuoteArg(cfg.CodexModel.Trim()));
        args.Append(" -o ").Append(QuoteArg(outputFile)).Append(" -");

        var info = CodexRpc.StartInfo(executable, args.ToString());
        info.RedirectStandardInput = true;
        info.RedirectStandardError = true;
        info.StandardErrorEncoding = new UTF8Encoding(false);

        try
        {
            using (var process = Process.Start(info))
            {
                if (process == null) throw new InvalidOperationException("无法启动本机 Codex 进程。");
                var stdOutTask = process.StandardOutput.ReadToEndAsync();
                var stdErrTask = process.StandardError.ReadToEndAsync();
                try
                {
                    byte[] promptBytes = new UTF8Encoding(false).GetBytes(prompt);
                    process.StandardInput.BaseStream.Write(promptBytes, 0, promptBytes.Length);
                    process.StandardInput.BaseStream.Flush();
                    process.StandardInput.Close();
                }
                catch (IOException) { }

                var sw = Stopwatch.StartNew();
                while (!process.HasExited)
                {
                    if (token.IsCancellationRequested)
                    {
                        CodexRpc.Stop(process);
                        token.ThrowIfCancellationRequested();
                    }
                    if (sw.ElapsedMilliseconds > 90000)
                    {
                        CodexRpc.Stop(process);
                        throw new InvalidOperationException("本机 Codex 回复超时（90 秒）。建议切换到在线 API 获得更快响应。");
                    }
                    Thread.Sleep(60);
                }

                string replyText = "";
                if (File.Exists(outputFile))
                    replyText = File.ReadAllText(outputFile, new UTF8Encoding(false)).Trim();

                if (String.IsNullOrWhiteSpace(replyText))
                {
                    string stdOut = stdOutTask.Wait(1000) ? (stdOutTask.Result ?? "").Trim() : "";
                    string stdErr = stdErrTask.Wait(1000) ? (stdErrTask.Result ?? "").Trim() : "";
                    if (process.ExitCode != 0)
                    {
                        string detail = !String.IsNullOrWhiteSpace(stdErr) ? stdErr : stdOut;
                        throw new InvalidOperationException("Codex 执行失败：" + TrimPreview(detail, 200));
                    }
                    replyText = stdOut;
                }

                if (String.IsNullOrWhiteSpace(replyText))
                    throw new InvalidOperationException("Codex 没有返回有效文本，请检查登录状态或网络。");

                return ParseReply(replyText, null);
            }
        }
        finally
        {
            try { if (File.Exists(outputFile)) File.Delete(outputFile); } catch (IOException) { }
        }
    }

    private ChatReply SendViaOpenAi(ChatConfig cfg, List<ChatMessage> snapshot, string userInput, CancellationToken token)
    {
        string baseUrl = (cfg.ApiBaseUrl ?? "").Trim().TrimEnd('/');
        if (baseUrl.Length == 0) throw new InvalidOperationException("请先在「选择大脑」中填写 API 地址。");
        string endpoint = baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : baseUrl + "/chat/completions";

        string apiKey = (cfg.ApiKey ?? "").Trim();
        if (apiKey.Length == 0) apiKey = (Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "").Trim();
        if (apiKey.Length == 0 && !IsLocalhost(endpoint))
            throw new InvalidOperationException("还没有填写 API Key。点击上方「选择大脑」可以去官网免费领取并一键填入。");

        string model = String.IsNullOrWhiteSpace(cfg.Model) ? "deepseek-ai/DeepSeek-V3" : cfg.Model.Trim();
        var messages = new List<Dictionary<string, string>>();
        if (!String.IsNullOrWhiteSpace(cfg.SystemPersona))
            messages.Add(new Dictionary<string, string> { { "role", "system" }, { "content", cfg.SystemPersona } });
        foreach (var item in snapshot)
        {
            if (String.IsNullOrWhiteSpace(item.Content)) continue;
            messages.Add(new Dictionary<string, string> { { "role", item.Role == "assistant" ? "assistant" : "user" }, { "content", item.Content } });
        }
        messages.Add(new Dictionary<string, string> { { "role", "user" }, { "content", userInput } });

        var payload = new Dictionary<string, object>
        {
            { "model", model },
            { "messages", messages },
            { "temperature", 0.7 }
        };

        string responseBody = PostJson(endpoint, payload, apiKey, 60000, token);
        var serializer = JsonValue.CreateSerializer();
        var parsed = JsonValue.Parse(serializer, responseBody);
        var choices = JsonValue.Array(parsed, "choices");
        if (choices.Length == 0)
            throw new InvalidOperationException("API 返回内容缺少 choices 字段：" + TrimPreview(responseBody, 180));

        var first = JsonValue.Map(choices[0]);
        var msg = JsonValue.Map(JsonValue.Get(first, "message"));
        string content = JsonValue.String(msg, "content");
        if (String.IsNullOrWhiteSpace(content))
            content = JsonValue.String(first, "text");
        if (String.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("模型返回了空内容。");

        return ParseReply(content, null);
    }

    private ChatReply SendViaLocalHttp(ChatConfig cfg, List<ChatMessage> snapshot, string userInput, CancellationToken token)
    {
        string endpoint = (cfg.LocalEndpoint ?? "").Trim();
        if (endpoint.Length == 0) throw new InvalidOperationException("请先在「开发者与高级选项」中填写本地 HTTP 后门地址。");

        var historyItems = snapshot.Select(m => new Dictionary<string, string>
        {
            { "role", m.Role },
            { "content", m.Content },
            { "time", m.Time ?? "" }
        }).ToArray();

        var payload = new Dictionary<string, object>
        {
            { "message", userInput },
            { "persona", cfg.SystemPersona ?? "" },
            { "history", historyItems },
            { "source", "gpt_dragon_pet" }
        };

        string responseBody = PostJson(endpoint, payload, (cfg.ApiKey ?? "").Trim(), 60000, token);
        var serializer = JsonValue.CreateSerializer();
        var parsed = JsonValue.Parse(serializer, responseBody);
        if (parsed == null)
            return ParseReply(responseBody.Trim(), null);

        string replyText = JsonValue.String(parsed, "reply")
            ?? JsonValue.String(parsed, "content")
            ?? JsonValue.String(parsed, "text")
            ?? JsonValue.String(parsed, "message");
        string mood = JsonValue.String(parsed, "mood");

        if (String.IsNullOrWhiteSpace(replyText))
            throw new InvalidOperationException("本地后门返回的 JSON 缺少 reply/content 字段：" + TrimPreview(responseBody, 180));

        return ParseReply(replyText, mood);
    }

    private static string PostJson(string url, object payload, string bearerToken, int timeoutMs, CancellationToken token)
    {
        var serializer = JsonValue.CreateSerializer();
        byte[] bodyBytes = new UTF8Encoding(false).GetBytes(serializer.Serialize(payload));
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "POST";
        request.ContentType = "application/json; charset=utf-8";
        request.Accept = "application/json";
        request.Timeout = timeoutMs;
        request.ReadWriteTimeout = timeoutMs;
        request.ContentLength = bodyBytes.Length;
        if (!String.IsNullOrWhiteSpace(bearerToken))
            request.Headers["Authorization"] = "Bearer " + bearerToken;

        using (token.Register(() => { try { request.Abort(); } catch { } }))
        {
            try
            {
                using (var reqStream = request.GetRequestStream())
                    reqStream.Write(bodyBytes, 0, bodyBytes.Length);

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var respStream = response.GetResponseStream())
                using (var reader = new StreamReader(respStream, new UTF8Encoding(false)))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (WebException ex)
            {
                if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                string detail = ex.Message;
                if (ex.Response != null)
                {
                    try
                    {
                        using (var errResp = (HttpWebResponse)ex.Response)
                        using (var errStream = errResp.GetResponseStream())
                        using (var reader = new StreamReader(errStream, new UTF8Encoding(false)))
                        {
                            string body = reader.ReadToEnd();
                            detail = "HTTP " + (int)errResp.StatusCode + " " + TrimPreview(body, 220);
                        }
                    }
                    catch { }
                }
                throw new InvalidOperationException("请求失败：" + detail);
            }
        }
    }

    private static string BuildCodexPrompt(string persona, List<ChatMessage> snapshot, string userInput)
    {
        var sb = new StringBuilder();
        sb.AppendLine("以下是桌面宠物对话请求。请直接以角色口吻回复用户，不要执行命令，也不要修改任何文件。");
        if (!String.IsNullOrWhiteSpace(persona))
        {
            sb.AppendLine();
            sb.AppendLine("[角色人设]");
            sb.AppendLine(persona.Trim());
        }
        if (snapshot.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[最近对话记忆]");
            foreach (var item in snapshot)
            {
                string who = item.Role == "assistant" ? "龙娘" : "用户";
                sb.Append(who).Append("：").AppendLine(item.Content);
            }
        }
        sb.AppendLine();
        sb.AppendLine("[用户最新消息]");
        sb.AppendLine(userInput);
        return sb.ToString();
    }

    private static ChatReply ParseReply(string raw, string explicitMood)
    {
        string mood = (explicitMood ?? "").Trim().ToLowerInvariant();
        string cleaned = (raw ?? "").Trim();
        var match = MoodTagRegex.Match(cleaned);
        if (match.Success)
        {
            if (mood.Length == 0) mood = match.Groups[1].Value.Trim().ToLowerInvariant();
            cleaned = MoodTagRegex.Replace(cleaned, "").Trim();
        }
        if (mood.Length == 0) mood = "normal";
        return new ChatReply { Text = cleaned, Mood = mood };
    }

    private static string QuoteArg(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static bool IsLocalhost(string url)
    {
        Uri uri;
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
        return uri.IsLoopback;
    }

    internal static string TrimPreview(string text, int maxLength)
    {
        string single = String.Join(" ", (text ?? "").Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (single.Length <= maxLength) return single;
        return single.Substring(0, Math.Max(1, maxLength - 1)) + "…";
    }

    private ChatConfig LoadConfig()
    {
        try
        {
            if (!File.Exists(configPath))
            {
                var fresh = new ChatConfig();
                if (codex != null && codex.HasLocalCodex()) fresh.Provider = "codex";
                else fresh.Provider = "openai";
                return fresh;
            }
            string raw = File.ReadAllText(configPath, new UTF8Encoding(false));
            var serializer = JsonValue.CreateSerializer();
            var loaded = serializer.Deserialize<ChatConfig>(raw);
            return NormalizeConfig(loaded);
        }
        catch { return new ChatConfig(); }
    }

    private static ChatConfig NormalizeConfig(ChatConfig cfg)
    {
        var defaults = new ChatConfig();
        if (cfg == null) return defaults;
        if (String.IsNullOrWhiteSpace(cfg.Provider)) cfg.Provider = defaults.Provider;
        if (String.IsNullOrWhiteSpace(cfg.PresetId)) cfg.PresetId = defaults.PresetId;
        if (String.IsNullOrWhiteSpace(cfg.ApiBaseUrl)) cfg.ApiBaseUrl = defaults.ApiBaseUrl;
        if (String.IsNullOrWhiteSpace(cfg.Model)) cfg.Model = defaults.Model;
        if (String.IsNullOrWhiteSpace(cfg.LocalLlmBaseUrl)) cfg.LocalLlmBaseUrl = defaults.LocalLlmBaseUrl;
        if (cfg.LocalLlmModel == null) cfg.LocalLlmModel = "";
        if (String.IsNullOrWhiteSpace(cfg.LocalEndpoint)) cfg.LocalEndpoint = defaults.LocalEndpoint;
        if (cfg.MaxHistoryTurns < 1 || cfg.MaxHistoryTurns > 50) cfg.MaxHistoryTurns = defaults.MaxHistoryTurns;
        if (cfg.SystemPersona == null) cfg.SystemPersona = defaults.SystemPersona;
        if (cfg.ApiKey == null) cfg.ApiKey = "";
        if (cfg.CodexModel == null) cfg.CodexModel = "";
        return cfg;
    }

    private List<ChatMessage> LoadHistory()
    {
        try
        {
            if (!File.Exists(memoryPath)) return new List<ChatMessage>();
            string raw = File.ReadAllText(memoryPath, new UTF8Encoding(false));
            var serializer = JsonValue.CreateSerializer();
            var loaded = serializer.Deserialize<List<ChatMessage>>(raw);
            return loaded ?? new List<ChatMessage>();
        }
        catch { return new List<ChatMessage>(); }
    }

    private void WriteJsonFile(string path, object data)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            string temp = path + ".tmp";
            var serializer = JsonValue.CreateSerializer();
            File.WriteAllText(temp, serializer.Serialize(data), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
