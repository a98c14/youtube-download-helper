using YoutubeDownloadHelper.Contracts.Errors;
using YoutubeDownloadHelper.Contracts.Messages;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Core.Errors;
using YoutubeDownloadHelper.Core.Queue;
using YoutubeDownloadHelper.Core.Tests.Persistence;
using YoutubeDownloadHelper.Core.Tests.Queue;

namespace YoutubeDownloadHelper.Core.Tests.Errors;

public sealed class RetryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    // Written out from PLAN.md §5.2, not taken from RetryPolicy.Backoff.
    private static readonly TimeSpan[] Schedule =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Transient_errors_back_off_30s_2m_10m_30m_then_fail()
    {
        var policy = Policy(0.5);

        for (var attempt = 0; attempt < Schedule.Length; attempt++)
        {
            Assert.Equal(new RetryDecision(RetryAction.WaitForRetry, Now + Schedule[attempt]), policy.Decide(Error(ErrorCategory.Transient), attempt));
        }

        Assert.Equal(new RetryDecision(RetryAction.Fail), policy.Decide(Error(ErrorCategory.Transient), Schedule.Length));
        Assert.Equal(new RetryDecision(RetryAction.Fail), policy.Decide(Error(ErrorCategory.Transient), Schedule.Length + 3));
    }

    [Fact]
    public void Unknown_errors_back_off_like_transient_ones_but_retry_twice()
    {
        var policy = Policy(0.5);

        Assert.Equal(Now + Schedule[0], policy.Decide(Error(ErrorCategory.Unknown), 0).NextAttemptAt);
        Assert.Equal(Now + Schedule[1], policy.Decide(Error(ErrorCategory.Unknown), 1).NextAttemptAt);
        Assert.Equal(new RetryDecision(RetryAction.Fail), policy.Decide(Error(ErrorCategory.Unknown), 2));
    }

    [Theory]
    [InlineData(0.0, 0.8)]
    [InlineData(0.25, 0.9)]
    [InlineData(0.75, 1.1)]
    [InlineData(1.0, 1.2)]
    public void Jitter_scales_the_delay_linearly(double random, double factor)
    {
        for (var attempt = 0; attempt < Schedule.Length; attempt++)
        {
            var delay = Policy(random).Decide(Error(ErrorCategory.Transient), attempt).NextAttemptAt - Now;
            Assert.InRange(delay!.Value, (Schedule[attempt] * factor) - TimeSpan.FromMilliseconds(1), (Schedule[attempt] * factor) + TimeSpan.FromMilliseconds(1));
        }
    }

    [Fact]
    public void Jitter_stays_within_20_percent()
    {
        var policy = new RetryPolicy(new ManualTimeProvider(Now), new Random(39));
        for (var i = 0; i < 1000; i++)
        {
            var attempt = i % Schedule.Length;
            var delay = policy.Decide(Error(ErrorCategory.Transient), attempt).NextAttemptAt!.Value - Now;
            Assert.InRange(delay, Schedule[attempt] * 0.8, Schedule[attempt] * 1.2);
        }
    }

    [Fact]
    public void Non_transient_categories_do_not_back_off()
    {
        var policy = Policy(0.5);

        Assert.Equal(new RetryDecision(RetryAction.WaitForAuth), policy.Decide(Error(ErrorCategory.Auth), 0));
        Assert.Equal(new RetryDecision(RetryAction.WaitForToolFix, Now + TimeSpan.FromHours(24)), policy.Decide(Error(ErrorCategory.ToolBroken), 0));
        Assert.Equal(new RetryDecision(RetryAction.Fail), policy.Decide(Error(ErrorCategory.Unavailable), 0));
        Assert.Equal(new RetryDecision(RetryAction.Fail), policy.Decide(Error(ErrorCategory.LocalIo, ErrorCodes.AccessDenied), 0));
        Assert.Equal(new RetryDecision(RetryAction.BlockDiskFull), policy.Decide(Error(ErrorCategory.LocalIo, ErrorCodes.DiskFull), 0));

        // The retry count does not matter outside backoff.
        Assert.Equal(RetryAction.WaitForAuth, policy.Decide(Error(ErrorCategory.Auth), 10).Action);
        Assert.Equal(RetryAction.WaitForToolFix, policy.Decide(Error(ErrorCategory.ToolBroken), 10).Action);
    }

    [Theory]
    [InlineData(RetryAction.WaitForRetry, ItemTrigger.WaitForRetry)]
    [InlineData(RetryAction.WaitForAuth, ItemTrigger.WaitForAuth)]
    [InlineData(RetryAction.WaitForToolFix, ItemTrigger.WaitForToolFix)]
    [InlineData(RetryAction.Fail, ItemTrigger.Fail)]
    public void Decisions_become_job_failures(RetryAction action, ItemTrigger trigger)
    {
        var error = Error(ErrorCategory.Transient);
        var output = new JobOutput(1, ["ERROR: x"]);

        var failure = new RetryDecision(action, Now).ToJobFailure(error, output);

        Assert.Equal(new JobFailure(trigger, error.Category, error.Error, Now, output), failure);
    }

    [Fact]
    public void Disk_full_is_not_a_job_failure()
    {
        Assert.Null(new RetryDecision(RetryAction.BlockDiskFull).ToJobFailure(Error(ErrorCategory.LocalIo, ErrorCodes.DiskFull)));
    }

    [Fact]
    public void A_negative_retry_count_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy(0.5).Decide(Error(ErrorCategory.Transient), -1));
    }

    /// <summary>The policy and the queue actor together, with a fake clock: four retries, then Failed.</summary>
    [Fact]
    public async Task A_transient_item_is_retried_on_schedule_until_it_fails()
    {
        await using var h = await QueueHarness.StartAsync();
        var policy = new RetryPolicy(h.Root.Time, new Random(39));
        var error = new ErrorClassifier().Classify(1, ["ERROR: unable to download video data: HTTP Error 503: Service Unavailable"]);
        var item = await h.AddAsync();

        for (var retry = 0; ; retry++)
        {
            await h.Actor.StartResolvingAsync(item.Id, Ct);
            var current = (await h.Actor.GetSnapshotAsync(Ct)).Items.Single();
            var decision = policy.Decide(error, current.Attempt);
            Assert.True((await h.Actor.FailAsync(item.Id, decision.ToJobFailure(error)!, Ct)).Succeeded);
            if (decision.Action == RetryAction.Fail)
            {
                Assert.Equal(Schedule.Length, retry);
                break;
            }

            var waiting = (await h.Actor.GetSnapshotAsync(Ct)).Items.Single();
            Assert.Equal((ItemState.WaitingRetry, retry + 1), (waiting.State, waiting.Attempt));
            Assert.InRange(waiting.NextAttemptAt!.Value - h.Root.Time.Now, Schedule[retry] * 0.8, Schedule[retry] * 1.2);

            h.Root.Time.Now = waiting.NextAttemptAt.Value;
            Assert.True((await h.Actor.RetryDueAsync(item.Id, Ct)).Succeeded);
        }

        var failed = (await h.Actor.GetSnapshotAsync(Ct)).Items.Single();
        Assert.Equal((ItemState.Failed, Schedule.Length, ErrorCodes.ServerError), (failed.State, failed.Attempt, failed.Error?.Code));
    }

    private static RetryPolicy Policy(double random) => new(new ManualTimeProvider(Now), new FixedRandom(random));

    private static ErrorClassification Error(ErrorCategory category, string code = "error.test") =>
        new(category, new EngineMessage(code, "Test error."));

    private sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }
}
