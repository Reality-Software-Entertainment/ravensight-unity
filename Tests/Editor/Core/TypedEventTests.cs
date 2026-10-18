using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace RealitySE.Tests
{
    public class TypedEventTests
    {
        private static async Task<List<Dictionary<string, object>>> Sent(Harness h, RavensightClient client)
        {
            await client.FlushAsync();
            return h.Transport.To("/track/batch").SelectMany(FakeTransport.EventsOf).ToList();
        }

        private static Dictionary<string, object> DataOf(Dictionary<string, object> evt)
        {
            var data = new Dictionary<string, object>((Dictionary<string, object>)evt["data"]);
            Assert.Matches("^[A-Za-z0-9_-]{1,64}$", (string)data["rs_event_id"]);
            data.Remove("rs_event_id");
            return data;
        }

        private static Dictionary<string, object> Last(List<Dictionary<string, object>> events)
        {
            return events[events.Count - 1];
        }

        private static List<string> Keys(Dictionary<string, object> data)
        {
            List<string> keys = data.Keys.ToList();
            keys.Sort(System.StringComparer.Ordinal);
            return keys;
        }

        [Fact]
        public async Task PurchaseSendsExactlyTheReservedKeysWithAnUpperCasedCurrency()
        {
            Harness h = new Harness();
            RavensightClient client = await h.Started();

            Assert.True(client.TrackPurchase(499, "usd", "pack", "gems_100", "cart_1", "txn_9"));
            List<Dictionary<string, object>> events = await Sent(h, client);

            Dictionary<string, object> evt = Last(events);
            Dictionary<string, object> data = DataOf(evt);
            Assert.Equal("purchase", (string)evt["event"]);
            Assert.Equal(new[] { "rs_amount", "rs_cart", "rs_currency", "rs_item_id", "rs_item_type", "rs_kind", "rs_transaction_id" }, Keys(data).ToArray());
            Assert.Equal("purchase", (string)data["rs_kind"]);
            Assert.Equal(499.0, System.Convert.ToDouble(data["rs_amount"]));
            Assert.Equal("USD", (string)data["rs_currency"]);
            Assert.Equal("pack", (string)data["rs_item_type"]);
            Assert.Equal("gems_100", (string)data["rs_item_id"]);
            Assert.Equal("cart_1", (string)data["rs_cart"]);
            Assert.Equal("txn_9", (string)data["rs_transaction_id"]);
        }

        [Fact]
        public void PurchaseOmitsTheOptionalKeysAndAcceptsADigitString()
        {
            RavensightTypedEvent evt = RavensightTyped.Purchase("1299", "eur", "skin", "red");

            Assert.Equal("purchase", evt.Name);
            Assert.Equal(new[] { "rs_amount", "rs_currency", "rs_item_id", "rs_item_type", "rs_kind" }, Keys(evt.Data).ToArray());
            Assert.Equal("1299", (string)evt.Data["rs_amount"]);
            Assert.Equal("EUR", (string)evt.Data["rs_currency"]);
        }

        [Fact]
        public void BadMoneyAndNullsPassThroughAndNeverThrow()
        {
            RavensightTypedEvent evt = RavensightTyped.Purchase(4.99, null, null, null);

            Assert.Equal(4.99, (double)evt.Data["rs_amount"]);
            Assert.Null(evt.Data["rs_currency"]);
            Assert.Equal("purchase", evt.Name);
        }

        [Fact]
        public void ResourceNamesTheEventByFlowAndSendsTheOptionalItem()
        {
            RavensightTypedEvent source = RavensightTyped.Resource("source", "gold", 25.5, "quest", "q7");
            RavensightTypedEvent sink = RavensightTyped.Resource("sink", "gold", 10);

            Assert.Equal("resource_source", source.Name);
            Assert.Equal(new[] { "rs_amount", "rs_flow", "rs_item_id", "rs_item_type", "rs_kind", "rs_resource" }, Keys(source.Data).ToArray());
            Assert.Equal("resource", (string)source.Data["rs_kind"]);
            Assert.Equal("source", (string)source.Data["rs_flow"]);
            Assert.Equal("gold", (string)source.Data["rs_resource"]);
            Assert.Equal(25.5, (double)source.Data["rs_amount"]);
            Assert.Equal("quest", (string)source.Data["rs_item_type"]);
            Assert.Equal("q7", (string)source.Data["rs_item_id"]);

            Assert.Equal("resource_sink", sink.Name);
            Assert.Equal(new[] { "rs_amount", "rs_flow", "rs_kind", "rs_resource" }, Keys(sink.Data).ToArray());
        }

        [Fact]
        public void ProgressionNamesTheEventByStatusAndSendsTheNodesAndScore()
        {
            RavensightTypedEvent full = RavensightTyped.Progression("complete", "world1", "level2", "boss", 1500);
            RavensightTypedEvent bare = RavensightTyped.Progression("start", "world1");
            RavensightTypedEvent fail = RavensightTyped.Progression("fail", "world1", "level2");

            Assert.Equal("progression_complete", full.Name);
            Assert.Equal(new[] { "rs_kind", "rs_p1", "rs_p2", "rs_p3", "rs_score", "rs_status" }, Keys(full.Data).ToArray());
            Assert.Equal("progression", (string)full.Data["rs_kind"]);
            Assert.Equal("complete", (string)full.Data["rs_status"]);
            Assert.Equal("world1", (string)full.Data["rs_p1"]);
            Assert.Equal("level2", (string)full.Data["rs_p2"]);
            Assert.Equal("boss", (string)full.Data["rs_p3"]);
            Assert.Equal(1500.0, (double)full.Data["rs_score"]);

            Assert.Equal("progression_start", bare.Name);
            Assert.Equal(new[] { "rs_kind", "rs_p1", "rs_status" }, Keys(bare.Data).ToArray());
            Assert.Equal("progression_fail", fail.Name);
        }

        [Fact]
        public void ErrorCutsTheMessageTo200AndTheStackTo2000()
        {
            string message = new string('m', 250);
            string stack = new string('s', 2500);

            RavensightTypedEvent evt = RavensightTyped.ErrorEvent("critical", message, stack);

            Assert.Equal("error", evt.Name);
            Assert.Equal(new[] { "rs_kind", "rs_message", "rs_severity", "rs_stack" }, Keys(evt.Data).ToArray());
            Assert.Equal("error", (string)evt.Data["rs_kind"]);
            Assert.Equal("critical", (string)evt.Data["rs_severity"]);
            Assert.Equal(200, ((string)evt.Data["rs_message"]).Length);
            Assert.Equal(2000, ((string)evt.Data["rs_stack"]).Length);

            RavensightTypedEvent noStack = RavensightTyped.ErrorEvent("warning", "short");
            Assert.Equal(new[] { "rs_kind", "rs_message", "rs_severity" }, Keys(noStack.Data).ToArray());
            Assert.Equal("short", (string)noStack.Data["rs_message"]);
        }

        [Fact]
        public void TheCutNeverSplitsASurrogatePair()
        {
            // 199 letters then an emoji (two UTF-16 units) is exactly 200 code points.
            string text = new string('a', 199) + "\U0001F600" + "tail";

            string cut = RavensightTyped.Cut(text, 200);

            Assert.Equal(new string('a', 199) + "\U0001F600", cut);
            Assert.Equal(201, cut.Length);
        }

        [Fact]
        public void ReservedKeysWinOverCallerKeys()
        {
            Dictionary<string, object> extra = new Dictionary<string, object>
            {
                { "rs_kind", "none" },
                { "rs_amount", 1 },
                { "rs_currency", "xxx" },
                { "mode", "hard" },
            };

            RavensightTypedEvent evt = RavensightTyped.Purchase(250, "gbp", "pack", "p1", null, null, extra);

            Assert.Equal("purchase", (string)evt.Data["rs_kind"]);
            Assert.Equal(250, (int)evt.Data["rs_amount"]);
            Assert.Equal("GBP", (string)evt.Data["rs_currency"]);
            Assert.Equal("hard", (string)evt.Data["mode"]);
            // The caller's own dictionary is never edited.
            Assert.Equal("none", (string)extra["rs_kind"]);
        }

        [Fact]
        public void ReservedKeysInExtraAreDroppedSoTheyCannotBypassACutOrADefault()
        {
            Dictionary<string, object> extra = new Dictionary<string, object>
            {
                { "rs_stack", new string('x', 5000) },
                { "rs_invalid", "none" },
                { "rs_p3", "sneak" },
                { "keep", 1 },
            };

            RavensightTypedEvent error = RavensightTyped.ErrorEvent("error", "m", null, extra);
            RavensightTypedEvent step = RavensightTyped.Progression("start", "w1", "l1", null, null, extra);

            Assert.Equal(new[] { "keep", "rs_kind", "rs_message", "rs_severity" }, Keys(error.Data).ToArray());
            Assert.Equal(new[] { "keep", "rs_kind", "rs_p1", "rs_p2", "rs_status" }, Keys(step.Data).ToArray());
        }

        [Fact]
        public async Task FramesGatheredWhileOptedOutAreNeverSent()
        {
            Harness h = new Harness();
            RavensightConfig config = h.Config();
            config.Performance = true;
            RavensightClient client = await h.Started(config);

            client.SetTrackingEnabled(false);
            client.SamplePerformance(1.0);
            client.SetTrackingEnabled(true);

            Assert.False(client.SendHealth());
        }

        [Fact]
        public void TheEventNameCanBeOverriddenOnEveryHelper()
        {
            Assert.Equal("iap", RavensightTyped.Purchase(1, "usd", "a", "b", null, null, null, "iap").Name);
            Assert.Equal("gold_in", RavensightTyped.Resource("source", "gold", 1, null, null, null, "gold_in").Name);
            Assert.Equal("lvl", RavensightTyped.Progression("start", "w", null, null, null, null, "lvl").Name);
            Assert.Equal("oops", RavensightTyped.ErrorEvent("error", "x", null, null, "oops").Name);
        }

        [Fact]
        public async Task TypedHelpersGoThroughTheOrdinaryTrackAndItsOptOut()
        {
            Harness h = new Harness();
            RavensightClient client = await h.Started();

            client.SetTrackingEnabled(false);
            Assert.False(client.TrackError("error", "boom"));
            Assert.False(client.TrackPurchase(1, "usd", "a", "b"));
            Assert.False(client.TrackResource("sink", "gold", 1));
            Assert.False(client.TrackProgression("start", "w"));
        }

        [Fact]
        public async Task TypedEventsAreQueuedAndSentLikeAnyOtherEvent()
        {
            Harness h = new Harness();
            RavensightClient client = await h.Started();

            Assert.True(client.TrackResource("sink", "gold", 5));
            Assert.True(client.TrackProgression("start", "w1"));
            Assert.True(client.TrackError("info", "hello"));
            await client.FlushAsync();

            Assert.Equal(new[] { "game_started", "resource_sink", "progression_start", "error" }, h.Transport.SentEventNames().ToArray());
        }

        [Fact]
        public void TheSamplerReportsTheMedianAndTheTenthPercentileOfAKnownSet()
        {
            RavensightFrameSampler sampler = new RavensightFrameSampler();
            for (int fps = 1; fps <= 10; fps++) sampler.Add(fps);

            RavensightHealthSummary summary = sampler.Summarize();

            Assert.Equal(10, summary.Samples);
            Assert.Equal(5.5, summary.FpsMedian);
            Assert.Equal(1.9, summary.FpsP10);
        }

        [Fact]
        public void TheSamplerTakesOneSamplePerSecondOfFrameTime()
        {
            RavensightFrameSampler sampler = new RavensightFrameSampler();

            // Quarter second frames, so the sums are exact in binary.
            for (int i = 0; i < 3; i++) sampler.Tick(0.25);
            Assert.Equal(0, sampler.Count);
            sampler.Tick(0.25);
            Assert.Equal(1, sampler.Count);
            for (int i = 0; i < 8; i++) sampler.Tick(0.25);
            Assert.Equal(3, sampler.Count);
            Assert.Equal(4.0, sampler.Summarize().FpsMedian);
        }

        [Fact]
        public void TheSamplerIgnoresZeroNegativeAndNonFiniteFrames()
        {
            RavensightFrameSampler sampler = new RavensightFrameSampler();

            sampler.Tick(0);
            sampler.Tick(-1);
            sampler.Tick(double.NaN);
            sampler.Tick(double.PositiveInfinity);
            sampler.Add(double.NaN);

            Assert.Equal(0, sampler.Count);
            Assert.Equal(0, sampler.Summarize().Samples);
        }

        [Fact]
        public void TheReservoirNeverHoldsMoreThan600Samples()
        {
            RavensightFrameSampler sampler = new RavensightFrameSampler();

            for (int i = 0; i < 5000; i++) sampler.Add(30 + (i % 60));

            Assert.Equal(600, sampler.Count);
            Assert.Equal(600, sampler.Summarize().Samples);
        }

        [Fact]
        public async Task HealthIsOffByDefaultAndSendsNothing()
        {
            Harness h = new Harness();
            RavensightClient client = await h.Started();

            for (int i = 0; i < 200; i++) client.SamplePerformance(1.0 / 60.0);

            Assert.False(client.PerformanceEnabled);
            Assert.False(client.SendHealth(100, 1000));
            await client.FlushAsync();
            Assert.Equal(new[] { "game_started" }, h.Transport.SentEventNames().ToArray());
        }

        [Fact]
        public async Task HealthSendsNothingWhenNoSampleWasTaken()
        {
            Harness h = new Harness();
            RavensightConfig config = h.Config();
            config.Performance = true;
            RavensightClient client = await h.Started(config);

            client.SamplePerformance(0.5);

            Assert.False(client.SendHealth(100, 1000));
            await client.FlushAsync();
            Assert.Equal(new[] { "game_started" }, h.Transport.SentEventNames().ToArray());
        }

        [Fact]
        public async Task HealthSendsOneEventPerSessionWithTheSummary()
        {
            Harness h = new Harness();
            RavensightConfig config = h.Config();
            config.Performance = true;
            RavensightClient client = await h.Started(config);

            // One second frames each cross the interval: 20 samples of 1 fps.
            for (int i = 0; i < 20; i++) client.SamplePerformance(1.0);
            Assert.True(client.SendHealth(256.5, 4200));
            Assert.False(client.SendHealth(256.5, 4200));
            client.SamplePerformance(1.0);
            Assert.False(client.SendHealth());
            await client.FlushAsync();

            List<Dictionary<string, object>> events = h.Transport.To("/track/batch").SelectMany(FakeTransport.EventsOf).ToList();
            List<Dictionary<string, object>> health = events.Where(e => (string)e["event"] == "health").ToList();
            Assert.Equal(1, health.Count);
            Dictionary<string, object> data = DataOf(health[0]);
            Assert.Equal(new[] { "rs_boot_ms", "rs_fps_median", "rs_fps_p10", "rs_kind", "rs_memory_mb" }, Keys(data).ToArray());
            Assert.Equal("health", (string)data["rs_kind"]);
            Assert.Equal(1.0, System.Convert.ToDouble(data["rs_fps_median"]));
            Assert.Equal(1.0, System.Convert.ToDouble(data["rs_fps_p10"]));
            Assert.Equal(256.5, System.Convert.ToDouble(data["rs_memory_mb"]));
            Assert.Equal(4200.0, System.Convert.ToDouble(data["rs_boot_ms"]));
        }

        [Fact]
        public async Task HealthOmitsMemoryAndBootWhenTheyAreNotAvailable()
        {
            Harness h = new Harness();
            RavensightConfig config = h.Config();
            config.Performance = true;
            RavensightClient client = await h.Started(config);
            client.SamplePerformance(1.0);

            Assert.True(client.SendHealth());
            await client.FlushAsync();

            Dictionary<string, object> data = DataOf(h.Transport.To("/track/batch").SelectMany(FakeTransport.EventsOf).Single(e => (string)e["event"] == "health"));
            Assert.Equal(new[] { "rs_fps_median", "rs_fps_p10", "rs_kind" }, Keys(data).ToArray());
        }

        [Fact]
        public async Task ANewSessionAfterTheBackgroundTimeoutMaySendHealthAgain()
        {
            Harness h = new Harness();
            RavensightConfig config = h.Config();
            config.Performance = true;
            RavensightClient client = await h.Started(config);

            client.SamplePerformance(1.0);
            Assert.True(client.SendHealth());
            client.EnterBackground();
            h.Clock.Advance(31 * 60 * 1000L);
            await client.EnterForegroundAsync();

            Assert.False(client.SendHealth());
            client.SamplePerformance(0.25);
            client.SamplePerformance(1.0);
            Assert.True(client.SendHealth());
        }

        [Fact]
        public async Task TheSessionRequestCarriesTheSdkNameAndVersion()
        {
            Harness h = new Harness();
            await h.Started();

            Dictionary<string, object> body = FakeTransport.BodyOf(h.Transport.To("/session")[0]);

            Assert.Equal("unity/" + RavensightClient.SdkVersion, (string)body["sdk"]);
            Assert.Equal("0.3.1", RavensightClient.SdkVersion);
        }
    }
}
