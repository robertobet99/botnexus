using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Gateway.Tests.TestInfrastructure;

/// <summary>
/// Deterministic <see cref="IApiProvider"/> whose behaviour is a per-call script. Call N consumes
/// step N, so a test states "a tool call, then an in-band failure, then a success" and gets exactly
/// that from the real agent loop, with no network, clock or randomness involved.
/// </summary>
/// <remarks>
/// <para>
/// Every call captures the <see cref="Context"/> the loop built, so tests can assert on what the
/// model was actually shown (for example, the replacement turn after a failed run).
/// </para>
/// <para>
/// Failure shapes are in-band: an <see cref="ErrorEvent"/> pushed into the stream, which is how the
/// real Responses parser reports a provider error frame. Which core lane a shape takes is decided by
/// its text, exactly as in production (<c>TransientErrorClassifier</c>), so the shapes below are
/// chosen by classification rather than by speed alone:
/// <list type="bullet">
/// <item><description><see cref="CutAfterOutput"/> - classified neither transient nor exhausted and
/// delivered after output: the core RETURNS an error message.</description></item>
/// <item><description><see cref="Rejected"/> - same classification, no output: the core RETURNS an
/// error message immediately.</description></item>
/// <item><description><see cref="QuotaExhausted"/> - exhaustion lane: one attempt, then the core
/// THROWS and converts the run to Failed. In production this also suspends the provider for the
/// container, so never use it where a replacement in the same container must succeed.</description></item>
/// <item><description><see cref="TransientServiceUnavailable"/> - the real incident shape: transient,
/// no output, so the loop spends its four attempts and about 3.5 seconds of real backoff before the
/// run fails.</description></item>
/// </list>
/// </para>
/// </remarks>
internal sealed class ScriptedLlmProvider : IApiProvider
{
    internal const string ApiName = "scripted-recovery-api";
    internal const string ProviderName = "scripted-recovery-provider";
    internal const string ModelId = "scripted-recovery-model";

    /// <summary>Text the model emitted before <see cref="CutAfterOutput"/> cut the stream.</summary>
    internal const string PartialOutput = "partial-output-alpha";

    /// <summary>Error delivered after output; classified as neither transient nor exhausted.</summary>
    internal const string CutAfterOutput = "scripted-fault-alpha: the stream was cut after partial output";

    /// <summary>Error with no output; classified as neither transient nor exhausted.</summary>
    internal const string Rejected = "scripted-fault-bravo: the request was rejected by the model backend";

    /// <summary>Error with no output; classified as quota exhaustion.</summary>
    internal const string QuotaExhausted = "insufficient_quota: scripted exhaustion for the test account";

    /// <summary>Error with no output in the real provider format (<c>code: message</c>); classified as transient.</summary>
    internal const string TransientServiceUnavailable =
        "service_unavailable: The service is temporarily unavailable. Try again later.";

    private static readonly TimeSpan CallLiveness = TimeSpan.FromSeconds(60);

    private readonly object _sync = new();
    private readonly IReadOnlyList<ScriptStep> _steps;
    private readonly List<ProviderCall> _calls = [];
    private readonly Dictionary<int, TaskCompletionSource<ProviderCall>> _entered = [];

    /// <summary>Creates a provider that serves <paramref name="steps"/> in call order.</summary>
    internal ScriptedLlmProvider(IReadOnlyList<ScriptStep> steps) => _steps = steps;

    /// <inheritdoc />
    public string Api => ApiName;

    /// <summary>Number of model calls made so far, including calls the script failed.</summary>
    internal int CallCount
    {
        get { lock (_sync) return _calls.Count; }
    }

    /// <summary>The captured calls in order; the first call is index zero.</summary>
    internal IReadOnlyList<ProviderCall> Calls
    {
        get { lock (_sync) return [.. _calls]; }
    }

    /// <summary>The model registered for this provider.</summary>
    internal static LlmModel CreateModel() => new(
        Id: ModelId,
        Name: "Scripted Recovery Model",
        Api: ApiName,
        Provider: ProviderName,
        BaseUrl: "https://scripted.test.invalid",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 128_000,
        MaxTokens: 32_000);

