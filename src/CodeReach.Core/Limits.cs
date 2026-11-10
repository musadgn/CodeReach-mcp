namespace CodeReach.Core;

/// <summary>The one home of every cap. Tool schemas, validation and queries all read these.</summary>
public static class Limits
{
    public const int DefaultRows = 60;
    public const int MaxRows = 1000;
    public const int DefaultCallDepth = 1;
    public const int MaxCallDepth = 8;
    public const int DefaultTraceDepth = 30;
    public const int MaxTraceDepth = 60;
    public const int MaxTraceNodes = 200_000;
    public const int MaxSymbolLength = 600;
    public const int MaxDiffBytes = 2_000_000;
    public const int MaxCandidatesListed = 12;
    public const int MaxPrefixHops = 4;
    public const int InstructionsMaxChars = 2400;
    public const int FreshnessSweepSeconds = 2;
    public const int WaitForFreshSeconds = 25;
    public const int IdleUnloadMinutes = 20;
    public const int WatcherDebounceMilliseconds = 400;
}
