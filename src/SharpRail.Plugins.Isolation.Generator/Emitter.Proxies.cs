using System.Collections.Generic;

namespace SharpRail.Plugins.Isolation.Generator;

internal sealed partial class Emitter
{
    public IEnumerable<(string Hint, string Source)> Runner() => [];

    public IEnumerable<(string Hint, string Source)> Trusted() => [];
}