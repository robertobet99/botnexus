using System.Text;
using System.Text.Json;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Events;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Activity;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Extensions;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.TestInfrastructure;

/// <summary>What a harness should script and start.</summary>
/// <param name="Script">The model's responses, one per call, in call order.</param>
internal sealed record RecoveryIngressOptions(IReadOnlyList<ScriptStep> Script)
{
    /// <summary>
    /// Subscribes the real interrupted-turn recovery service to live lifecycle events. The startup scan
    /// is never run implicitly; tests that need it call it, so a scan only happens when a test says so.
    /// </summary>
    internal bool RecoveryEnabled { get; init; }

    /// <summary>Overrides <c>GatewayOptions.MaxAutoReplayAttempts</c> when set.</summary>
    internal int? MaxAutoReplayAttempts { get; init; }

    /// <summary>
    /// Makes agent construction fail the way an infrastructure fault would, before any model call, so a
    /// test can exercise the host's generic failure handler rather than a settled run.
    /// </summary>
    internal bool FailAgentCreation { get; init; }
}

/// <summary>The conversation and session an ingress call landed on.</summary>
internal sealed record IngressReceipt(ConversationId ConversationId, SessionId SessionId);

/// <summary>
/// In-process component integration from the real inbound producers to the model boundary: the real
/// <see cref="ConversationMessagesController"/> and <see cref="ConversationTool"/> post into the real
/// orchestrator, <see cref="GatewayHost"/>, isolation strategy, agent loop, stores and (optionally) the
/// real interrupted-turn recovery service. Only the model is scripted and only tool effects are synthetic.
/// It is not HTTP, a wire protocol or a running gateway process, and it never starts a hosted service.
/// </summary>
/// <remarks>
/// <para>
/// Composition uses the no-config <c>AddBotNexusGateway()</c> overload over a unique temp home, so nothing
/// here touches the process-global store identity or a user directory. The home and the data path are
/// both pinned to that directory: the data path would otherwise honour <c>BOTNEXUS_DATA_DIR</c> and could
/// point a store at real data. Every hosted service is removed; the recovery service is constructed
/// explicitly and subscribed by hand. Auxiliary model calls (conversation titling) are disabled so the
/// scripted call count is exactly the turn's own calls.
/// </para>
/// <para>
/// A fresh provider suspension registry is registered per harness, as the platform composition does for a
/// container, so the exhaustion lane behaves as in production (it remembers the exhausted provider). The
/// provider recovery coordinator is deliberately not registered: its circuit breaker adds process-wide
/// probe timing that scripted tests must not depend on. A scenario that needs a replacement to succeed
/// after an exhaustion failure must not use that shape, because the provider stays suspended.
/// </para>
/// <para>
/// Completion is observed through an explicit signal, the gateway's per-turn terminal activity
/// (<see cref="AwaitTurnsAsync"/>), never through disposal: disposal completes queue writers and can
/// change admission. Assert first, dispose last. Disposal itself never abandons the graph: it stops the
/// agent handles (the supervisor is not disposable, so container disposal would leave them running),
/// drains the queues, disposes the container, and only then deletes the home. A step that does not finish
/// fails the test naming the step, and the home is left in place.
/// </para>
/// </remarks>
internal sealed class RecoveryIngressHarness : IAsyncDisposable
{
    private static readonly TimeSpan Liveness = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DisposalDeadline = TimeSpan.FromSeconds(30);
    private static readonly AgentId WorkerAgentId = AgentId.From("worker-agent");
    private static readonly AgentId CallerAgentId = AgentId.From("coordinator-agent");

    private readonly IHost _host;
    private readonly GatewayHost _gateway;
    private readonly string _tempRoot;
    private readonly ConversationMessagesController _controller;
    private readonly ConversationTool _conversationTool;
    private readonly List<SessionId> _knownSessions = [];
    private int _disposed;