    /// <summary>Registers the provider and its model so agents can point at <see cref="ModelId"/>.</summary>
    internal void Register(ApiProviderRegistry apiProviders, ModelRegistry models)
    {
        apiProviders.Register(this, sourceId: ApiName);
        models.Register(ProviderName, CreateModel());
    }

    /// <summary>
    /// Completes with the call numbered <paramref name="callNumber"/> (one-based) as soon as the model
    /// is invoked for it. A deadline turns a missing call into a readable failure, not a hang.
    /// </summary>
    internal Task<ProviderCall> AwaitCallAsync(int callNumber)
        => EnteredSignal(callNumber).Task.WaitAsync(CallLiveness);

    /// <inheritdoc />
    public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
        => StreamSimple(model, context);

    /// <inheritdoc />
    public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
    {
        ProviderCall call;
        ScriptStep? step;
        lock (_sync)
        {
            call = new ProviderCall(_calls.Count + 1, context);
            _calls.Add(call);
            step = call.Number <= _steps.Count ? _steps[call.Number - 1] : null;
        }

        EnteredSignal(call.Number).TrySetResult(call);

        // An unscripted call is a failing test, not a silent success: it surfaces in the run's own
        // failure text and in CallCount.
        if (step is null)
            throw new InvalidOperationException($"The scripted provider has no step for call {call.Number}.");

        var stream = new LlmStream();
        step.Emit(stream, call);
        return stream;
    }

    private TaskCompletionSource<ProviderCall> EnteredSignal(int callNumber)
    {
        lock (_sync)
        {
            if (!_entered.TryGetValue(callNumber, out var signal))
            {
                signal = new TaskCompletionSource<ProviderCall>(TaskCreationOptions.RunContinuationsAsynchronously);
                _entered[callNumber] = signal;
            }

            return signal;
        }
    }
}

/// <summary>One scripted model response. Build with the static factories, never directly.</summary>
internal sealed class ScriptStep
{
    private readonly Action<LlmStream, ProviderCall> _emit;

    private ScriptStep(Action<LlmStream, ProviderCall> emit) => _emit = emit;

    internal void Emit(LlmStream stream, ProviderCall call) => _emit(stream, call);

    /// <summary>A normal completion with <paramref name="text"/>.</summary>
    internal static ScriptStep Text(string text) => new((stream, _) =>
    {
        var message = Message([new TextContent(text)], StopReason.Stop, error: null);
        stream.Push(new StartEvent(message));
        PushText(stream, 0, text, message);
        stream.Push(new DoneEvent(StopReason.Stop, message));
        stream.End(message);
    });

    /// <summary>A tool call that the loop executes before calling the model again.</summary>
    internal static ScriptStep ToolCall(string toolName, string toolCallId) => new((stream, _) =>
    {
        var call = new ToolCallContent(toolCallId, toolName, new Dictionary<string, object?>());
        var message = Message([call], StopReason.ToolUse, error: null);
        stream.Push(new StartEvent(message));
        stream.Push(new ToolCallStartEvent(0, message, toolCallId, toolName));
        stream.Push(new ToolCallDeltaEvent(0, "{}", message, toolCallId, toolName));
        stream.Push(new ToolCallEndEvent(0, call, message));
        stream.Push(new DoneEvent(StopReason.ToolUse, message));
        stream.End(message);
    });

    /// <summary>Some visible output, then an in-band error (the error arrives after output).</summary>
    internal static ScriptStep FailAfterOutput(string partialText, string error) => new((stream, _) =>
    {
        var partial = Message([new TextContent(partialText)], StopReason.Stop, error: null);
        var failed = Message([new TextContent(partialText)], StopReason.Error, error);
        stream.Push(new StartEvent(partial));
        PushText(stream, 0, partialText, partial, includeEnd: false);
        stream.Push(new ErrorEvent(StopReason.Error, failed));
        stream.End(failed);
    });

