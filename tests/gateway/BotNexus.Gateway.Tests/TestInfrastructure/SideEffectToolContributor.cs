using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Gateway.Abstractions.Agents;

namespace BotNexus.Gateway.Tests.TestInfrastructure;

/// <summary>
/// Contributes one tool, <see cref="ToolName"/>, that applies a synthetic side effect: it appends the
/// tool-call id to a marker file the test owns. The file, not an in-process counter, is the source of
/// truth, so "the mutation ran exactly once" survives anything the gateway does to its own state.
/// </summary>
/// <remarks>
/// A test can hold the next execution after its effect is applied (<see cref="HoldNextEffect"/>) to
/// keep a run live while it drives other ingress, then release it. The hold stays reachable until the
/// held execution actually resumes, so <see cref="ReleaseHeldEffect"/> works however late it is called.
/// Contributors are created per harness with a path under the harness's own temp home, so parallel
/// tests never share a marker.
/// </remarks>
/// <param name="markerPath">Absolute path of the marker file, unique to the owning harness.</param>
internal sealed class SideEffectToolContributor(string markerPath) : IAgentToolContributor
{
    internal const string ToolName = "record_effect";

    private static readonly TimeSpan HoldLiveness = TimeSpan.FromSeconds(60);

    private readonly object _sync = new();
    private TaskCompletionSource _entered = NewSignal();
    private TaskCompletionSource? _armed;
    private TaskCompletionSource? _active;

    /// <summary>How many times the synthetic effect has been applied.</summary>
    internal int EffectCount
    {
        get
        {
            lock (_sync)
                return File.Exists(markerPath) ? File.ReadAllLines(markerPath).Length : 0;
        }
    }

    /// <summary>Makes the next execution block, after applying its effect, until <see cref="ReleaseHeldEffect"/>.</summary>
    internal void HoldNextEffect()
    {
        lock (_sync)
        {
            _entered = NewSignal();
            _armed = NewSignal();
        }
    }

    /// <summary>Completes when a held execution has applied its effect and is waiting to be released.</summary>
    internal Task AwaitHeldEffectAsync()
    {
        TaskCompletionSource entered;
        lock (_sync)
            entered = _entered;

        return entered.Task.WaitAsync(HoldLiveness);
    }

    /// <summary>
    /// Lets a held execution return, whether it is already parked or has not started yet. Safe to call
    /// when nothing is held.
    /// </summary>
    internal void ReleaseHeldEffect()
    {
        lock (_sync)
        {
            _armed?.TrySetResult();
            _active?.TrySetResult();
        }
    }

    /// <inheritdoc />
    public Task<AgentToolContribution> ContributeAsync(
        AgentToolContributionContext context,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new AgentToolContribution([new RecordEffectTool(this)]));

    private async Task<string> ApplyAsync(string toolCallId, CancellationToken cancellationToken)
    {
        TaskCompletionSource? hold;
        TaskCompletionSource entered;
        lock (_sync)
        {
            var directory = Path.GetDirectoryName(markerPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.AppendAllText(markerPath, toolCallId + Environment.NewLine);
            hold = _armed;
            if (hold is not null)
            {
                // One hold parks one execution. It moves from armed to active, where release can still
                // reach it for as long as the execution is parked.
                _armed = null;
                _active = hold;
            }

            entered = _entered;
        }

        if (hold is not null)
        {
            entered.TrySetResult();
            try
            {
                await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_active, hold))
                        _active = null;
                }
            }
        }

        return Receipt(toolCallId);
    }

    /// <summary>The result text the tool returns for <paramref name="toolCallId"/>, which tests look for in a transcript.</summary>
    internal static string Receipt(string toolCallId) => $"effect applied for call {toolCallId}";

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class RecordEffectTool(SideEffectToolContributor owner) : IAgentTool
    {
        public string Name => ToolName;

        public string Label => ToolName;

        public Tool Definition => new(
            Name,
            "Applies one synthetic side effect and reports it.",
            JsonSerializer.SerializeToElement(new { type = "object" }));

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default)
            => Task.FromResult(arguments);

        public async Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default,
            AgentToolUpdateCallback? onUpdate = null)
        {
            var text = await owner.ApplyAsync(toolCallId, cancellationToken).ConfigureAwait(false);
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, text)]);
        }
    }
}
