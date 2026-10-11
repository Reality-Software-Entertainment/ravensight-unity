using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace RealitySE.Tests
{
    public class LifecycleTests
    {
        [Fact]
        public async Task GameStartedIsFirstWithTheStartTimeAndQueuedOnce()
        {
            Harness h = new Harness();
            long startSeconds = h.Clock.NowMs / 1000;
            RavensightClient client = h.Client();
            client.Begin();
            client.Track("early");
            h.Clock.Advance(5000); // the startup check takes a while
            client.Begin();
            await client.InitAsync();
            await client.InitAsync(); // a repeated settings check
            client.SetTrackingEnabled(true);
            await client.FlushAsync();

            List<Dictionary<string, object>> events = h.Transport.To("/track/batch").SelectMany(FakeTransport.EventsOf).ToList();
            Assert.Equal(new[] { "game_started", "early" }, events.Select(e => (string)e["event"]));
            Assert.Equal((double)startSeconds, events[0]["timestamp"]);
        }

        [Fact]
        public async Task NothingIsSentBeforeTheKillSwitchIsRead()
        {
            Harness h = new Harness();
            RavensightClient client = h.Client();
            client.Begin();
            RavensightFlushResult early = await client.FlushAsync();
            Assert.Equal("not_ready", early.Skipped);
            Assert.Empty(h.Transport.Requests);
        }

        [Fact]
        public async Task GameExitedIsQueuedOnce()
        {
            Harness h = new Harness();
            h.Transport.Routes["/track/batch"] = r => FakeTransport.Json(0, null);
            RavensightClient client = await h.Started();

            Assert.True(client.QueueGameExited());
            Assert.False(client.QueueGameExited());
            Assert.Equal(2, client.QueueCount); // game_started, game_exited
        }

        [Fact]
        public async Task LifecycleEventsCanBeTurnedOff()
        {
            Harness h = new Harness();
            RavensightConfig config = h.Config();
            config.TrackLifecycleEvents = false;
            RavensightClient client = await h.Started(config);
            client.QueueGameExited();
            await client.FlushAsync();
            Assert.Empty(h.Transport.To("/track/batch"));
        }

        [Fact]
        public async Task BackgroundSavesAMarkerAndAShortBreakQueuesNothing()
        {
            Harness h = new Harness();
            RavensightClient client = await h.Started();
            int batches = h.Transport.To("/track/batch").Count;

            client.EnterBackground();
            string marker = h.Storage.GetString(RavensightClient.BackgroundMarkerKey);
            Assert.NotNull(marker);
            Dictionary<string, object> parsed = RavensightJson.AsObject(RavensightJson.Parse(marker));
            Assert.Equal("tok_1", parsed["sessionToken"]);
            Assert.Equal((double)(h.Clock.NowMs / 1000), parsed["backgroundedAt"]);

            h.Clock.Advance(29 * 60 * 1000);
            await client.EnterForegroundAsync();

            Assert.Null(h.Storage.GetString(RavensightClient.BackgroundMarkerKey));
            Assert.Equal(0, client.QueueCount);
            Assert.Equal(batches, h.Transport.To("/track/batch").Count);
            Assert.Equal("tok_1", client.SessionToken);
        }

        [Fact]
        public async Task ThirtyMinutesInTheBackgroundEndsTheSessionAtTheTimeItLeft()
        {
            Harness h = new Harness();
            RavensightClient client = await h.Started();
            long leftAt = h.Clock.NowMs / 1000;

            client.EnterBackground();
            h.Clock.Advance(30 * 60 * 1000);
            await client.EnterForegroundAsync();

            List<RavensightRequest> batches = h.Transport.To("/track/batch");
            RavensightRequest exit = batches[batches.Count - 2];
            RavensightRequest restart = batches[batches.Count - 1];

            Assert.Equal("tok_1", exit.Headers["X-Session-Token"]);
            Dictionary<string, object> exitEvent = FakeTransport.EventsOf(exit).Single();
            Assert.Equal("game_exited", exitEvent["event"]);
            Assert.Equal((double)leftAt, exitEvent["timestamp"]);

            Assert.Equal("tok_2", restart.Headers["X-Session-Token"]);
            Assert.Equal("game_started", FakeTransport.EventsOf(restart).Single()["event"]);
            Assert.Null(h.Storage.GetString(RavensightClient.BackgroundMarkerKey));
        }

        [Fact]
        public async Task ColdLaunchSendsTheSavedExitFirstThenDeletesTheMarker()
        {
            Harness h = new Harness();
            long expiresAt = h.Clock.NowMs / 1000 + 3600;
            h.Storage.SetString(RavensightClient.BackgroundMarkerKey,
                "{\"sessionToken\":\"old_tok\",\"expiresAt\":" + expiresAt + ",\"backgroundedAt\":1759990000}");

            RavensightClient client = await h.Started();

            RavensightRequest first = h.Transport.Requests[0];
            Assert.Equal("/track/batch", FakeTransport.PathOf(first));
            Assert.Equal("old_tok", first.Headers["X-Session-Token"]);
            Dictionary<string, object> exit = FakeTransport.EventsOf(first).Single();
            Assert.Equal("game_exited", exit["event"]);
            Assert.Equal(1759990000.0, exit["timestamp"]);
            Assert.Null(h.Storage.GetString(RavensightClient.BackgroundMarkerKey));
            Assert.Equal("/settings", FakeTransport.PathOf(h.Transport.Requests[1]));
        }

        [Fact]
        public async Task ColdLaunchMakesOneAttemptAndDeletesTheMarkerWhateverTheAnswer()
        {
            Harness h = new Harness();
            int exitAttempts = 0;
            h.Transport.Routes["/track/batch"] = r =>
            {
                if (r.Headers["X-Session-Token"] == "old_tok") exitAttempts++;
                return FakeTransport.Json(0, null);
            };
            h.Storage.SetString(RavensightClient.BackgroundMarkerKey,
                "{\"sessionToken\":\"old_tok\",\"expiresAt\":" + (h.Clock.NowMs / 1000 + 3600) + ",\"backgroundedAt\":1759990000}");

            await h.Started();

            Assert.Equal(1, exitAttempts);
            Assert.Null(h.Storage.GetString(RavensightClient.BackgroundMarkerKey));
        }

        [Fact]
        public async Task AnExpiredOrOptedOutMarkerIsDeletedWithoutARequest()
        {
            Harness expired = new Harness();
            expired.Storage.SetString(RavensightClient.BackgroundMarkerKey,
                "{\"sessionToken\":\"old_tok\",\"expiresAt\":" + (expired.Clock.NowMs / 1000 - 1) + ",\"backgroundedAt\":1759990000}");
            await expired.Started();
            Assert.Equal("/settings", FakeTransport.PathOf(expired.Transport.Requests[0]));
            Assert.Null(expired.Storage.GetString(RavensightClient.BackgroundMarkerKey));

            Harness optedOut = new Harness();
            RavensightClient.WriteTrackingChoice(optedOut.Storage, false);
            optedOut.Storage.SetString(RavensightClient.BackgroundMarkerKey,
                "{\"sessionToken\":\"old_tok\",\"expiresAt\":" + (optedOut.Clock.NowMs / 1000 + 3600) + ",\"backgroundedAt\":1759990000}");
            await optedOut.Started();
            Assert.Empty(optedOut.Transport.Requests);
            Assert.Null(optedOut.Storage.GetString(RavensightClient.BackgroundMarkerKey));
        }
    }
}