    private RecoveryIngressHarness(
        IHost host,
        string tempRoot,
        ScriptedLlmProvider provider,
        SideEffectToolContributor effects,
        RecordingOrchestrator orchestrator,
        RecordingActivityBroadcaster activities,
        RecordingLifecycleSubscriber lifecycle,
        RecordingDeliverer deliverer,
        InterruptedTurnNotificationService? recovery,
        ConversationMessagesController controller,
        ConversationTool conversationTool)
    {
        _host = host;
        _tempRoot = tempRoot;
        Provider = provider;
        Effects = effects;
        Orchestrator = orchestrator;
        Activities = activities;
        Lifecycle = lifecycle;
        Deliverer = deliverer;
        Recovery = recovery;
        _controller = controller;
        _conversationTool = conversationTool;
        _gateway = host.Services.GetRequiredService<GatewayHost>();
        Supervisor = host.Services.GetRequiredService<IAgentSupervisor>();
        SessionStore = host.Services.GetRequiredService<ISessionStore>();
        Saves = SessionStore as RecordingSessionStore
            ?? throw new InvalidOperationException("The harness session store must be the recording decorator.");
        ConversationStore = host.Services.GetRequiredService<IConversationStore>();
    }

    /// <summary>The agent that owns every conversation this harness creates.</summary>
    internal AgentId AgentId => WorkerAgentId;

    /// <summary>The unique directory that is both the gateway home and its data path.</summary>
    internal string HomePath => _tempRoot;

    internal ScriptedLlmProvider Provider { get; }

    internal SideEffectToolContributor Effects { get; }

    /// <summary>Records everything producers hand the real orchestrator.</summary>
    internal RecordingOrchestrator Orchestrator { get; }

    internal RecordingActivityBroadcaster Activities { get; }

    internal RecordingLifecycleSubscriber Lifecycle { get; }

    /// <summary>Records what the host offers the outbound fan-out after each run.</summary>
    internal RecordingDeliverer Deliverer { get; }

    /// <summary>The real recovery service, or null when the harness was started without it.</summary>
    internal InterruptedTurnNotificationService? Recovery { get; }

    internal IAgentSupervisor Supervisor { get; }

    internal ISessionStore SessionStore { get; }

    /// <summary>
    /// The same store as <see cref="SessionStore"/>, seen as the recording decorator: completed fenced saves,
    /// in order, and the switches that make the next one fail.
    /// </summary>
    internal RecordingSessionStore Saves { get; }

    internal IConversationStore ConversationStore { get; }

    /// <summary>Starts a harness. Dispose it when the test ends.</summary>
    internal static async Task<RecoveryIngressHarness> StartAsync(RecoveryIngressOptions options)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "botnexus-recovery-ingress", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        IHost? host = null;
        try
        {
            var effects = new SideEffectToolContributor(Path.Combine(tempRoot, "effects", "applied.log"));
            var provider = new ScriptedLlmProvider(options.Script);
            var deliverer = new RecordingDeliverer();
            var clock = new HarnessClock();
            host = BuildHost(options, tempRoot, provider, effects, deliverer, clock);

            var container = host.Services;
            _ = container.GetRequiredService<LlmClient>();

            var registry = container.GetRequiredService<IAgentRegistry>();
            registry.Register(new AgentDescriptor
            {
                AgentId = WorkerAgentId,
                DisplayName = WorkerAgentId.Value,
                ModelId = ScriptedLlmProvider.ModelId,
                ApiProvider = ScriptedLlmProvider.ProviderName,
                SystemPrompt = "You are a deterministic test agent.",
                IsolationStrategy = "in-process"
            });

            var orchestrator = (RecordingOrchestrator)container.GetRequiredService<IInboundMessageOrchestrator>();
            var activities = (RecordingActivityBroadcaster)container.GetRequiredService<IActivityBroadcaster>();
            var conversations = container.GetRequiredService<IConversationStore>();
            var sessions = container.GetRequiredService<ISessionStore>();

            // Subscribe the recorder first so it sees each event before recovery mutates the session.
            var lifecycle = new RecordingLifecycleSubscriber(container.GetRequiredService<SessionLifecycleEvents>(), clock);
            InterruptedTurnNotificationService? recovery = null;
            if (options.RecoveryEnabled)
            {
                recovery = container.GetRequiredService<InterruptedTurnNotificationService>();
                await recovery.StartAsync(CancellationToken.None);
            }

            var controller = new ConversationMessagesController(
                conversations,
                sessions,
                container.GetRequiredService<IConversationDispatcher>(),
                orchestrator,
                registry,
                NullLogger<ConversationMessagesController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            var conversationTool = new ConversationTool(
                conversations,
                CallerAgentId,
                accessLevel: ConversationAccessLevel.All,
                sessionStore: sessions,
                messageOrchestrator: orchestrator,
                conversationRouter: container.GetRequiredService<IConversationRouter>());

            return new RecoveryIngressHarness(
                host, tempRoot, provider, effects, orchestrator, activities, lifecycle, deliverer, recovery, controller, conversationTool);
        }
        catch
        {
            // Nothing has run yet, so there is no live work to stop. The host (when it was built) and the
            // home are released, and the start failure being rethrown is the one worth reporting.
            try
            {
                await DisposeHostAsync(host).ConfigureAwait(false);
            }
            catch
            {
                // Superseded by the original failure below.
            }

            TryDeleteDirectory(tempRoot);
            throw;
        }
    }

