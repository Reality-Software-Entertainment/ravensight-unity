using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace RealitySE.Tests
{
    /// <summary>
    /// The public build contract: inert without a key, the opt-in consent
    /// mode, the distribution label, a refused key ending the run, retry
    /// jitter, rs_event_id and the 8 KB event cap.
    /// </summary>
    public class PublicBuildTests
    {
        private static RavensightConfig OptIn(Harness h)
        {
            RavensightConfig config = h.Config();
            config.Consent = RavensightConsent.OptIn;
            return config;
        }

        private static List<Dictionary<string, object>> SentData(Harness h)
        {
            return h.Transport.To("/track/batch")
                .SelectMany(FakeTransport.EventsOf)
                .Select(e => (Dictionary<string, object>)e["data"])
                .ToList();
        }

        // --- Inert without a key ------------------------------------------------

        [Fact]
        public async Task NoKeyIsInertAndTouchesNothing()
        {
            Harness h = new Harness();
            RavensightConfig config = h.Config();
            config.IngestKey = "";
            RavensightClient client = h.Client(config);
            client.ReportStartupProblems();

            client.Begin();
            Assert.False(client.Track("level_started"));
            RavensightInitResult init = await client.InitAsync();
            await client.FlushAsync(true);
            await client.AutoFlushAsync();
            client.EnterBackground();
            await client.EnterForegroundAsync();
            Assert.Empty(await client.FetchSuggestionsAsync());
            RavensightException refused = await Assert.ThrowsAsync<RavensightException>(
                () => client.SubmitFeedbackAsync("hello", "bug", 3));

            Assert.Equal(RavensightReason.NoKey, init.Reason);
            Assert.Equal(RavensightReason.NoKey, client.StatusReason);
            Assert.Equal(RavensightReason.NoKey, refused.Code);
            Assert.True(client.IsInert);
            Assert.False(client.IsEnabled);
            Assert.Null(client.DeviceId);
            Assert.Empty(h.Transport.Requests);
            Assert.Equal(0, h.Storage.Writes);
            Assert.False(h.Storage.HasKey(RavensightClient.DeviceIdKey));
            Assert.Equal(new[] { RavensightClient.NoKeyWarning }, h.Warnings);
        }

        [Fact]
        public void ANullOrBlankKeyIsInertToo()
        {
            Harness h = new Harness();
            RavensightConfig config = h.Config();
            config.IngestKey = null;
            Assert.True(h.Client(config).IsInert);

            config = h.Config();
            config.IngestKey = "   ";
            Assert.True(h.Client(config).IsInert);
            Assert.Equal(0, h.Storage.Writes);
        }

        [Fact]
        public void OptionalSilencesTheOneWarning()
        {
            Harness h = new Harness();
            RavensightConfig config = h.Config();
            config.IngestKey = "";
            config.Optional = true;
            RavensightClient client = h.Client(config);
            client.ReportStartupProblems();
            client.ReportStartupProblems();

            Assert.Empty(h.Warnings);
            Assert.Equal(RavensightReason.NoKey, client.StatusReason);
        }

        [Fact]
        public async Task WhileInertThePlayersChoiceIsStillSaved()
        {
            Harness h = new Harness();
            RavensightConfig config = OptIn(h);
            config.IngestKey = "";
            RavensightClient client = h.Client(config);

            client.SetTrackingEnabled(true);
            await client.InitAsync();

            Assert.True(RavensightClient.ReadTrackingChoice(h.Storage, RavensightConsent.OptIn));
            Assert.False(h.Storage.HasKey(RavensightClient.DeviceIdKey));
            Assert.False(client.IsEnabled);
            Assert.Empty(h.Transport.Requests);
        }

        // --- Opt-in consent ---------------------------------------------------------

        [Fact]
        public async Task OptInBeforeTheYesMakesNoIdSendsNothingAndDropsEvents()
        {
            Harness h = new Harness();
            RavensightClient client = h.Client(OptIn(h));
            client.Begin();

            Assert.False(client.Track("before_yes"));
            RavensightInitResult init = await client.InitAsync();
            await client.FlushAsync(true);
            Assert.Empty(await client.FetchSuggestionsAsync());

            Assert.Equal(RavensightReason.Disabled, init.Reason);
            Assert.False(client.IsEnabled);
            Assert.True(client.OptedOut);
            Assert.Null(client.DeviceId);
            Assert.Equal(0, client.QueueCount);
            Assert.Empty(h.Transport.Requests);
            Assert.Equal(0, h.Storage.Writes);
            Assert.False(h.Storage.HasKey(RavensightClient.OptInKey));
        }

        [Fact]
        public async Task TheYesIsSavedStartsTheRunAndQueuesWhileTheSettingsCheckRuns()
        {
            Harness h = new Harness();
            RavensightClient client = h.Client(OptIn(h));
            client.Begin();
            Assert.False(client.Track("before_yes"));
            await client.InitAsync();

            client.SetTrackingEnabled(true);
            Assert.Equal(1, h.Storage.GetInt(RavensightClient.OptInKey, 0));
            Assert.Matches("^dev_[0-9a-f]{32}$", client.DeviceId);
            Assert.Equal(client.DeviceId, h.Storage.GetString(RavensightClient.DeviceIdKey));
            Assert.True(client.IsEnabled);

            // Tracked right after the yes, before the settings check has answered.
            Assert.True(client.Track("after_yes"));
            RavensightFlushResult early = await client.FlushAsync();
            Assert.Equal("not_ready", early.Skipped);
            Assert.Empty(h.Transport.Requests);

            await client.InitAsync();
            await client.FlushAsync();

            Assert.Equal("/settings", FakeTransport.PathOf(h.Transport.Requests[0]));
            Assert.Equal(new[] { "game_started", "after_yes" }, h.Transport.SentEventNames());
        }

        [Fact]
        public void TheYesIsRememberedByTheNextLaunch()
        {
            Harness h = new Harness();
            RavensightClient first = h.Client(OptIn(h));
            first.SetTrackingEnabled(true);
            string id = first.DeviceId;

            RavensightClient second = h.Client(OptIn(h));
            Assert.True(second.IsEnabled);
            Assert.Equal(id, second.DeviceId);
        }

        [Fact]
        public void SayingNoAgainKeepsTheDeviceIdAndResetForgetsIt()
        {
            Harness h = new Harness();
            RavensightClient first = h.Client(OptIn(h));
            first.SetTrackingEnabled(true);
            string id = first.DeviceId;
            first.SetTrackingEnabled(false);

            Assert.False(h.Storage.HasKey(RavensightClient.OptInKey));
            Assert.Equal(id, h.Storage.GetString(RavensightClient.DeviceIdKey));
            Assert.False(first.IsEnabled);

            // The next launch, still without a yes, does not even read the id.
            RavensightClient second = h.Client(OptIn(h));
            Assert.Null(second.DeviceId);
            Assert.False(second.IsEnabled);

            // A new yes brings the same id back.
            second.SetTrackingEnabled(true);
            Assert.Equal(id, second.DeviceId);

            second.SetTrackingEnabled(false);
            Assert.Null(second.ResetDeviceId());
            Assert.False(h.Storage.HasKey(RavensightClient.DeviceIdKey));
            second.SetTrackingEnabled(true);
            Assert.NotEqual(id, second.DeviceId);
        }

        [Fact]
        public void TheDefaultModeNeverWritesTheOptInKey()
        {
            Harness h = new Harness();
            RavensightClient client = h.Client();
            client.SetTrackingEnabled(false);
            client.SetTrackingEnabled(true);

            Assert.Equal(RavensightConsent.OptOut, client.Consent);
            Assert.False(h.Storage.HasKey(RavensightClient.OptInKey));
            Assert.True(client.IsEnabled);
        }

        [Fact]
        public void ASavedOptOutStillWinsInOptInMode()
        {
            Harness h = new Harness();
            h.Storage.SetInt(RavensightClient.OptInKey, 1);
            RavensightClient.WriteTrackingChoice(h.Storage, false);

            Assert.False(h.Client(OptIn(h)).IsEnabled);
        }

        // --- Distribution ---------------------------------------------------------------

        [Fact]
        public async Task DistributionIsSentOnTheSessionBody()
        {
            Harness h = new Harness();
            RavensightConfig config = h.Config();
            config.Distribution = "native-release";
            await h.Started(config);

            Dictionary<string, object> body = FakeTransport.BodyOf(h.Transport.To("/session")[0]);
            Assert.Equal("native-release", body["distribution"]);
            Assert.Equal(new[] { "deviceId", "distribution", "gameVersion", "platform", "sdk" }, body.Keys.OrderBy(k => k));
        }

        [Fact]
        public async Task NoDistributionMeansNoKey()
        {
            Harness h = new Harness();
            await h.Started();

            Dictionary<string, object> body = FakeTransport.BodyOf(h.Transport.To("/session")[0]);
            Assert.Equal(new[] { "deviceId", "gameVersion", "platform", "sdk" }, body.Keys.OrderBy(k => k));
        }

        [Theory]
        [InlineData("1234567890123456789012345678901234567890", true)]
        [InlineData("12345678901234567890123456789012345678901", false)]
        [InlineData("web\nitch", false)]
        [InlineData("tab\there", false)]
        [InlineData("", false)]
        public async Task DistributionIsAtMostFortyCharactersWithNoControlCharacters(string label, bool sent)
        {
            Harness h = new Harness();
            RavensightConfig config = h.Config();
            config.Distribution = label;
            RavensightClient client = h.Client(config);
            client.ReportStartupProblems();
            client.Begin();
            await client.InitAsync();

            Dictionary<string, object> body = FakeTransport.BodyOf(h.Transport.To("/session")[0]);
            Assert.Equal(sent, body.ContainsKey("distribution"));
            if (sent) Assert.Empty(h.Warnings);
            else if (label.Length > 0) Assert.Single(h.Warnings);
        }

        // --- A refused key ends the run ---------------------------------------------------

        [Fact]
        public async Task SettingsForbiddenForTheKeyEndsTheRun()
        {
            Harness h = new Harness();
            h.Transport.Routes["/settings"] = r => FakeTransport.Json(403, "{\"error\":\"invalid_api_key\"}");
            RavensightClient client = await h.Started();
            h.Clock.Advance(600000);
            await client.FlushAsync(true);

            Assert.Equal(RavensightReason.InvalidKey, client.StatusReason);
            Assert.False(client.IsEnabled);
            Assert.Equal(0, client.QueueCount);
            Assert.Single(h.Transport.Requests);
        }

        [Fact]
        public async Task ABatchUnauthorizedStillRefreshesTheSessionFirst()
        {
            Harness h = new Harness();
            int batches = 0;
            h.Transport.Routes["/track/batch"] = r => ++batches == 1
                ? FakeTransport.Json(401, "{\"error\":\"invalid_session\"}")
                : FakeTransport.Json(202, "{}");
            RavensightClient client = await h.Started();

            Assert.Equal(2, h.Transport.To("/session").Count);
            Assert.Equal(0, client.QueueCount);
            Assert.True(client.IsEnabled);
        }

        // --- Jitter -----------------------------------------------------------------------

        [Fact]
        public void JitterIsPlusOrMinusTwentyPercentWithinTheFloorAndCeiling()
        {
            Assert.Equal(8000L, RavensightClient.JitteredWaitMs(10000, 0.0));
            Assert.Equal(10000L, RavensightClient.JitteredWaitMs(10000, 0.5));
            Assert.True(RavensightClient.JitteredWaitMs(10000, 0.999999) <= 12000L);
            Assert.True(RavensightClient.JitteredWaitMs(10000, 0.999999) > 11990L);
            Assert.Equal(500L, RavensightClient.JitteredWaitMs(100, 0.0));
            Assert.Equal(300000L, RavensightClient.JitteredWaitMs(300000, 0.999999));

            System.Random source = new System.Random(7);
            for (int i = 0; i < 1000; i++)
            {
                long wait = RavensightClient.JitteredWaitMs(20000, source.NextDouble());
                Assert.True(wait >= 16000L && wait <= 24000L, "wait " + wait);
            }
        }

        [Fact]
        public async Task TheClientsRetryWaitIsJittered()
        {
            Harness low = new Harness();
            low.Transport.Routes["/track/batch"] = r => FakeTransport.Json(0, null);
            RavensightConfig config = low.Config();
            config.Random = () => 0.0;
            RavensightClient client = await low.Started(config);
            Assert.Equal(8000L, client.RetryInMs);

            Harness high = new Harness();
            high.Transport.Routes["/track/batch"] = r => FakeTransport.Json(0, null);
            config = high.Config();
            config.Random = () => 0.99;
            client = await high.Started(config);
            Assert.True(client.RetryInMs > 10000L && client.RetryInMs <= 12000L);
        }

        [Fact]
        public async Task RetryAfterIsObeyedExactlyWhateverTheRandomValue()
        {
            Harness h = new Harness();
            h.Transport.Routes["/track/batch"] = r => FakeTransport.Json(429, "{\"error\":\"rate_limited\"}", "7");
            RavensightConfig config = h.Config();
            config.Random = () => 0.0;
            RavensightClient client = await h.Started(config);

            Assert.Equal(7000L, client.RetryInMs);
        }

        // --- rs_event_id --------------------------------------------------------------------

        [Fact]
        public async Task EveryEventCarriesAWellFormedUniqueEventId()
        {
            Harness h = new Harness();
            RavensightClient client = await h.Started();
            Dictionary<string, object> mine = new Dictionary<string, object> { { "level", 3 } };
            client.Track("a", mine);
            client.Track("b");
            await client.FlushAsync();

            Assert.False(mine.ContainsKey(RavensightClient.EventIdKey)); // the caller's dictionary is untouched
            List<string> ids = SentData(h).Select(d => (string)d[RavensightClient.EventIdKey]).ToList();
            Assert.Equal(3, ids.Count); // game_started, a, b
            Assert.Equal(3, ids.Distinct().Count());
            string prefix = client.DeviceId.Substring(0, 16);
            foreach (string id in ids)
            {
                Assert.Matches("^[A-Za-z0-9_-]{1,64}$", id);
                Assert.True(id.StartsWith(prefix + "-"), id);
                Assert.Matches("^[A-Za-z0-9_]{16}-[0-9a-f]+-[0-9a-f]{8}-[0-9]+$", id);
            }
            // One nonce per launch.
            Assert.Single(ids.Select(id => id.Split('-')[2]).Distinct());
        }

        [Fact]
        public async Task AResendAfterAFailureCarriesTheSameIds()
        {
            Harness h = new Harness();
            bool offline = true;
            h.Transport.Routes["/track/batch"] = r => offline ? FakeTransport.Json(0, null) : FakeTransport.Json(202, "{}");
            RavensightClient client = await h.Started();
            client.Track("level_completed");
            h.Clock.Advance(10000);
            await client.FlushAsync();

            offline = false;
            h.Clock.Advance(60000);
            await client.FlushAsync();

            List<RavensightRequest> batches = h.Transport.To("/track/batch");
            List<string> first = FakeTransport.EventsOf(batches[batches.Count - 2])
                .Select(e => (string)((Dictionary<string, object>)e["data"])[RavensightClient.EventIdKey]).ToList();
            List<string> last = FakeTransport.EventsOf(batches[batches.Count - 1])
                .Select(e => (string)((Dictionary<string, object>)e["data"])[RavensightClient.EventIdKey]).ToList();
            Assert.Equal(first.ToArray(), last.ToArray());
            Assert.Equal(0, client.QueueCount);
        }

        [Fact]
        public void TheIdShapeHoldsForAnyDeviceId()
        {
            Assert.Matches("^[A-Za-z0-9_-]{1,64}$", RavensightClient.EventId("pt-run.42/a b", 1760000000L, "0a1b2c3d", 12));
            Assert.Matches("^[A-Za-z0-9_-]{1,64}$", RavensightClient.EventId(null, 0L, "0a1b2c3d", 1));
            Assert.Equal("dev_0123456789ab-68e77800-0a1b2c3d-5",
                RavensightClient.EventId("dev_0123456789abcdef0123456789abcdef", 1760000000L, "0a1b2c3d", 5));
            Assert.Matches("^[0-9a-f]{8}$", RavensightClient.NewLaunchNonce());
        }

        [Fact]
        public async Task TheLifecycleEventsCarryAnIdToo()
        {
            Harness h = new Harness();
            RavensightClient client = await h.Started();
            client.QueueGameExited();
            await client.FlushAsync();

            Assert.All(SentData(h), d => Assert.True(d.ContainsKey(RavensightClient.EventIdKey)));
        }

        // --- The 8 KB cap ---------------------------------------------------------------------

        [Fact]
        public async Task AnEventOverEightKilobytesIsDroppedWithOneWarning()
        {
            Harness h = new Harness();
            RavensightConfig config = h.Config();
            config.TrackLifecycleEvents = false;
            RavensightClient client = await h.Started(config);

            string big = new string('x', RavensightClient.MaxEventBytes);
            Assert.False(client.Track("huge", new Dictionary<string, object> { { "blob", big } }));
            Assert.False(client.Track("huge_again", new Dictionary<string, object> { { "blob", big } }));
            Assert.True(client.Track("fits", new Dictionary<string, object> { { "blob", new string('y', 7000) } }));
            await client.FlushAsync();

            Assert.Equal(new[] { "fits" }, h.Transport.SentEventNames());
            Assert.Equal(2, client.Stats.Dropped);
            Assert.Single(h.Warnings);
        }

        [Fact]
        public void TheSizeIsTheUtf8JsonOfTheData()
        {
            Dictionary<string, object> data = new Dictionary<string, object> { { "k", "é" } };
            Assert.Equal(10, RavensightClient.EventBytes(data)); // {"k":"é"} with é as two bytes
        }
    }
}