    /// <summary>An in-band error with no output at all.</summary>
    internal static ScriptStep Fail(string error) => new((stream, _) =>
    {
        var failed = Message([], StopReason.Error, error);
        stream.Push(new StartEvent(Message([], StopReason.Stop, error: null)));
        stream.Push(new ErrorEvent(StopReason.Error, failed));
        stream.End(failed);
    });

    /// <summary>A call that never answers; the run only ends when it is aborted.</summary>
    internal static ScriptStep Hang() => new((_, _) => { });

    /// <summary>Answers with <paramref name="next"/> only once <paramref name="gate"/> completes.</summary>
    internal static ScriptStep HoldThen(Task gate, ScriptStep next) => new((stream, call) =>
        _ = Task.Run(async () =>
        {
            await gate.ConfigureAwait(false);
            next.Emit(stream, call);
        }));

    /// <summary>Chooses the response from what the model was shown, like a model that reads its prompt.</summary>
    internal static ScriptStep Respond(Func<ProviderCall, ScriptStep> choose)
        => new((stream, call) => choose(call).Emit(stream, call));

    /// <summary>The same step <paramref name="times"/> times in a row (for example, one per retry attempt).</summary>
    internal static IEnumerable<ScriptStep> Repeat(ScriptStep step, int times) => Enumerable.Repeat(step, times);

    private static void PushText(
        LlmStream stream, int index, string text, AssistantMessage message, bool includeEnd = true)
    {
        stream.Push(new TextStartEvent(index, message));
        stream.Push(new TextDeltaEvent(index, text, message));
        if (includeEnd)
            stream.Push(new TextEndEvent(index, text, message));
    }

    private static AssistantMessage Message(IReadOnlyList<ContentBlock> content, StopReason reason, string? error)
        => new(
            Content: content,
            Api: ScriptedLlmProvider.ApiName,
            Provider: ScriptedLlmProvider.ProviderName,
            ModelId: ScriptedLlmProvider.ModelId,
            Usage: Usage.Empty(),
            StopReason: reason,
            ErrorMessage: error,
            ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}

/// <summary>What the model was shown on one call, captured at the provider boundary.</summary>
internal sealed class ProviderCall
{
    private readonly IReadOnlyList<Message> _messages;
    private readonly IReadOnlyList<string> _toolResultTexts;

    internal ProviderCall(int number, Context context)
    {
        Number = number;
        _messages = [.. context.Messages];
        UserTexts =
        [
            .. _messages.OfType<UserMessage>()
                .Where(message => !message.IsRequestOnlyContext)
                .Select(TextOf)
        ];
        _toolResultTexts =
        [
            .. _messages.OfType<ToolResultMessage>()
                .Select(message => string.Concat(message.Content.OfType<TextContent>().Select(block => block.Text)))
        ];
    }

    /// <summary>One-based position of the call.</summary>
    internal int Number { get; }

    /// <summary>Conversational user messages in order. Host-supplied request-only context is excluded.</summary>
    internal IReadOnlyList<string> UserTexts { get; }

    /// <summary>The prompt this call is answering: the last conversational user message.</summary>
    internal string? FinalUserText => UserTexts.Count == 0 ? null : UserTexts[^1];

    /// <summary>
    /// How many tool results the context carries. A warm agent still holds the interrupted turn's; an agent
    /// rebuilt from the transcript holds none, because the transcript replays only user and assistant text.
    /// </summary>
    internal int ToolResultCount => _toolResultTexts.Count;

    /// <summary>True when any user, assistant or tool-result text in the context contains <paramref name="text"/>.</summary>
    internal bool ContextContains(string text)
        => UserTexts.Any(candidate => candidate.Contains(text, StringComparison.Ordinal))
            || _toolResultTexts.Any(candidate => candidate.Contains(text, StringComparison.Ordinal))
            || _messages.OfType<AssistantMessage>().Any(message => message.Content.OfType<TextContent>()
                .Any(block => block.Text.Contains(text, StringComparison.Ordinal)));

    private static string TextOf(UserMessage message)
        => message.Content.Text
            ?? string.Concat((message.Content.Blocks ?? []).OfType<TextContent>().Select(block => block.Text));
}
