; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
SRI001 | Isolation | Error | A plugin-facing member is unclassified, or isolation-pending.txt names no reachable member
SRI002 | Isolation | Error | A signature cannot cross the plugin channel as it is
SRI003 | Isolation | Error | A by-value type is missing from the isolation JSON contexts
SRI004 | Isolation | Error | Two members share a member id
SRI005 | Isolation | Error | A default interface member has no explicit implementation in a proxy
SRI006 | Isolation | Error | A tuple element has no name
