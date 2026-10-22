using System.Windows.Forms;
using LiveSplit.Model;

namespace LiveSplit.Bridge.Tests;

/// <summary>A real Form owner and message loop for icon/RPC lifecycle tests.</summary>
internal sealed class LiveSplitTestUiThread : IDisposable
{
    private readonly Thread thread;
    internal LiveSplitState State { get; }
    internal int ManagedThreadId => thread.ManagedThreadId;

    internal LiveSplitTestUiThread(IRun run)
    {
        var ready = new TaskCompletionSource<LiveSplitState>(TaskCreationOptions.RunContinuationsAsynchronously);
        thread = new Thread(() =>
        {
            try
            {
                var state = TestLiveSplitState.Create(run);
                using var form = state.Form;
                _ = form.Handle;
                ready.SetResult(state);
                Application.Run();
            }
            catch (Exception exception) { ready.TrySetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(ready.Task.Wait(TimeSpan.FromSeconds(5)), "The test Form did not start.");
        State = ready.Task.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        State.Form.BeginInvoke((Action)Application.ExitThread);
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "The test UI did not stop.");
    }
}
