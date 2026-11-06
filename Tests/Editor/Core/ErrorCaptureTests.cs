using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace RealitySE.Tests
{
    public class ErrorCaptureTests
    {
        private const string UnityStack =
            "UnityEngine.Debug:LogError (object)\n" +
            "PlayerController:Update () (at Assets/Scripts/PlayerController.cs:42)\n";

        private static RavensightLogEntry Log(RavensightLogLevel level, string text, string stack = null)
        {
            RavensightLogEntry entry = new RavensightLogEntry();
            entry.Level = level;
            entry.Text = text;
            entry.Stack = stack;
            return entry;
        }

        private static RavensightLogEntry Thrown(string source, string type, string message, string stack)
        {
            RavensightLogEntry entry = Log(RavensightLogLevel.Exception, message, stack);
            entry.Source = source;
            entry.Type = type;
            return entry;
        }

        private static void Settings(Harness h, string errorCapture)
        {
            string body = errorCapture == null
                ? "{\"trackingEnabled\":true}"
                : "{\"trackingEnabled\":true,\"errorCapture\":" + errorCapture + "}";
            h.Transport.Routes["/settings"] = r => FakeTransport.Json(200, body);
        }

        private static async Task<List<Dictionary<string, object>>> SentErrors(Harness h, RavensightClient client)
        {
            await client.FlushAsync();
            return h.Transport.To("/track/batch").SelectMany(FakeTransport.EventsOf)
                .Select(e => (Dictionary<string, object>)e["data"])
                .Where(d => d.ContainsKey("rs_kind") && (string)d["rs_kind"] == "error")
                .ToList();
        }

        private static string Stack(string frame)
        {
            return "Game." + frame + ":Run () (at Assets/Game.cs:1)\n";
        }

        // --- Pure rules --------------------------------------------------------

        [Theory]
        [InlineData("mail bob@example.com now", "mail [email] now")]
        [InlineData("auth Bearer abc.def-ghi", "auth [token]")]
        [InlineData("jwt eyJhbGciOi.eyJzdWIiOi.c2lnbmF0dXJl end", "jwt [token] end")]
        [InlineData("key gt_live_abcdef123 end", "key [token] end")]
        [InlineData("run 0123456789abcdef0123456789abcdef!", "run [token]!")]
        [InlineData("at handle_the_very_long_snake_case_function_name_here", "at handle_the_very_long_snake_case_function_name_here")]
        [InlineData("key sk9aB3cD4eF5gH6iJ7kL8mN9oP0qR1sT2uV3wX4y end", "key [token] end")]
        [InlineData("at Assets/Scripts/Inventory2/InventoryControllerBehaviour.cs:42", "at Assets/Scripts/Inventory2/InventoryControllerBehaviour.cs:42")]
        [InlineData("aws wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY1 leaked", "aws [token] leaked")]
        [InlineData("from 192.168.1.20 ok", "from [ip] ok")]
        [InlineData("from 2001:db8:0:0:0:0:0:1 ok", "from [ip] ok")]
        [InlineData("file /Users/jacob/Game/a.cs", "file ~/Game/a.cs")]
        [InlineData("file /home/jacob/game", "file ~/game")]
        [InlineData("file C:\\Users\\Jacob\\Game", "file ~\\Game")]
        [InlineData("GET https://api.example.com/v1/x?token=1#frag done", "GET https://api.example.com/v1/x done")]
        public void ScrubFollowsTheContract(string input, string expected)
        {
            Assert.Equal(expected, RavensightErrorRules.Scrub(input));
        }

        /// <summary>The shared vectors, copied from the platform repo's docs/errors/scrub-vectors.json.</summary>
        [Fact]
        public void ScrubMatchesEverySharedVector()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ThisFile()), "scrub-vectors.json");
            List<object> cases = RavensightJson.GetArray(RavensightJson.Parse(System.IO.File.ReadAllText(path)), "cases");
            Assert.NotNull(cases);
            Assert.True(cases.Count >= 18);
            foreach (object item in cases)
            {
                string name = RavensightJson.GetString(item, "name", "");
                string input = RavensightJson.GetString(item, "in", null);
                string expected = RavensightJson.GetString(item, "out", null);
                string actual = RavensightErrorRules.Scrub(input);
                Assert.True(expected == actual, name + ": expected \"" + expected + "\", got \"" + actual + "\"");
            }
        }

        private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "")
        {
            return path;
        }

        [Theory]
        [InlineData("NullReferenceException: Object reference not set", "NullReferenceException", "Object reference not set")]
        [InlineData("System.InvalidOperationException: bad state", "InvalidOperationException", "bad state")]
        [InlineData("Exception: ", "Exception", "")]
        [InlineData("something broke here", "Exception", "something broke here")]
        public void TheExceptionLineSplitsIntoTypeAndMessage(string line, string type, string message)
        {
            string t, m;
            RavensightErrorRules.ParseExceptionLine(line, out t, out m);
            Assert.Equal(type, t);
            Assert.Equal(message, m);
        }

        [Fact]
        public void TheDedupeKeyIgnoresNumbersAndLineNumbers()
        {
            Assert.Equal(
                RavensightErrorRules.DedupeKey("E", "item 12 missing", UnityStack),
                RavensightErrorRules.DedupeKey("E", "item 99 missing", UnityStack.Replace(":42", ":43")));
            Assert.NotEqual(
                RavensightErrorRules.DedupeKey("E", "item missing", Stack("A")),
                RavensightErrorRules.DedupeKey("E", "item missing", Stack("B")));
        }

        [Fact]
        public void TrailLinesAreFormattedFlatScrubbedAndCut()
        {
            string line = RavensightErrorRules.TrailLine(1500, "info", "hello\nbob@example.com " + new string('x', 200));
            Assert.True(line.StartsWith("+1500 info hello [email] x", System.StringComparison.Ordinal));
            Assert.Equal(120, line.Length);
        }

        [Fact]
        public void TheInboxIsBoundedAndDropsSdkLinesAndUnwantedTrailLines()
        {
            RavensightErrorInbox inbox = new RavensightErrorInbox();
            Assert.False(inbox.Offer(Log(RavensightLogLevel.Error, "x")));
            inbox.Accepting = true;
            Assert.False(inbox.Offer(Log(RavensightLogLevel.Error, "Ravensight: request failed")));
            Assert.False(inbox.Offer(Log(RavensightLogLevel.Error, "[Ravensight] session ready")));
            Assert.False(inbox.Offer(Log(RavensightLogLevel.Info, "a line")));
            inbox.WantTrail = true;
            Assert.True(inbox.Offer(Log(RavensightLogLevel.Info, "a line")));
            for (int i = 0; i < 100; i++) inbox.Offer(Log(RavensightLogLevel.Error, "e"));
            Assert.Equal(RavensightErrorInbox.Capacity, inbox.Count);
            Assert.Equal(RavensightErrorInbox.Capacity, inbox.Drain().Count);
            Assert.Equal(0, inbox.Count);
        }

        // --- The gate ------------------------------------------------------------

        [Fact]
        public async Task AnUnityExceptionBecomesOneUncaughtErrorEvent()
        {
            Harness h = new Harness();
            RavensightClient client = await h.Started();
            Assert.True(client.IsErrorCaptureActive);

            Assert.True(client.ProcessLog(Log(RavensightLogLevel.Exception,
                "NullReferenceException: Object reference not set for bob@example.com",
                "PlayerController.Update () (at /Users/jacob/Game/Assets/PlayerController.cs:42)")));

            Dictionary<string, object> data = Assert.Single(await SentErrors(h, client));
            Assert.Equal("error", (string)data["rs_severity"]);
            Assert.Equal("uncaught", (string)data["rs_source"]);
            Assert.Equal("NullReferenceException", (string)data["rs_error_type"]);
            Assert.Equal("Object reference not set for [email]", (string)data["rs_message"]);
            Assert.Equal("PlayerController.Update () (at ~/Game/Assets/PlayerController.cs:42)", (string)data["rs_stack"]);
            Assert.False(data.ContainsKey("rs_trail"));
            Assert.False(data.ContainsKey("rs_fp"));
        }

        [Fact]
        public async Task ErrorAndAssertLinesAreLogSourceAtWarningWithEngineFramesTrimmed()
        {
            Harness h = new Harness();
            RavensightClient client = await h.Started();

            client.ProcessLog(Log(RavensightLogLevel.Error, "door missing", UnityStack));
            client.ProcessLog(Log(RavensightLogLevel.Assert, "Assertion failed", Stack("B")));

            List<Dictionary<string, object>> sent = await SentErrors(h, client);
            Assert.Equal(2, sent.Count);
            Assert.Equal("log", (string)sent[0]["rs_source"]);
            Assert.Equal("warning", (string)sent[0]["rs_severity"]);
            Assert.Equal("Error", (string)sent[0]["rs_error_type"]);
            Assert.Equal("PlayerController:Update () (at Assets/Scripts/PlayerController.cs:42)", (string)sent[0]["rs_stack"]);
            Assert.Equal("Assert", (string)sent[1]["rs_error_type"]);
        }

        [Fact]
        public async Task WarningsAndLogsAreNeverEvents()
        {
            Harness h = new Harness();
            Settings(h, "{\"enabled\":true,\"trail\":true}");
            RavensightClient client = await h.Started();

            Assert.False(client.ProcessLog(Log(RavensightLogLevel.Warning, "low ammo")));
            Assert.False(client.ProcessLog(Log(RavensightLogLevel.Info, "loaded level")));
            Assert.Empty(await SentErrors(h, client));
        }

        [Fact]
        public async Task TheServerSwitchOffStopsCaptureButNotManualTrackError()
        {
            Harness h = new Harness();
            Settings(h, "{\"enabled\":false,\"trail\":true}");
            RavensightClient client = await h.Started();

            Assert.False(client.IsErrorCaptureActive);
            Assert.False(client.ProcessLog(Log(RavensightLogLevel.Exception, "Exception: boom")));
            Assert.True(client.TrackError(RavensightTyped.Error, "manual bob@example.com"));

            Dictionary<string, object> data = Assert.Single(await SentErrors(h, client));
            Assert.False(data.ContainsKey("rs_source"));
            // Manual text is sent as written; the server scrubs it.
            Assert.Equal("manual bob@example.com", (string)data["rs_message"]);
        }

        [Fact]
        public async Task AnAbsentSwitchMeansOnWithNoTrail()
        {
            Harness h = new Harness();
            Settings(h, null);
            RavensightClient client = await h.Started();
            Assert.True(client.ErrorSettingsKnown);
            Assert.True(client.IsErrorCaptureActive);
            Assert.False(client.IsErrorTrailActive);
        }

        [Fact]
        public async Task TheGameCanOptOut()
        {
            Harness h = new Harness();
            RavensightConfig config = h.Config();
            config.ErrorCapture = false;
            RavensightClient client = await h.Started(config);
            Assert.False(client.IsErrorCaptureActive);
            Assert.False(client.ProcessLog(Log(RavensightLogLevel.Exception, "Exception: boom")));

            client.ErrorCaptureEnabled = true;
            Assert.True(client.ProcessLog(Log(RavensightLogLevel.Exception, "Exception: boom")));
        }

        [Fact]
        public void NothingRunsWithoutAKeyOrBeforeConsent()
        {
            Harness h = new Harness();
            RavensightConfig noKey = h.Config();
            noKey.IngestKey = "";
            Assert.False(h.Client(noKey).IsErrorCaptureActive);

            RavensightConfig optIn = h.Config();
            optIn.Consent = RavensightConsent.OptIn;
            RavensightClient client = h.Client(optIn);
            Assert.False(client.IsErrorCaptureActive);
            Assert.False(client.ProcessLog(Log(RavensightLogLevel.Exception, "Exception: boom")));
            Assert.Empty(h.Transport.Requests);
        }

        [Fact]
        public async Task OptingOutStopsCaptureAndForgetsTheTrail()
        {
            Harness h = new Harness();
            Settings(h, "{\"enabled\":true,\"trail\":true}");
            RavensightClient client = await h.Started();
            client.ProcessLog(Log(RavensightLogLevel.Info, "secret line"));

            client.SetTrackingEnabled(false);
            Assert.False(client.IsErrorCaptureActive);
            client.SetTrackingEnabled(true);
            client.ProcessLog(Log(RavensightLogLevel.Exception, "Exception: boom"));

            Dictionary<string, object> data = Assert.Single(await SentErrors(h, client));
            string trail = data.ContainsKey("rs_trail") ? (string)data["rs_trail"] : "";
            Assert.False(trail.Contains("secret line"));
        }

        // --- Boot errors -----------------------------------------------------------

        [Fact]
        public async Task BootErrorsAreHeldUpToFiveAndSentWhenTheAnswerSaysOn()
        {
            Harness h = new Harness();
            RavensightClient client = h.Client();
            client.Begin();
            Assert.True(client.IsErrorCaptureActive);
            for (int i = 0; i < 8; i++)
            {
                client.ProcessLog(Log(RavensightLogLevel.Exception, "Exception: boot " + (char)('a' + i), Stack("F" + (char)('a' + i))));
                h.Clock.Advance(20000);
            }
            Assert.Equal(RavensightClient.MaxHeldErrors, client.HeldErrorCount);
            Assert.Empty(h.Transport.Requests);

            await client.InitAsync();
            Assert.Equal(0, client.HeldErrorCount);
            Assert.Equal(5, (await SentErrors(h, client)).Count);
        }

        [Fact]
        public async Task BootErrorsAreDiscardedWhenTheAnswerSaysOff()
        {
            Harness h = new Harness();
            Settings(h, "{\"enabled\":false}");
            RavensightClient client = h.Client();
            client.Begin();
            client.ProcessLog(Log(RavensightLogLevel.Exception, "Exception: boot"));
            Assert.Equal(1, client.HeldErrorCount);

            await client.InitAsync();
            Assert.Equal(0, client.HeldErrorCount);
            Assert.Empty(await SentErrors(h, client));
        }

        // --- Caps --------------------------------------------------------------------

        [Fact]
        public async Task ThreePerKeySixAMinuteAndThirtyASession()
        {
            Harness h = new Harness();
            RavensightClient client = await h.Started();

            int same = 0;
            for (int i = 0; i < 5; i++)
            {
                if (client.ProcessLog(Log(RavensightLogLevel.Exception, "Exception: item " + i + " missing", Stack("A")))) same++;
            }
            Assert.Equal(3, same);

            // Three tokens left this minute.
            int burst = 0;
            for (int i = 0; i < 10; i++)
            {
                if (client.ProcessLog(Log(RavensightLogLevel.Exception, "Exception: burst", Stack("B" + (char)('a' + i))))) burst++;
            }
            Assert.Equal(3, burst);

            int later = 0;
            for (int i = 0; i < 40; i++)
            {
                h.Clock.Advance(10000);
                if (client.ProcessLog(Log(RavensightLogLevel.Exception, "Exception: later", Stack("C" + i.ToString("x"))))) later++;
            }
            Assert.Equal(RavensightErrorRules.MaxPerSession - 6, later);
            Assert.Equal(RavensightErrorRules.MaxPerSession, (await SentErrors(h, client)).Count);
        }

        [Fact]
        public async Task ATrailRidesOnlyOnAnErrorAtMostTenTimesASession()
        {
            Harness h = new Harness();
            Settings(h, "{\"enabled\":true,\"trail\":true}");
            RavensightClient client = await h.Started();
            for (int i = 0; i < 25; i++) client.ProcessLog(Log(RavensightLogLevel.Info, "line " + i));
            h.Clock.Advance(1234);
            client.ProcessLog(Log(RavensightLogLevel.Warning, "careful"));

            client.ProcessLog(Log(RavensightLogLevel.Exception, "Exception: first", Stack("A")));
            for (int i = 0; i < 11; i++)
            {
                h.Clock.Advance(20000);
                client.ProcessLog(Log(RavensightLogLevel.Exception, "Exception: next", Stack("N" + (char)('a' + i))));
            }

            List<Dictionary<string, object>> sent = await SentErrors(h, client);
            Assert.Equal(12, sent.Count);
            string[] lines = ((string)sent[0]["rs_trail"]).Split('\n');
            Assert.Equal(RavensightErrorRules.TrailLines, lines.Length);
            Assert.Equal("+1234 warning careful", lines[lines.Length - 1]);
            Assert.Equal(10, sent.Count(d => d.ContainsKey("rs_trail")));
            Assert.All(sent.Where(d => d.ContainsKey("rs_trail")), d => Assert.True(((string)d["rs_trail"]).Length <= 1500));
        }

        // --- No self capture -------------------------------------------------------------

        [Fact]
        public async Task TheSdksOwnLinesAndStacksAreNeverCaptured()
        {
            Harness h = new Harness();
            RavensightClient client = await h.Started();
            Assert.False(client.ProcessLog(Log(RavensightLogLevel.Error, "Ravensight: request failed (boom)")));
            Assert.False(client.ProcessLog(Log(RavensightLogLevel.Error, "[Ravensight] dropped one event")));
            Assert.False(client.ProcessLog(Log(RavensightLogLevel.Exception, "Exception: inside",
                "UnityEngine.Debug:LogException (System.Exception)\nRealitySE.RavensightClient:Flush () (at Packages/x.cs:1)")));
            Assert.Empty(await SentErrors(h, client));
        }

        [Fact]
        public async Task OneExceptionLoggedAndRaisedIsOneEvent()
        {
            Harness h = new Harness();
            RavensightClient client = await h.Started();
            client.ProcessLog(Log(RavensightLogLevel.Exception, "InvalidOperationException: bad state", Stack("A")));
            Assert.False(client.ProcessLog(Thrown("uncaught", "System.InvalidOperationException", "bad state", Stack("A"))));
            h.Clock.Advance(5000);
            Assert.True(client.ProcessLog(Thrown("unhandled_rejection", "TimeoutException", "late", Stack("T"))));

            List<Dictionary<string, object>> sent = await SentErrors(h, client);
            Assert.Equal(2, sent.Count);
            Assert.Equal("unhandled_rejection", (string)sent[1]["rs_source"]);
            Assert.Equal("error", (string)sent[1]["rs_severity"]);
        }

        // --- Queue order and 413 -------------------------------------------------------------

        [Fact]
        public async Task GameplayEventsGoAheadOfQueuedCaptures()
        {
            Harness h = new Harness();
            RavensightClient client = await h.Started();
            client.ProcessLog(Log(RavensightLogLevel.Exception, "Exception: boom"));
            client.Track("level_completed");
            await client.FlushAsync();

            List<string> names = h.Transport.SentEventNames();
            Assert.True(names.IndexOf("level_completed") < names.IndexOf("error"));
        }

        [Fact]
        public async Task AFullQueueEvictsCapturesFirstAndACaptureNeverDisplacesGameplay()
        {
            Harness h = new Harness();
            h.Transport.Routes["/track/batch"] = r => FakeTransport.Json(503, "{}");
            RavensightConfig config = h.Config();
            config.MaxQueueSize = 3;
            config.TrackLifecycleEvents = false;
            RavensightClient client = await h.Started(config);

            client.Track("a");
            client.ProcessLog(Log(RavensightLogLevel.Exception, "Exception: one", Stack("A")));
            client.Track("b");
            client.Track("c");
            Assert.Equal(3, client.QueueCount);
            Assert.False(client.ProcessLog(Log(RavensightLogLevel.Exception, "Exception: two", Stack("B"))));

            h.Transport.Routes.Remove("/track/batch");
            h.Clock.Advance(600000);
            int before = h.Transport.To("/track/batch").Count;
            await client.FlushAsync(true);
            Assert.Equal(new List<string> { "a", "b", "c" }, h.Transport.To("/track/batch").Skip(before).SelectMany(FakeTransport.EventsOf).Select(e => (string)e["event"]).ToList());
        }

        [Fact]
        public async Task A413IsSplitAndTheBadEventDroppedLikeA400()
        {
            Harness h = new Harness();
            RavensightConfig config = h.Config();
            config.TrackLifecycleEvents = false;
            RavensightClient client = await h.Started(config);
            h.Transport.Routes["/track/batch"] = r =>
                FakeTransport.EventsOf(r).Any(e => (string)e["event"] == "huge")
                    ? FakeTransport.Json(413, "{\"error\":\"payload_too_large\"}")
                    : FakeTransport.Json(202, "{}");

            client.Track("one");
            client.Track("huge");
            client.Track("two");
            RavensightFlushResult result = await client.FlushAsync();

            Assert.Equal(0, result.Queued);
            Assert.Equal(1, client.Stats.Dropped);
            List<string> accepted = h.Transport.To("/track/batch")
                .Where(r => !FakeTransport.EventsOf(r).Any(e => (string)e["event"] == "huge"))
                .SelectMany(FakeTransport.EventsOf).Select(e => (string)e["event"]).ToList();
            Assert.Equal(new List<string> { "one", "two" }, accepted);
        }
    }
}
