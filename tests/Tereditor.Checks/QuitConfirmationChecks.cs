using SharpRail.UI;

namespace SharpRail.Checks;

/// <summary>Translations of upstream's quitConfirmation.test.ts against a fake clock and scheduler.</summary>
internal static class QuitConfirmationChecks
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed class Harness
    {
        public long Time;
        public bool? Held = true;
        public bool CanShow = true;
        public int Quits;
        public readonly List<QuitHint> Hints = [];
        public Action? Tick;
        public QuitConfirmation Gesture { get; }

        public Harness() => Gesture = new(() => Held, () => CanShow, () => Quits++, Hints.Add, () => Time,
            (callback, _) => { Tick = callback; return new Stop(this); });

        private sealed class Stop(Harness owner) : IDisposable { public void Dispose() => owner.Tick = null; }

        public void Advance(long ms)
        {
            for (var elapsed = 0L; elapsed < ms; elapsed += QuitConfirmation.PollMs)
            { Time += Math.Min(QuitConfirmation.PollMs, ms - elapsed); Tick?.Invoke(); }
        }

        public QuitHint Last => Hints.Count == 0 ? QuitHint.Hidden : Hints[^1];
    }

    internal static void Run()
    {
        var h = new Harness();
        h.Gesture.Press(true); h.Held = false; h.Advance(40);
        Require(h.Last == QuitHint.Armed, "A first press must arm the hint.");
        h.Advance(600);
        Require(h.Quits == 0 && h.Last == QuitHint.Hidden && h.Tick is null, "A single tap must expire silently and stop polling.");

        h = new();
        h.Gesture.Press(true); h.Held = false; h.Advance(80); h.Held = true; h.Gesture.Press(true);
        Require(h.Last == QuitHint.Release && h.Quits == 0, "A double press must wait for release.");
        h.Held = false; h.Advance(40);
        Require(h.Quits == 1 && h.Last == QuitHint.Quitting, "Releasing a double press must quit once.");

        h = new();
        h.Gesture.Press(true); h.Advance(1160);
        Require(h.Last == QuitHint.Armed, "Holding short of 1200ms must stay armed.");
        h.Advance(80);
        Require(h.Last == QuitHint.Release && h.Quits == 0, "A 1200ms hold must show the release hint without quitting.");
        h.Held = false; h.Advance(40);
        Require(h.Quits == 1, "Releasing after a hold must quit.");

        h = new();
        h.Gesture.Press(true); h.Gesture.Press(true); h.Gesture.Press(true);
        Require(h.Quits == 0 && h.Last == QuitHint.Armed, "Key repeat must never confirm.");

        h = new();
        h.Gesture.Press(true); h.Held = false; h.Advance(40 * 14); h.Held = true; h.Gesture.Press(true);
        Require(h.Last == QuitHint.Armed && h.Quits == 0, "A press after the window must re-arm, not confirm.");
        h.Held = false; h.Advance(40);
        Require(h.Quits == 0, "A re-armed press must not quit on release.");

        h = new();
        h.Gesture.Press(true); h.Gesture.Cancel();
        Require(h.Quits == 0 && h.Last == QuitHint.Hidden, "Losing focus must cancel an unconfirmed gesture.");

        h = new();
        h.Gesture.Press(true); h.Advance(1240); h.Gesture.Cancel();
        Require(h.Quits == 1, "Losing focus during release must complete the confirmed quit.");

        h = new();
        h.Gesture.Press(true); h.Held = null; h.Advance(40);
        Require(h.Quits == 0 && h.Last == QuitHint.Hidden, "An unreadable key state must cancel.");

        h = new() { CanShow = false };
        h.Gesture.Press(false);
        Require(h.Quits == 1, "Without a hint surface a tap must quit immediately.");
        h = new() { CanShow = false };
        h.Gesture.Press(true);
        Require(h.Quits == 0 && h.Last == QuitHint.Release, "Without a hint surface a hold must await release.");
        h.Held = false; h.Advance(40);
        Require(h.Quits == 1, "Releasing without a hint surface must quit.");

        h = new();
        h.Gesture.QuitNow(); h.Gesture.QuitNow(); h.Gesture.Press(false);
        Require(h.Quits == 1, "Quitting must be idempotent.");
    }
}