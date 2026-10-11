using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace RealitySE.Tests
{
    public class PlaytestTests
    {
        private static RavensightConfig PlaytestConfig(Harness h, string runId)
        {
            RavensightConfig config = h.Config();
            config.Playtest = new RavensightPlaytestContext("pt_token_abc", runId, "pt_job_1", "speedrunner");
            return config;
        }

        [Fact]
        public async Task PlaytestUsesTheRunIdTheBareHeaderAndWritesNothing()
        {
            Harness h = new Harness();
            h.Storage.SetInt(RavensightClient.TrackingEnabledKey, 0); // the player opted out
            int writesBefore = h.Storage.Writes;

            RavensightClient client = await h.Started(PlaytestConfig(h, "run_42"));
            client.Track("level_started");
            await client.FetchSuggestionsAsync();
            await client.SubmitFeedbackAsync("the jump is hard", "playtest", 2);
            await client.FlushAsync();
            client.SetTrackingEnabled(false); // a no-op that logs
            client.ResetDeviceId(); // a no-op that logs
            await client.FlushAsync();

            Assert.True(client.IsPlaytest);
            Assert.Equal("pt-run_42", client.DeviceId);
            Assert.True(client.IsActive);
            Assert.Equal(writesBefore, h.Storage.Writes);
            Assert.False(h.Storage.HasKey(RavensightClient.DeviceIdKey));
            Assert.All(h.Transport.Requests, r => Assert.Equal("pt_token_abc", r.Headers[RavensightClient.PlaytestHeader]));
            Assert.Equal("pt-run_42", FakeTransport.BodyOf(h.Transport.To("/session")[0])["deviceId"]);
            Assert.Equal(2, h.Warnings.Count);

            Dictionary<string, object> data = (Dictionary<string, object>)FakeTransport
                .EventsOf(h.Transport.To("/track/batch")[0])[0]["data"];
            Assert.Equal(true, data["synthetic"]);
            Assert.Equal("run_42", data["pt_run"]);
            Assert.Equal("pt_job_1", data["pt_job"]);
            Assert.Equal("speedrunner", data["persona"]);
        }

        [Fact]
        public async Task JobClosedStopsTheRunAndClearsTheQueue()
        {
            Harness h = new Harness();
            h.Transport.Routes["/track/batch"] = r => FakeTransport.Json(403, "{\"error\":\"playtest_job_closed\"}");
            RavensightClient client = await h.Started(PlaytestConfig(h, "run_1"));
            int after = h.Transport.Requests.Count;

            Assert.Equal(RavensightReason.PlaytestJobClosed, client.StatusReason);
            Assert.False(client.IsActive);
            Assert.Equal(0, client.QueueCount);
            Assert.False(client.Track("more"));
            h.Clock.Advance(600000);
            await client.FlushAsync();
            Assert.Equal(after, h.Transport.Requests.Count);
            Assert.Single(h.Warnings);
        }

        [Fact]
        public async Task InvalidPlaytestTokenStopsWithSessionFailed()
        {
            Harness h = new Harness();
            h.Transport.Routes["/session"] = r => FakeTransport.Json(403, "{\"error\":\"invalid_playtest_token\"}");
            RavensightClient client = await h.Started(PlaytestConfig(h, "run_1"));

            Assert.Equal(RavensightReason.SessionFailed, client.StatusReason);
            Assert.False(client.IsActive);
        }

        [Fact]
        public async Task ATokenWithoutARunIdNeverRunsAsARealPlayer()
        {
            Harness h = new Harness();
            RavensightClient client = h.Client(PlaytestConfig(h, null));
            client.ReportStartupProblems();
            client.Begin();
            RavensightInitResult init = await client.InitAsync();
            client.Track("x");
            await client.FlushAsync();

            Assert.Equal(RavensightReason.SessionFailed, init.Reason);
            Assert.Empty(h.Transport.Requests);
            Assert.Equal(0, h.Storage.Writes);
            Assert.Single(h.Warnings);
        }

        [Fact]
        public void ResolveTakesTheFirstSourceThatHasAToken()
        {
            Dictionary<string, string> env = new Dictionary<string, string>
            {
                { "RAVENSIGHT_PLAYTEST_TOKEN", "env_token" },
                { "RAVENSIGHT_PLAYTEST_RUN_ID", "env_run" },
                { "RAVENSIGHT_PLAYTEST_JOB_ID", "job" },
            };
            string[] args = { "game.exe", "--ravensight-playtest-token=arg_token", "--ravensight-playtest-run-id=arg_run" };
            System.Func<string, string> read = k => env.TryGetValue(k, out string v) ? v : null;

            RavensightPlaytestContext explicitWins = RavensightPlaytestContext.Resolve(
                new RavensightPlaytestContext("explicit", "run_x", null, null), read, args);
            Assert.Equal("explicit", explicitWins.Token);

            RavensightPlaytestContext fromEnv = RavensightPlaytestContext.Resolve(null, read, args);
            Assert.Equal("env_token", fromEnv.Token);
            Assert.Equal("env_run", fromEnv.RunId);
            Assert.Equal("job", fromEnv.JobId);

            RavensightPlaytestContext fromArgs = RavensightPlaytestContext.Resolve(null, k => null, args);
            Assert.Equal("arg_token", fromArgs.Token);
            Assert.Equal("arg_run", fromArgs.RunId);

            Assert.Null(RavensightPlaytestContext.Resolve(null, k => null, new[] { "game.exe" }));
        }
    }

    public class FeedbackTests
    {
        [Theory]
        [InlineData("balance", 3, "invalid_category")]
        [InlineData("gameplay", 0, "invalid_category")]
        [InlineData("bug", 6, "invalid_rating")]
        [InlineData("bug", -1, "invalid_rating")]
        [InlineData(null, 9, "invalid_rating")]
        public async Task InvalidFeedbackFailsLocallyWithNoRequest(string category, int rating, string code)
        {
            Harness h = new Harness();
            RavensightClient client = await h.Started();
            int before = h.Transport.Requests.Count;

            RavensightException e = await Assert.ThrowsAsync<RavensightException>(
                () => client.SubmitFeedbackAsync("message", category, rating));

            Assert.Equal(code, e.Code);
            Assert.Equal(before, h.Transport.Requests.Count);
        }

        [Fact]
        public async Task ValidFeedbackSendsCategoryAndRatingAndOmitsAZeroRating()
        {
            Harness h = new Harness();
            RavensightClient client = await h.Started();

            await client.SubmitFeedbackAsync("The boss fight drags", "suggestion", 3);
            await client.SubmitFeedbackAsync("Love it", "praise", 0);

            List<RavensightRequest> sent = h.Transport.To("/feedback");
            Dictionary<string, object> first = FakeTransport.BodyOf(sent[0]);
            Dictionary<string, object> second = FakeTransport.BodyOf(sent[1]);
            Assert.Equal("suggestion", first["category"]);
            Assert.Equal(3.0, first["rating"]);
            Assert.False(second.ContainsKey("rating"));
        }

        [Fact]
        public void TheSixCategoriesAreExported()
        {
            Assert.Equal(
                new[] { "bug", "suggestion", "complaint", "praise", "playtest", "other" },
                RavensightClient.FeedbackCategories.ToArray());
        }
    }
}