    private static IHost BuildHost(
        RecoveryIngressOptions options,
        string tempRoot,
        ScriptedLlmProvider provider,
        SideEffectToolContributor effects,
        RecordingDeliverer deliverer,
        HarnessClock clock)
    {
        // Defaults are disabled so the builder creates no configuration sources. The default sources
        // (appsettings files, user secrets) watch the file system for changes, and each watcher holds an OS
        // file-watch handle, a scarce per-user resource when many harnesses are alive at once. Nothing the
        // harness builds reads configuration: every option it needs is registered explicitly below.
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        try
        {
            ConfigureHost(builder, options, tempRoot, provider, effects, deliverer, clock);
            return builder.Build();
        }
        catch
        {
            // The builder owns its configuration until a host does, so a failed build must release it.
            builder.Configuration.Dispose();
            throw;
        }
    }

    private static void ConfigureHost(
        HostApplicationBuilder builder,
        RecoveryIngressOptions options,
        string tempRoot,
        ScriptedLlmProvider provider,
        SideEffectToolContributor effects,
        RecordingDeliverer deliverer,
        HarnessClock clock)
    {
        builder.Logging.ClearProviders();
        var services = builder.Services;

        // The data path is pinned as well as the home: left unset it honours BOTNEXUS_DATA_DIR, which on
        // a developer machine can point the stores at real data.
        services.AddSingleton(new BotNexusHome(new System.IO.Abstractions.FileSystem(), homePath: tempRoot, dataPath: tempRoot));
        services.AddOptions<PlatformConfig>().Configure(config => config.Gateway = new GatewaySettingsConfig
        {
            // Auxiliary behaviour that would add model calls or rows of its own is switched off so the
            // scripted call count and the transcript are exactly what the turn under test produces.
            Auxiliary = new AuxiliaryConfig { Titling = new TitlingConfig { Enabled = false } },
            ClaimAudit = new ClaimAuditConfig { Enabled = false }
        });
        services.AddSingleton<GatewayAuthManager>();
        services.AddSingleton<IAgentConfigurationSource, EmptyAgentConfigurationSource>();
        services.AddBotNexusGateway(configure: gateway =>
        {
            if (options.MaxAutoReplayAttempts is { } maxAttempts)
                gateway.MaxAutoReplayAttempts = maxAttempts;
        });

        // No hosted service ever starts here. Removing the registrations (rather than just not starting
        // the host) keeps that true even if a future test starts the host.
        foreach (var hosted in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToList())
            services.Remove(hosted);

        // Fresh per harness, as the platform composition registers them per container: the exhaustion lane
        // remembers an exhausted provider here and nowhere else.
        services.AddSingleton<BotNexus.Agent.Core.Loop.IProviderSuspensionRegistry>(
            _ => new BotNexus.Agent.Core.Loop.ProviderSuspensionRegistry());

        services.AddSingleton<ApiProviderRegistry>();
        services.AddSingleton<ModelRegistry>();
        services.AddSingleton(serviceProvider =>
        {
            var apiProviders = serviceProvider.GetRequiredService<ApiProviderRegistry>();
            var models = serviceProvider.GetRequiredService<ModelRegistry>();
            provider.Register(apiProviders, models);
            return new LlmClient(apiProviders, models);
        });
        services.AddSingleton<IAgentToolContributor>(effects);
        if (options.FailAgentCreation)
            services.AddSingleton<IAgentToolContributor>(new FaultingToolContributor());

        services.Replace(ServiceDescriptor.Singleton<IOutboundResponseDeliverer>(deliverer));
        services.Replace(ServiceDescriptor.Singleton<IActivityBroadcaster>(_ => new RecordingActivityBroadcaster(
            new InMemoryActivityBroadcaster(NullLogger<InMemoryActivityBroadcaster>.Instance))));
        services.Replace(ServiceDescriptor.Singleton<IInboundMessageOrchestrator>(serviceProvider =>
            new RecordingOrchestrator(serviceProvider.GetRequiredService<GatewayHost>().Orchestrator)));

        // The composition root's own store (with its archive drain attached) is wrapped, not replaced, so
        // the store under test is exactly the one production would build.
        var storeFactory = services.Last(descriptor => descriptor.ServiceType == typeof(ISessionStore)).ImplementationFactory
            ?? throw new InvalidOperationException("The session store is expected to be registered through a factory.");
        services.Replace(ServiceDescriptor.Singleton<ISessionStore>(serviceProvider =>
            new RecordingSessionStore(
                storeFactory(serviceProvider) as ISessionStore
                    ?? throw new InvalidOperationException("The registered session store factory returned no session store."),
                clock)));

        // Mirrors the composition root's construction of the recovery service, which is registered there
        // as a hosted service (removed above) and so cannot be resolved by type.
        services.AddSingleton(serviceProvider => new InterruptedTurnNotificationService(
            serviceProvider.GetRequiredService<ISessionStore>(),
            serviceProvider.GetRequiredService<IAgentRegistry>(),
            serviceProvider.GetRequiredService<IActivityBroadcaster>(),
            serviceProvider.GetRequiredService<IConversationEventPublisher>(),
            serviceProvider.GetRequiredService<ILogger<InterruptedTurnNotificationService>>(),
            serviceProvider.GetService<IInboundMessageOrchestrator>(),
            serviceProvider.GetService<IOptions<GatewayOptions>>(),
            serviceProvider.GetService<IConversationStore>(),
            serviceProvider.GetService<SessionLifecycleEvents>()));
    }

