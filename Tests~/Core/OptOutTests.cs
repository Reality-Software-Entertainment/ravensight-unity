using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace RealitySE.Tests
{
    public class OptOutTests
    {
        [Fact]
        public async Task DisabledStartMakesZeroRequests()
        {
            Harness h = new Harness();
            RavensightConfig config = h.Config();
            config.Enabled = false;
            RavensightClient client = h.Client(config);

            client.Begin();
            Assert.False(client.Track("level_started"));
            RavensightInitResult init = await client.InitAsync();
            await client.FlushAsync();
            await client.AutoFlushAsync();
            Assert.Empty(await client.FetchSuggestionsAsync());
            RavensightException refused = await Assert.ThrowsAsync<RavensightException>(
                () => client.SubmitFeedbackAsync("hello", "bug", 3));

            Assert.Equal(RavensightReason.Disabled, init.Reason);
            Assert.Equal("disabled", refused.Code);
            Assert.Equal(0, client.QueueCount);
            Assert.Empty(h.Transport.Requests);
            Assert.False(client.SettingsChecked);
        }

        [Fact]
        public async Task EnablingLaterRunsTheBootAndQueuesGameStartedOnce()
        {
            Harness h = new Harness();
            RavensightConfig config = h.Config();
            config.Enabled = false;
            RavensightClient client = h.Client(config);
            client.Begin();
            await client.InitAsync();
            Assert.Empty(h.Transport.Requests);

            client.Enabled = true;
            await client.InitAsync();
            await client.FlushAsync();
            Assert.Single(h.Transport.To("/settings"));
            Assert.Equal(new[] { "game_started" }, h.Transport.SentEventNames());

            // The once-guard holds across toggles and a repeated check.
            client.Enabled = false;
            client.Enabled = true;
            client.SetTrackingEnabled(true);
            client.Begin();
            await client.InitAsync();
            client.Track("next");
            await client.FlushAsync();
            Assert.Equal(new[] { "game_started", "next" }, h.Transport.SentEventNames());
        }

        [Fact]
        public async Task SavedOptOutMakesZeroRequestsAcrossANewClient()
        {
            Harness h = new Harness();
            RavensightClient first = await h.Started();
            first.SetTrackingEnabled(false);
            int before = h.Transport.Requests.Count;

            RavensightClient second = h.Client();
            second.Begin();
            Assert.True(second.OptedOut);
            Assert.False(second.IsActive);
            Assert.False(second.Track("anything"));
            RavensightInitResult init = await second.InitAsync();
            await second.FlushAsync();
            Assert.Empty(await second.FetchSuggestionsAsync());

            Assert.Equal(RavensightReason.Disabled, init.Reason);
            Assert.Equal(before, h.Transport.Requests.Count);
        }

        [Fact]
        public async Task OptOutWrittenBeforeAnyClientExistsWins()
        {
            Harness h = new Harness();
            RavensightClient.WriteTrackingChoice(h.Storage, false);

            RavensightClient client = h.Client(h.Config());
            client.Begin();
            await client.InitAsync();
            client.Track("x");
            await client.FlushAsync();

            Assert.Empty(h.Transport.Requests);
            Assert.Equal(0, h.Storage.GetInt(RavensightClient.TrackingEnabledKey, 1));
        }

        [Fact]
        public async Task OptOutIsWrittenEvenWhenTheClientWasConstructedDisabled()
        {
            Harness h = new Harness();
            RavensightConfig config = h.Config();
            config.Enabled = false;
            RavensightClient client = h.Client(config);

            client.SetTrackingEnabled(false);
            Assert.Equal(0, h.Storage.GetInt(RavensightClient.TrackingEnabledKey, 1));

            client.Enabled = true;
            await client.InitAsync();
            Assert.Empty(h.Transport.Requests);
        }

        [Fact]
        public async Task TurningTrackingOffClearsTheQueueAndStopsSending()
        {
            Harness h = new Harness();
            h.Transport.Routes["/track/batch"] = r => FakeTransport.Json(0, null);
            RavensightClient client = await h.Started();
            client.Track("a");
            client.Track("b");
            Assert.True(client.QueueCount > 0);

            client.SetTrackingEnabled(false);
            int before = h.Transport.Requests.Count;
            h.Clock.Advance(600000);
            await client.AutoFlushAsync();
            await client.FlushAsync();

            Assert.Equal(0, client.QueueCount);
            Assert.Equal(before, h.Transport.Requests.Count);
            Assert.Equal(RavensightReason.Disabled, client.StatusReason);
        }

        [Fact]
        public async Task SuggestionsAndFeedbackWhileOptedOutMakeNoRequest()
        {
            Harness h = new Harness();
            RavensightClient.WriteTrackingChoice(h.Storage, false);
            RavensightClient client = h.Client();

            Assert.Empty(await client.FetchSuggestionsAsync());
            RavensightException e = await Assert.ThrowsAsync<RavensightException>(
                () => client.SubmitFeedbackAsync("too hard", "complaint", 2));
            Assert.Equal("disabled", e.Code);
            Assert.Empty(h.Transport.Requests);
        }

        [Fact]
        public async Task ResetDeviceIdMintsSavesClearsAndDropsTheSession()
        {
            Harness h = new Harness();
            h.Transport.Routes["/track/batch"] = r => FakeTransport.Json(0, null);
            RavensightClient client = await h.Started();
            string oldId = client.DeviceId;
            string oldToken = client.SessionToken;
            Assert.NotNull(oldToken);
            client.Track("before_reset");

            string newId = client.ResetDeviceId();

            Assert.NotEqual(oldId, newId);
            Assert.Equal(newId, client.DeviceId);
            Assert.Equal(newId, h.Storage.GetString(RavensightClient.DeviceIdKey));
            Assert.Equal(0, client.QueueCount);
            Assert.Null(client.SessionToken);

            h.Transport.Routes.Remove("/track/batch");
            h.Clock.Advance(600000);
            client.Track("after_reset");
            await client.FlushAsync();
            var session = h.Transport.To("/session").Last();
            Assert.Equal(newId, FakeTransport.BodyOf(session)["deviceId"]);
            Assert.Equal(new[] { "after_reset" }, h.Transport.SentEventNames().Where(n => n != "game_started"));
        }

        [Fact]
        public void DeviceIdIsDevPlus32HexAndASavedIdIsNeverRewritten()
        {
            Harness h = new Harness();
            RavensightClient first = h.Client();
            Assert.Matches("^dev_[0-9a-f]{32}$", first.DeviceId);

            Harness legacy = new Harness();
            legacy.Storage.SetString(RavensightClient.DeviceIdKey, "dev_LEGACY-format");
            RavensightClient second = legacy.Client();
            Assert.Equal("dev_LEGACY-format", second.DeviceId);
            Assert.Equal("dev_LEGACY-format", legacy.Storage.GetString(RavensightClient.DeviceIdKey));
        }
    

        [Fact]
        public async Task HoldDisabledCannotBeLiftedByEnabled()
        {
            Harness h = new Harness();
            RavensightConfig config = h.Config();
            config.HoldDisabled = true;
            RavensightClient client = h.Client(config);
            client.Begin();

            client.Enabled = true;
            await client.InitAsync();
            client.Track("x");
            await client.FlushAsync();

            Assert.False(client.Enabled);
            Assert.Empty(h.Transport.Requests);
            Assert.Single(h.Warnings);
        }
    }
}
