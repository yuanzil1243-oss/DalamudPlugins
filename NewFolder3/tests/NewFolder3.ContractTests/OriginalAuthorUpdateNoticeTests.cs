namespace NewFolder3;

internal static class OriginalAuthorUpdateNoticeTests
{
    internal static int Run()
    {
        (string Name, Action Test)[] tests =
        [
            ("upstream-notice-unacknowledged-load-denies-start", () => {
                var state = new OriginalAuthorUpdateNoticeState();
                var gate = new OriginalAuthorUpdateNoticeAccessGate(new NewFolder3AllowAllAccessGate(), state);
                Check(!state.Acknowledged && !gate.Current.IsAllowed, "Each load must start pending.");
                Check(!gate.TryAuthorizeFsdStart(out var error) && error == OriginalAuthorUpdateNoticeState.PendingMessage,
                    "API starts must require the same explicit acknowledgement as the UI.");
            }),
            ("upstream-notice-acknowledgement-allows-existing-gate", () => {
                var state = new OriginalAuthorUpdateNoticeState();
                var gate = new OriginalAuthorUpdateNoticeAccessGate(new NewFolder3AllowAllAccessGate(), state);
                state.Acknowledge();
                Check(gate.Current.IsAllowed && gate.TryAuthorizeFsdStart(out var error) && error.Length == 0,
                    "Acknowledgement restores the existing start policy.");
            }),
            ("upstream-notice-new-load-requires-confirmation-again", () => {
                var oldState = new OriginalAuthorUpdateNoticeState(); oldState.Acknowledge();
                Check(!new OriginalAuthorUpdateNoticeState().Acknowledged, "Acknowledgement is load-local, never saved.");
            }),
            ("upstream-notice-cannot-replace-inner-denial-or-start-task", () => {
                var inner = new DeniedGate(); var state = new OriginalAuthorUpdateNoticeState();
                var gate = new OriginalAuthorUpdateNoticeAccessGate(inner, state);
                Check(!gate.TryAuthorizeFsdStart(out _) && inner.Calls == 0, "Pending notice does not authorize inner work.");
                state.Acknowledge(); state.Acknowledge();
                Check(inner.Calls == 0, "Acknowledgement itself must not authorize or start anything.");
                Check(!gate.Current.IsAllowed && !gate.TryAuthorizeFsdStart(out var error) && error == "existing-denial",
                    "Existing authorization remains authoritative after confirmation.");
                Check(gate.FsdStartDenialNotice == "existing-denial" && inner.Calls == 1, "Preserve original denial notice.");
            }),
            ("upstream-notice-official-links-and-recommendation", () => {
                Check(new Uri(OriginalAuthorUpdateNoticeState.FeedUrl).Host == "raw.githubusercontent.com" &&
                    OriginalAuthorUpdateNoticeState.FeedUrl.Contains("/transparent-duck/NewFolder3/refs/heads/main/NewFolder3-release.json") &&
                    OriginalAuthorUpdateNoticeState.RepositoryUrl == "https://github.com/transparent-duck/NewFolder3",
                    "Offer the original author's feed and source, never this fork's feed.");
                Check(OriginalAuthorUpdateNoticeState.Recommendation.Contains("死宫、天宫、妖宫") &&
                    OriginalAuthorUpdateNoticeState.Recommendation.Contains("推荐使用原作者版本"), "Keep the requested notice wording.");
            })
        ];
        foreach (var (name, test) in tests) { test(); Console.WriteLine("  ok  " + name); }
        return tests.Length;
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    private sealed class DeniedGate : INewFolder3AccessGate
    {
        internal int Calls;
        public NewFolder3AccessDecision Current => NewFolder3AccessDecision.Denied("existing-denial");
        public string FsdStartDenialNotice => "existing-denial";
        public bool TryAuthorizeFsdStart(out string error) { Calls++; error = "existing-denial"; return false; }
    }
}