    /// <summary>Creates an empty, user-facing conversation owned by the harness agent.</summary>
    internal async Task<Conversation> CreateConversationAsync(string title = "Scripted ingress thread")
    {
        var conversation = ConversationFactory.CreateForAgent(
            ConversationKind.HumanAgent,
            ConversationId.Create(),
            WorkerAgentId,
            title: title,
            initiator: CitizenId.Of(WorkerAgentId));
        return await ConversationStore.CreateAsync(conversation);
    }

    /// <summary>
    /// Posts through the real REST controller, which resolves the conversation's session and admits the
    /// message through the real orchestrator. Asserts the 202 the API promises.
    /// </summary>
    internal async Task<IngressReceipt> PostViaControllerAsync(
        ConversationId conversationId,
        string message,
        bool wake = true,
        string? sender = null,
        InboundDeliveryMode delivery = InboundDeliveryMode.Auto)
    {
        var result = await _controller.PostMessage(
            WorkerAgentId.Value,
            conversationId.Value,
            new PostConversationMessageRequest(message, wake, sender, delivery),
            CancellationToken.None);

        var accepted = result.ShouldBeOfType<AcceptedResult>();
        var response = accepted.Value.ShouldBeOfType<PostConversationMessageResponse>();
        var receipt = new IngressReceipt(ConversationId.From(response.ConversationId), SessionId.From(response.SessionId));
        Remember(receipt.SessionId);
        return receipt;
    }

