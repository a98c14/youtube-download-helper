using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Core.Queue;
using static YoutubeDownloadHelper.Contracts.Queue.ItemState;
using static YoutubeDownloadHelper.Core.Queue.ItemTrigger;

namespace YoutubeDownloadHelper.Core.Tests.Queue;

public sealed class ItemStateMachineTests
{
    // Written out by hand from PLAN.md §5.1, not derived from the implementation.
    private static readonly (ItemState From, ItemTrigger Trigger, ItemState To)[] Allowed =
    [
        (Added, Enqueue, Queued),

        (Added, Pause, Paused),
        (Queued, Pause, Paused),
        (Resolving, Pause, Paused),
        (Downloading, Pause, Paused),
        (PostProcessing, Pause, Paused),
        (WaitingRetry, Pause, Paused),
        (WaitingAuth, Pause, Paused),
        (WaitingToolFix, Pause, Paused),
        (Paused, Resume, Queued),

        (Queued, StartResolving, Resolving),
        (Resolving, StartDownloading, Downloading),
        (Downloading, StartPostProcessing, PostProcessing),
        (Downloading, Complete, Completed),
        (PostProcessing, Complete, Completed),
        (Resolving, Skip, Skipped),

        (Resolving, WaitForRetry, WaitingRetry),
        (Downloading, WaitForRetry, WaitingRetry),
        (PostProcessing, WaitForRetry, WaitingRetry),
        (Resolving, WaitForAuth, WaitingAuth),
        (Downloading, WaitForAuth, WaitingAuth),
        (PostProcessing, WaitForAuth, WaitingAuth),
        (Resolving, WaitForToolFix, WaitingToolFix),
        (Downloading, WaitForToolFix, WaitingToolFix),
        (PostProcessing, WaitForToolFix, WaitingToolFix),

        (Resolving, Fail, Failed),
        (Downloading, Fail, Failed),
        (PostProcessing, Fail, Failed),
        (WaitingRetry, Fail, Failed),
        (WaitingAuth, Fail, Failed),
        (WaitingToolFix, Fail, Failed),

        (WaitingRetry, RetryDue, Queued),
        (WaitingAuth, AuthRestored, Queued),
        (WaitingToolFix, ToolFixed, Queued),
        (Failed, Retry, Queued),
        (WaitingRetry, Retry, Queued),
        (WaitingAuth, Retry, Queued),
        (WaitingToolFix, Retry, Queued),

        (Resolving, Interrupt, Queued),
        (Downloading, Interrupt, Queued),
        (PostProcessing, Interrupt, Queued),

        (Added, Remove, Removed),
        (Queued, Remove, Removed),
        (Paused, Remove, Removed),
        (Resolving, Remove, Removed),
        (Downloading, Remove, Removed),
        (PostProcessing, Remove, Removed),
        (Completed, Remove, Removed),
        (WaitingRetry, Remove, Removed),
        (WaitingAuth, Remove, Removed),
        (WaitingToolFix, Remove, Removed),
        (Failed, Remove, Removed),
        (Skipped, Remove, Removed),
        (Seen, Remove, Removed),
    ];

    public static TheoryData<ItemState, ItemTrigger, ItemState> AllowedTransitions()
    {
        var data = new TheoryData<ItemState, ItemTrigger, ItemState>();
        foreach (var (from, trigger, to) in Allowed)
        {
            data.Add(from, trigger, to);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllowedTransitions))]
    public void Allowed_transition(ItemState from, ItemTrigger trigger, ItemState to)
    {
        Assert.True(ItemStateMachine.TryFire(from, trigger, out var actual));
        Assert.Equal(to, actual);
    }

    [Theory]
    [InlineData(Added, StartResolving)]
    [InlineData(Queued, Complete)]
    [InlineData(Queued, StartDownloading)]
    [InlineData(Queued, Resume)]
    [InlineData(Queued, Interrupt)]
    [InlineData(Paused, StartResolving)]
    [InlineData(Paused, Pause)]
    [InlineData(Resolving, Complete)]
    [InlineData(Resolving, StartPostProcessing)]
    [InlineData(Downloading, Skip)]
    [InlineData(Completed, Retry)]
    [InlineData(Completed, Pause)]
    [InlineData(Skipped, Retry)]
    [InlineData(Failed, Pause)]
    [InlineData(Failed, RetryDue)]
    [InlineData(WaitingRetry, AuthRestored)]
    [InlineData(WaitingAuth, RetryDue)]
    [InlineData(Removed, Remove)]
    [InlineData(Removed, Retry)]
    [InlineData(Seen, Enqueue)]
    [InlineData(WaitingForStream, Remove)]
    [InlineData(Recording, Remove)]
    public void Disallowed_transition(ItemState from, ItemTrigger trigger) =>
        Assert.False(ItemStateMachine.TryFire(from, trigger, out _));

    [Fact]
    public void The_table_has_exactly_the_listed_transitions() =>
        Assert.Equal(
            Allowed.Order().ToArray(),
            ItemStateMachine.Transitions.Order().ToArray());

    [Fact]
    public void Reserved_states_are_never_entered() =>
        Assert.DoesNotContain(ItemStateMachine.Transitions, t => t.To is WaitingForStream or Recording or Seen or Added);
}
