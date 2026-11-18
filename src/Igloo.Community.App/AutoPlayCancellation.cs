using System.Runtime.InteropServices;

namespace Igloo.Community.App;

// Before Explorer opens a drive that has just appeared, it asks the foreground window; while the installer partition is made the wizard answers no.
internal static partial class AutoPlayCancellation
{
    private const uint MSGFLT_ALLOW = 1;
    private static readonly Lazy<uint> Question = new(() => RegisterWindowMessage("QueryCancelAutoPlay"));
    private static readonly AutoPlayGate Gate = new();

    /// <summary>How often Explorer asked and was told no since the process started.</summary>
    internal static int Declined => Gate.Declined;

    /// <summary>Declines every question until the returned scope is disposed.</summary>
    internal static IDisposable Begin() => Gate.Begin();

    // Explorer is not elevated and the wizard is; without this filter its question does not reach the window.
    internal static void Attach(IntPtr window) =>
        _ = ChangeWindowMessageFilterEx(window, Question.Value, MSGFLT_ALLOW, IntPtr.Zero);

    /// <summary>True when the message is Explorer's question and the wizard declines it; the window then returns 1.</summary>
    internal static bool Declines(int message) => Gate.Declines(message, Question.Value);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessage(string name);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeWindowMessageFilterEx(IntPtr window, uint message, uint action, IntPtr changeInfo);
}

/// <summary>Counts the open scopes and the questions declined inside them.</summary>
internal sealed class AutoPlayGate
{
    private int _active;
    private int _declined;

    internal int Declined => Volatile.Read(ref _declined);

    internal IDisposable Begin()
    {
        Interlocked.Increment(ref _active);
        return new Scope(this);
    }

    // A question id of 0 means Windows did not register the message.
    internal bool Declines(int message, uint question)
    {
        if (question == 0 || unchecked((uint)message) != question || Volatile.Read(ref _active) <= 0)
            return false;
        Interlocked.Increment(ref _declined);
        return true;
    }

    private sealed class Scope(AutoPlayGate gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref gate._active);
        }
    }
}
