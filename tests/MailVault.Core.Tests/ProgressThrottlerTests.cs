using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using Xunit;

namespace MailVault.Core.Tests;

public class ProgressThrottlerTests
{
    public sealed class MockTimer : ITimer
    {
        public TimerCallback Callback { get; }
        public object? State { get; }
        public TimeSpan DueTime { get; private set; } = Timeout.InfiniteTimeSpan;
        public TimeSpan Period { get; private set; } = Timeout.InfiniteTimeSpan;

        public MockTimer(TimerCallback callback, object? state)
        {
            Callback = callback ?? throw new ArgumentNullException(nameof(callback));
            State = state;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            Period = period;
            return true;
        }

        public void Trigger()
        {
            Callback(State);
        }

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public sealed class MockTimeProvider : TimeProvider
    {
        public List<MockTimer> Timers { get; } = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new MockTimer(callback, state);
            timer.Change(dueTime, period);
            Timers.Add(timer);
            return timer;
        }

        public void TriggerPendingTimers()
        {
            foreach (var timer in Timers)
            {
                if (timer.DueTime != Timeout.InfiniteTimeSpan)
                {
                    timer.Trigger();
                }
            }
        }
    }

    [Fact]
    public void ProgressThrottler_DoesNotFloodConsumer()
    {
        // Arrange
        var received = new List<string>();
        var mockTime = new MockTimeProvider();
        using var throttler = new ProgressThrottler<string>(received.Add, TimeSpan.FromMilliseconds(250), mockTime);

        // Act
        throttler.Report("update-1");
        throttler.Report("update-2");
        throttler.Report("update-3");

        // Assert - target should not receive immediately since it is throttled
        Assert.Empty(received);

        // Advance time / Trigger timer tick
        mockTime.TriggerPendingTimers();

        // Should receive the latest update only
        Assert.Single(received);
        Assert.Equal("update-3", received[0]);
    }

    [Fact]
    public void ProgressThrottler_AlwaysEmitsFinalProgress()
    {
        // Arrange
        var received = new List<string>();
        var mockTime = new MockTimeProvider();
        using (var throttler = new ProgressThrottler<string>(received.Add, TimeSpan.FromMilliseconds(250), mockTime))
        {
            throttler.Report("update-1");
            throttler.Report("update-2");

            // Act - Flush
            throttler.Flush();
        }

        // Assert
        Assert.Single(received);
        Assert.Equal("update-2", received[0]);
    }

    [Fact]
    public void ProgressThrottler_DeduplicatesEquivalentUpdates()
    {
        // Arrange
        var received = new List<string>();
        var mockTime = new MockTimeProvider();
        using var throttler = new ProgressThrottler<string>(received.Add, TimeSpan.FromMilliseconds(250), mockTime);

        // Act
        throttler.Report("duplicate");
        mockTime.TriggerPendingTimers();

        throttler.Report("duplicate"); // Identical to last emitted
        mockTime.TriggerPendingTimers();

        // Assert
        Assert.Single(received);
        Assert.Equal("duplicate", received[0]);
    }

    [Fact]
    public void ProgressThrottler_FlushesLastPendingUpdate()
    {
        // Arrange
        var received = new List<string>();
        var mockTime = new MockTimeProvider();
        using var throttler = new ProgressThrottler<string>(received.Add, TimeSpan.FromMilliseconds(250), mockTime);

        // Act
        throttler.Report("last-one");
        throttler.Flush();

        // Assert
        Assert.Single(received);
        Assert.Equal("last-one", received[0]);

        // Triggering timer later should not emit anything new
        received.Clear();
        mockTime.TriggerPendingTimers();
        Assert.Empty(received);
    }
}
