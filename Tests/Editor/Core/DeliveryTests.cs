using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace RealitySE.Tests
{
    public class DeliveryTests
    {
        [Theory]
        [InlineData(null, "https://api.ravensight.io/api/v1")]
        [InlineData("", "https://api.ravensight.io/api/v1")]
        [InlineData("   ", "https://api.ravensight.io/api/v1")]
        [InlineData("https://api.ravensight.io", "https://api.ravensight.io/api/v1")]
        [InlineData("https://api.ravensight.io/", "https://api.ravensight.io/api/v1")]
        [InlineData("https://api.ravensight.io///", "https://api.ravensight.io/api/v1")]
        [InlineData("https://api.ravensight.io/api/v1", "https://api.ravensight.io/api/v1")]
        [InlineData("https://api.ravensight.io/api/v1/", "https://api.ravensight.io/api/v1")]
        [InlineData("  http://localhost:3000/api/v1//  ", "http://localhost:3000/api/v1")]
        [InlineData("https://example.test/ravensight", "https://example.test/ravensight/api/v1")]
        public void ApiUrlForms(string input, string expected)
        {
            Assert.Equal(expected, RavensightClient.NormalizeApiUrl(input));
        }

        [Fact]
        public void DefaultIsTheBareHost()
        {
            Assert.Equal("https://api.ravensight.io", RavensightClient.DefaultApiUrl);
            Assert.Equal("0.2.1", RavensightClient.SdkVersion);
        }

        [Fact]
        public async Task SettingsUnauthorizedMakesOneRequestThenNone()
        {
            Harness h = new Harness();
            h.Transport.Routes["/settings"] = r => FakeTransport.Json(401, "{\"error\":\"invalid_api_key\"}");
            RavensightClient client = h.Client();
            client.Begin();

            RavensightInitResult init = await client.InitAsync();
            for (int i = 0; i < 120; i++) client.Track("e" + i);
            h.Clock.Advance(600000);
            RavensightFlushResult auto = await client.AutoFlushAsync();

            Assert.Equal(RavensightReason.InvalidKey, init.Reason);
            Assert.Equal(RavensightReason.InvalidKey, client.StatusReason);
            Assert.Equal(RavensightReason.InvalidKey, auto.Skipped);
            Assert.Single(h.Transport.Requests);
            Assert.True(client.QueueCount > 0); // kept, the cap applies
            Assert.Equal(new[] { RavensightClient.InvalidKeyWarning }, h.Warnings);
        }

        [Fact]
        public async Task ExplicitFlushAfterARefusedKeyMakesOneMoreAttempt()
        {
            Harness h = new Harness();
            h.Transport.Routes["/session"] = r => FakeTransport.Json(401, "{\"error\":\"invalid_api_key\"}");
            RavensightClient client = await h.Started();
            int afterBoot = h.Transport.Requests.Count;
            Assert.Equal(RavensightReason.InvalidKey, client.StatusReason);

            await client.AutoFlushAsync();
            Assert.Equal(afterBoot, h.Transport.Requests.Count);

            await client.FlushAsync();
            Assert.Equal(afterBoot + 1, h.Transport.Requests.Count);
            Assert.Single(h.Warnings); // logged once

            // A key that works again resumes automatic sending.
            h.Transport.Routes.Remove("/session");
            await client.FlushAsync();
            Assert.Null(client.StatusReason);
            Assert.Contains("game_started", h.Transport.SentEventNames());
        }

        [Fact]
        public async Task SessionTrackingDisabledIsTheKillSwitch()
        {
            Harness h = new Harness();
            h.Transport.Routes["/session"] = r => FakeTransport.Json(403, "{\"error\":\"tracking_disabled\"}");
            RavensightClient client = h.Client();
            client.Begin();

            RavensightInitResult init = await client.InitAsync();
            int after = h.Transport.Requests.Count;
            Assert.False(client.Track("x"));
            h.Clock.Advance(600000);
            await client.FlushAsync();
            Assert.Empty(await client.FetchSuggestionsAsync());

            Assert.Equal(RavensightReason.TrackingDisabled, init.Reason);
            Assert.False(client.TrackingEnabled);
            Assert.False(client.IsActive);
            Assert.Equal(0, client.QueueCount);
            Assert.Equal(after, h.Transport.Requests.Count);
            Assert.Single(h.Warnings);
        }

        [Fact]
        public async Task SettingsTrackingFalseIsTheKillSwitchWithoutASession()
        {
            Harness h = new Harness();
            h.Transport.Routes["/settings"] = r => FakeTransport.Json(200, "{\"trackingEnabled\":false}");
            RavensightClient client = h.Client();
            client.Begin();
            RavensightInitResult init = await client.InitAsync();

            Assert.Equal(RavensightReason.TrackingDisabled, init.Reason);
            Assert.Single(h.Transport.Requests);
            Assert.Equal(0, client.QueueCount);
        }

        [Fact]
        public async Task RateLimitWaitsExactlyRetryAfter()
        {
            Harness h = new Harness();
            bool limited = true;
            h.Transport.Routes["/track/batch"] = r => limited
                ? FakeTransport.Json(429, "{\"error\":\"rate_limited\"}", "7")
                : FakeTransport.Json(202, "{}");
            RavensightClient client = await h.Started();

            Assert.Equal(RavensightReason.RateLimited, client.StatusReason);
            Assert.Equal(7000, client.RetryInMs);
            int before = h.Transport.Requests.Count;

            h.Clock.Advance(6999);
            RavensightFlushResult early = await client.FlushAsync(true);
            Assert.Equal("backoff", early.Skipped);
            Assert.Equal(before, h.Transport.Requests.Count);

            limited = false;
            h.Clock.Advance(1);
            RavensightFlushResult flushed = await client.FlushAsync();
            Assert.Equal(1, flushed.Sent);
            Assert.Null(client.StatusReason);
        }

        [Fact]
        public async Task ServiceUnavailableHonorsRetryAfter()
        {
            Harness h = new Harness();
            h.Transport.Routes["/track/batch"] = r =>
                FakeTransport.Json(503, "{\"error\":\"storage_unavailable\"}", "60");
            RavensightClient client = await h.Started();

            Assert.Equal(60000, client.RetryInMs);
            Assert.Equal(1, client.QueueCount);
        }

        [Fact]
        public async Task OfflineBacksOffFromTenSecondsAndKeepsEvents()
        {
            Harness h = new Harness();
            h.Transport.Routes["/track/batch"] = r => FakeTransport.Json(0, null);
            RavensightClient client = await h.Started();
            Assert.Equal(RavensightReason.Offline, client.StatusReason);
            Assert.Equal(10000, client.RetryInMs);

            h.Clock.Advance(10000);
            await client.FlushAsync();
            Assert.Equal(20000, client.RetryInMs);
            Assert.Equal(1, client.QueueCount);
        }

        [Fact]
        public async Task BadRequestHalvesTheBatchAndDropsOnlyTheEventThatStillFails()
        {
            Harness h = new Harness();
            h.Transport.Routes["/track/batch"] = r =>
            {
                List<Dictionary<string, object>> events = FakeTransport.EventsOf(r);
                if (events.Any(e => (string)e["event"] == "poison")) return FakeTransport.Json(400, "{\"error\":\"invalid_event\"}");
                return FakeTransport.Json(202, "{}");
            };
            RavensightConfig config = h.Config();
            config.TrackLifecycleEvents = false;
            RavensightClient client = await h.Started(config);

            for (int i = 0; i < 10; i++) client.Track("e" + i);
            client.Track("poison");
            for (int i = 10; i < 20; i++) client.Track("e" + i);
            RavensightFlushResult result = await client.FlushAsync();

            Assert.Equal(20, result.Sent);
            Assert.Equal(0, client.QueueCount);
            Assert.Equal(1, client.Stats.Dropped);
            Assert.Single(h.Warnings);
            List<int> sizes = h.Transport.To("/track/batch").Select(r => FakeTransport.EventsOf(r).Count).ToList();
            // 21 refused, 10 accepted, 11 refused and halved down to the poison alone, then the last 10.
            Assert.Equal(new[] { 21, 10, 11, 5, 2, 1, 10 }, sizes);
        }

        [Fact]
        public async Task BatchesHoldAtMostFiftyAndAFullBatchSendsAtOnce()
        {
            Harness h = new Harness();
            RavensightConfig config = h.Config();
            config.TrackLifecycleEvents = false;
            RavensightClient client = await h.Started(config);

            for (int i = 0; i < 49; i++) client.Track("e" + i);
            Assert.Empty(h.Transport.To("/track/batch")); // track only enqueues
            client.Track("e49");
            Assert.Single(h.Transport.To("/track/batch")); // the 50th sends

            for (int i = 50; i < 120; i++) client.Track("e" + i);
            await client.FlushAsync();

            Assert.All(h.Transport.To("/track/batch"), r => Assert.True(FakeTransport.EventsOf(r).Count <= 50));
            Assert.Equal(120, h.Transport.SentEventNames().Count);
            Assert.Equal(0, client.QueueCount);
        }

        [Fact]
        public async Task QueueCapIsFiveHundredAndDropsTheOldest()
        {
            Harness h = new Harness();
            h.Transport.Routes["/track/batch"] = r => FakeTransport.Json(0, null);
            RavensightConfig config = h.Config();
            config.TrackLifecycleEvents = false;
            RavensightClient client = await h.Started(config);

            for (int i = 0; i < 600; i++) client.Track("e" + i);

            Assert.Equal(500, client.QueueCount);
            Assert.Equal(100, client.Stats.Dropped);
        }

        [Fact]
        public async Task BatchUnauthorizedReopensTheSessionAtMostTwiceThenBacksOff()
        {
            Harness h = new Harness();
            h.Transport.Routes["/track/batch"] = r => FakeTransport.Json(401, "{\"error\":\"invalid_session\"}");
            RavensightClient client = await h.Started();

            Assert.Equal(3, h.Transport.To("/track/batch").Count);
            Assert.Equal(3, h.Transport.To("/session").Count); // the first plus two re-opens
            Assert.True(client.RetryInMs > 0);
            Assert.Equal(1, client.QueueCount);
        }

        [Fact]
        public async Task OtherForbiddenIsAnInvalidKey()
        {
            Harness h = new Harness();
            h.Transport.Routes["/session"] = r => FakeTransport.Json(403, "{\"error\":\"game_inactive_key\"}");
            RavensightClient client = await h.Started();
            Assert.Equal(RavensightReason.InvalidKey, client.StatusReason);
            Assert.True(client.IsActive);
            Assert.Equal(1, client.QueueCount);
        }

        [Fact]
        public async Task StatusChangesAreReported()
        {
            Harness h = new Harness();
            h.Transport.Routes["/track/batch"] = r => FakeTransport.Json(0, null);
            RavensightClient client = await h.Started();
            h.Transport.Routes.Remove("/track/batch");
            h.Clock.Advance(10000);
            await client.FlushAsync();
            client.SetTrackingEnabled(false);

            Assert.Equal(new string[] { RavensightReason.Offline, null, RavensightReason.Disabled }, h.Statuses);
        }

        [Fact]
        public async Task JoinTokenIsSentOnceAndARefusedOneFallsBackToAPlainSession()
        {
            Harness h = new Harness();
            h.Transport.Routes["/session"] = r =>
            {
                if (FakeTransport.BodyOf(r).ContainsKey("joinToken"))
                {
                    return FakeTransport.Json(401, "{\"error\":\"invalid_join_token\"}");
                }
                return FakeTransport.Json(201, "{\"token\":\"plain\",\"expiresIn\":86400}");
            };
            RavensightConfig config = h.Config();
            config.JoinToken = "player_session_token";
            RavensightClient client = await h.Started(config);

            List<RavensightRequest> sessions = h.Transport.To("/session");
            Assert.Equal(2, sessions.Count);
            Assert.Equal("player_session_token", FakeTransport.BodyOf(sessions[0])["joinToken"]);
            Assert.False(FakeTransport.BodyOf(sessions[1]).ContainsKey("joinToken"));
            Assert.Equal("plain", client.SessionToken);
            Assert.Null(client.StatusReason);
        }

        [Fact]
        public async Task SuggestionsAreReturnedWhileActive()
        {
            Harness h = new Harness();
            RavensightClient client = await h.Started();
            Assert.Single(await client.FetchSuggestionsAsync());
        }
    }
}
