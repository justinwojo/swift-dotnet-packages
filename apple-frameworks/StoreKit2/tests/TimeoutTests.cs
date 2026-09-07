// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace SwiftBindings.StoreKit2.Tests;

// Managed-only deadline regressions: no StoreKit calls or receipt/backend prerequisites.
internal static class TimeoutTests
{
    private static readonly TimeSpan UnwindBudget = TimeSpan.FromSeconds(5);

    internal static async Task RunAsync(Action<string> pass, Action<string, string> fail)
    {
        foreach (var (name, run) in new (string, Func<Task>)[]
        {
            ("timeout helper preserves normal result ownership", NormalResultOwnership),
            ("timeout helper requests token cancellation", TokenCancellation),
            ("timeout helper disposes success racing cancellation", LateSuccessDisposal),
            ("timeout helper signals failed unwind and observes eventual result", FailedUnwind),
        })
        {
            try { await run(); pass(name); }
            catch (Exception ex) { fail(name, $"{ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private static async Task NormalResultOwnership()
    {
        using var expected = new DisposableResult();
        var actual = await Tests.WithCancellationTimeout(
            _ => Task.FromResult(expected), UnwindBudget, UnwindBudget, "normal result");
        Require(ReferenceEquals(actual, expected), "result identity changed");
        Require(expected.DisposeCount == 0, "timely result must remain caller-owned");
    }

    private static async Task TokenCancellation()
    {
        var pending = new TaskCompletionSource<DisposableResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedToken = default;
        using var registration = new RegistrationOwner();
        var timeout = await ExpectTimeout(() => Tests.WithCancellationTimeout(token =>
        {
            observedToken = token;
            registration.Value = token.Register(() => pending.TrySetCanceled(token));
            return pending.Task;
        }, TimeSpan.Zero, UnwindBudget, "token cancellation"));
        Require(observedToken.IsCancellationRequested, "deadline did not cancel the supplied token");
        Require(pending.Task.IsCanceled, "managed task did not observe cancellation");
        Require(timeout is not Tests.ManagedUnwindTimeoutException, "canceled task should unwind");
    }

    private static async Task LateSuccessDisposal()
    {
        var pending = new TaskCompletionSource<DisposableResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateResult = new DisposableResult();
        using var registration = new RegistrationOwner();
        var timeout = await ExpectTimeout(() => Tests.WithCancellationTimeout(token =>
        {
            // The result becomes available only when cancellation is requested after the deadline.
            registration.Value = token.Register(() => pending.TrySetResult(lateResult));
            return pending.Task;
        }, TimeSpan.Zero, UnwindBudget, "late success"));
        Require(timeout is not Tests.ManagedUnwindTimeoutException, "late success should unwind");
        Require(lateResult.DisposeCount == 1, "late success must be disposed exactly once");
    }

    private static async Task FailedUnwind()
    {
        var pending = new TaskCompletionSource<DisposableResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateResult = new DisposableResult();
        CancellationToken observedToken = default;
        try
        {
            // Zero bounds make both deadline decisions deterministic; the task ignores cancellation.
            var timeout = await ExpectTimeout(() => Tests.WithCancellationTimeout(token =>
            {
                observedToken = token;
                return pending.Task;
            }, TimeSpan.Zero, TimeSpan.Zero, "failed unwind"));
            Require(timeout is Tests.ManagedUnwindTimeoutException,
                "failed managed unwind must use the distinct exception that stops later StoreKit tests");
            Require(observedToken.IsCancellationRequested && !pending.Task.IsCompleted,
                "the failed-unwind case must leave a canceled request with a still-pending managed task");
        }
        finally
        {
            // Release the synthetic operation even when an assertion fails. The helper's observer
            // must retain ownership of success arriving after the second deadline as well.
            pending.TrySetResult(lateResult);
        }
        await lateResult.WhenDisposed.WaitAsync(UnwindBudget);
        Require(lateResult.DisposeCount == 1, "success after failed unwind must be disposed exactly once");
    }

    private static async Task<TimeoutException> ExpectTimeout(Func<Task<DisposableResult>> run)
    {
        try { using var unexpected = await run(); }
        catch (TimeoutException ex) { return ex; }
        throw new InvalidOperationException("operation unexpectedly passed its deadline");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RegistrationOwner : IDisposable
    {
        public CancellationTokenRegistration Value;
        public void Dispose() => Value.Dispose();
    }

    private sealed class DisposableResult : IDisposable
    {
        private readonly TaskCompletionSource disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int disposeCount;
        public int DisposeCount => Volatile.Read(ref disposeCount);
        public Task WhenDisposed => disposed.Task;
        public void Dispose()
        {
            Interlocked.Increment(ref disposeCount);
            disposed.TrySetResult();
        }
    }
}
