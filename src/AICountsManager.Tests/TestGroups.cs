namespace AICountsManager.Tests;

internal static partial class Program
{
    // Explicit method identities keep CLI test groups stable when tests are added or reordered.
    private static readonly Dictionary<string, HashSet<string>> TestGroupDefinitions = new(StringComparer.Ordinal)
    {
        ["architecture"] = [
            nameof(ProtectedArchiveCompatibilityAsync),
            nameof(InitializationIsSharedAsync),
            nameof(InitializationShutdownAsync),
            nameof(TrayStartsBeforeRecoveryAsync),
            nameof(SharedAccountManagementAsync),
            nameof(ExtensibleAccountRegistryAsync),
            nameof(ConstructorPreservesSavedDataAsync),
            nameof(NamedTestGroupsStableAsync),
        ],
        ["client"] = [
            nameof(ClientQuotaParsingAsync),
            nameof(ClientQuotaTransportAsync),
            nameof(ClientCredentialReadOnlyAsync),
            nameof(ClientDisabledAndRateLimitAsync),
            nameof(ClientFailureBoundariesAsync),
            nameof(ClaudeIncrementalHistoryAsync),
            nameof(CursorHistoryIsolationAsync),
        ],
        ["provider"] = [
            nameof(ClientQuotaParsingAsync),
            nameof(ClientQuotaTransportAsync),
            nameof(ClientCredentialReadOnlyAsync),
            nameof(ClientDisabledAndRateLimitAsync),
            nameof(ClientFailureBoundariesAsync),
            nameof(ClaudeIncrementalHistoryAsync),
            nameof(CursorHistoryIsolationAsync),
            nameof(ProviderDisplayParityAsync),
            nameof(CurrentProviderBoundaryAsync),
            nameof(RegisteredProvidersUiAsync),
            nameof(GeminiFirstDayAsync),
            nameof(WeeklyOnlyPaceAsync),
            nameof(UnifiedOrderPersistsAsync),
            nameof(UnifiedOrderUiAsync),
            nameof(CloudQuotaAsync),
            nameof(CloudFailuresAsync),
            nameof(ProviderModelIsolationAsync),
            nameof(AntigravityQuotaParsingAsync),
            nameof(AntigravityIdentityAsync),
            nameof(MultiProviderRefreshAsync),
            nameof(ProviderCacheIdentityAsync),
            nameof(ProviderRegistryAsync),
            nameof(AntigravityAccountingAsync),
            nameof(AntigravitySqliteHistoryAsync),
            nameof(ProviderUiAsync),
        ],
        ["account-flow"] = [
            nameof(CodexProviderToggleAsync),
            nameof(CodexEnrollmentCaptureAsync),
            nameof(CodexEnrollmentRecoveryAsync),
            nameof(CodexEnrollmentBoundariesAsync),
            nameof(CodexEnrollmentFailureAsync),
            nameof(CodexDisabledWatcherAsync),
        ],
        ["claude-accounts"] = [
            nameof(ClaudeArchiveRoundTripAsync),
            nameof(ClaudeArchiveRollbackAsync),
            nameof(ClaudeAccountsLifecycleAsync),
            nameof(ClaudeMixedIdentityAsync),
            nameof(ClaudeAccountsUiAsync),
        ],
        ["claude-desktop"] = [
            nameof(ClaudeDesktopSelectionAsync),
            nameof(ClaudeDesktopStorageAsync),
            nameof(ClaudeDesktopIdentityAsync),
        ],
        ["organization"] = [
            nameof(CodexOrganizationRoundTripAsync),
            nameof(CodexOrganizationSwitchAsync),
            nameof(ClaudeOrganizationRoundTripAsync),
            nameof(ClaudeOrganizationSwitchAsync),
            nameof(OrganizationRecoveryAsync),
            nameof(CodexOrganizationRecoveryAsync),
            nameof(OrganizationClientRaceAsync),
            nameof(OrganizationBoundariesAsync),
        ],
        ["reset"] = [
            nameof(AntigravityHostSelectionAsync),
            nameof(AntigravityHostFallbackAsync),
            nameof(QuotaResetStatesAsync),
            nameof(QuotaResetUiAsync),
        ],
    };

    private static (string Name, Func<Task> Run)[] SelectTestGroup(string group, IEnumerable<(string Name, Func<Task> Run)>? source = null)
    {
        var ids = TestGroupDefinitions[group];
        var selected = (source ?? Tests).Where(t => ids.Contains(t.Run.Method.Name)).ToArray();
        if (selected.Length != ids.Count) throw new InvalidOperationException("El grupo de pruebas contiene una entrada ausente o duplicada: " + group);
        return selected;
    }
}