    /// <summary>
    /// Sends through the real <see cref="ConversationTool"/> as another agent would: an internal inbound with
    /// an agent sender, voiced as the agent unless <paramref name="speakAs"/> says otherwise.
    /// </summary>
    internal async Task<IngressReceipt> SendViaConversationToolAsync(
        ConversationId conversationId,
        string message,
        string? speakAs = null)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["action"] = "message",
            ["conversationId"] = conversationId.Value,
            ["message"] = message
        };
        if (speakAs is not null)
            arguments["speak_as"] = speakAs;

        var result = await _conversationTool.ExecuteAsync("conversation-call", arguments, CancellationToken.None);
        using var receipt = JsonDocument.Parse(result.Content.Single().Value);
        var sessionId = SessionId.From(
            receipt.RootElement.GetProperty("sessionId").GetString()
            ?? throw new InvalidOperationException("The conversation tool returned no session id."));
        Remember(sessionId);
        return new IngressReceipt(conversationId, sessionId);
    }

    /// <summary>
    /// Posts a steer through the real orchestrator the way a session-addressed transport does. The REST
    /// route carries no session id, so it can only queue; a steer needs one.
    /// </summary>
    internal Task<InboundDispatchStatus> SteerAsync(IngressReceipt target, string text, string senderLabel)
        => Orchestrator.PostAsync(new InboundMessage
        {
            ChannelType = ChannelKey.From("api"),
            SenderId = senderLabel,
            Sender = CitizenId.Of(WorkerAgentId),
            ChannelAddress = ChannelAddress.From(target.ConversationId.Value),
            Content = text,
            RoutingHints = new InboundMessageRoutingHints(
                WorkerAgentId, target.SessionId, target.ConversationId, InboundDeliveryMode.Steer)
        });

    /// <summary>Waits until <paramref name="finishedTurns"/> turns have ended in total.</summary>
    internal async Task AwaitTurnsAsync(int finishedTurns)
    {
        try
        {
            await TestAwait.EventuallyAsync(
                () => Activities.TurnsFinished >= finishedTurns,
                $"{finishedTurns} gateway turn(s) to finish",
                timeout: Liveness);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(ex.Message + Environment.NewLine + await DescribeAsync(), ex);
        }
    }

    /// <summary>
    /// Waits until every turn the test started (<paramref name="ingressTurns"/>) and every recovery
    /// replacement those turns admitted have ended. A turn's replacements are decided inline before the
    /// turn is counted as ended, so the replacement count is final each time it is read. Only admitted
    /// replacements are waited for: a refused one never becomes a turn.
    /// </summary>
    internal async Task AwaitSettledAsync(int ingressTurns)
    {
        var expected = ingressTurns;
        while (true)
        {
            await AwaitTurnsAsync(expected);
            var next = ingressTurns + Orchestrator.AcceptedReplayPosts.Count;
            if (next <= expected)
                return;

            expected = next;
        }
    }

    /// <summary>Reads the stored session. With the in-memory store this is the live aggregate.</summary>
    internal async Task<GatewaySession> ReadSessionAsync(SessionId sessionId)
    {
        Remember(sessionId);
        var session = await SessionStore.GetAsync(sessionId);
        return session.ShouldNotBeNull($"session {sessionId.Value} should exist");
    }

    /// <summary>A diagnostic dump of everything the harness observed, for timeout and assertion messages.</summary>
    internal async Task<string> DescribeAsync()
    {
        var text = new StringBuilder();
        text.AppendLine(
            $"provider calls: {Provider.CallCount}; effects applied: {Effects.EffectCount}; turns finished: {Activities.TurnsFinished}");
        text.AppendLine("posts: " + string.Join(
            "; ",
            Orchestrator.Posts.Select(post => $"{(post.IsReplay ? "replay" : "ingress")}/{(post.Accepted ? "accepted" : "refused")}")));
        text.AppendLine("lifecycle events: " + string.Join(", ", Lifecycle.Events.Select(item => $"{item.Event.Type}@{item.Sequence}")));
        text.AppendLine("fenced saves: " + string.Join(
            ", ",
            Saves.FencedSaves.Select(save => $"{(save.Outcome?.ToString() ?? "threw")}@{save.Sequence}")));
        text.AppendLine("activities: " + string.Join(", ", Activities.Items.Select(item => item.Type)));
        text.AppendLine("fan-outs offered: " + string.Join(
            "; ",
            Deliverer.FanOuts.Select(fanOut => string.IsNullOrEmpty(fanOut.Content) ? "<nothing>" : $"\"{fanOut.Content}\"")));
        foreach (var sessionId in _knownSessions.Distinct())
        {
            var session = await SessionStore.GetAsync(sessionId);
            text.AppendLine($"session {sessionId.Value} ({session?.ChannelType}):");
            text.AppendLine(session?.GetHistorySnapshot().Render() ?? "  <missing>");
        }

        return text.ToString();
    }

    /// <summary>
    /// Tears the graph down without ever abandoning it. Callers assert before this runs; disposal is
    /// cleanup, never the barrier that makes an assertion deterministic.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Order matters. Gates the test left closed are released first so teardown cannot deadlock on the
    /// harness's own gates. Subscribers detach so nothing reacts while the graph comes down. The agent
    /// handles are stopped next, because <c>DefaultAgentSupervisor</c> is not disposable: disposing the
    /// container would leave them (and any run still in flight) alive. Stopping a handle aborts its run
    /// and waits for it to settle, which is what lets the queue workers finish, so the queues drain after
    /// it. Only then is the container disposed and the home deleted.
    /// </para>
    /// <para>
    /// Every step is bounded and a step that does not finish throws, naming itself. The home is deleted
    /// only after every step succeeded, so no live work can ever run against a deleted directory.
    /// </para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        Orchestrator.ReleaseReplayPosts();
        Effects.ReleaseHeldEffect();

        Lifecycle.Detach();
        if (Recovery is not null)
            await WithinDeadlineAsync(Recovery.StopAsync(CancellationToken.None), "unsubscribing recovery");

        await WithinDeadlineAsync(Supervisor.StopAllAsync(), "stopping the agent handles");
        await WithinDeadlineAsync(_gateway.DisposeAsync().AsTask(), "draining the inbound queues");
        await WithinDeadlineAsync(Supervisor.StopAllAsync(), "stopping handles created while the queues drained");
        await WithinDeadlineAsync(DisposeHostAsync(_host), "disposing the service graph");

        TryDeleteDirectory(_tempRoot);
    }

    private static async Task WithinDeadlineAsync(Task work, string step)
    {
        try
        {
            await work.WaitAsync(DisposalDeadline).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"Harness teardown did not finish {step} within {DisposalDeadline.TotalSeconds:0} s. "
                + "The service graph may still be running, so its home was left in place.",
                ex);
        }
    }

    private static async Task DisposeHostAsync(IHost? host)
    {
        switch (host)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case not null:
                host.Dispose();
                break;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a unique temp directory.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of a unique temp directory.
        }
    }

    private void Remember(SessionId sessionId)
    {
        if (!_knownSessions.Contains(sessionId))
            _knownSessions.Add(sessionId);
    }

    private sealed class EmptyAgentConfigurationSource : IAgentConfigurationSource
    {
        public Task<IReadOnlyList<AgentDescriptor>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentDescriptor>>([]);

        public IDisposable? Watch(Action<IReadOnlyList<AgentDescriptor>> onChanged) => null;
    }

    /// <summary>Fails agent construction, which no run can recover from, to stand in for an infrastructure fault.</summary>
    private sealed class FaultingToolContributor : IAgentToolContributor
    {
        public Task<AgentToolContribution> ContributeAsync(
            AgentToolContributionContext context,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Synthetic agent construction failure.");
    }
}
