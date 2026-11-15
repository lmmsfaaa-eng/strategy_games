global using Xunit;

internal static class TestMods
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Load() => IH.Shared.Rules.SetBranches(IH.Shared.Rules.DefaultBranches());
}
