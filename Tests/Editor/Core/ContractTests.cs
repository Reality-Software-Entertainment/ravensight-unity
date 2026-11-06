using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace RealitySE.Tests
{
    /// <summary>
    /// The platform's SDK contracts: delivery (with every shared vector), the
    /// persisted queue and origin replay, crash capture, ads and attribution,
    /// experiment exposure and device fields.
    /// </summary>
    public class ContractTests
    {
        private static readonly Dictionary<string, object> Vectors =
            RavensightJson.AsObject(RavensightJson.Parse(DeliveryVectors.Json));

        private static Dictionary<string, object> Obj(object node, string key)
        {
            return RavensightJson.AsObject(RavensightJson.AsObject(node)[key]);
        }

        /// <summary>Expands the vectors' {repeat, count} values.</summary>
        private static object Expand(object value)
        {
            Dictionary<string, object> map = value as Dictionary<string, object>;
            if (map == null) return value;
            object repeat, count;
            if (map.TryGetValue("repeat", out repeat) && map.TryGetValue("count", out count) && repeat is string)
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < (int)(double)count; i++) sb.Append((string)repeat);
                return sb.ToString();
            }
            Dictionary<string, object> copy = new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> item in map) copy[item.Key] = Expand(item.Value);
            return copy;
        }

        private static string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "rs-unity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static RavensightResponse SessionWithOrigin(int n, bool signing)
        {
            string body = "{\"token\":\"tok_" + n + "\",\"expiresIn\":86400," +
                          "\"origin\":{\"session\":\"ses_" + n + "\",\"build\":\"1.0.0\",\"platform\":\"unity\",\"stamp\":\"o1.stamp" + n + "\"}" +
                          (signing ? ",\"signing\":{\"algorithm\":\"HMAC-SHA256\",\"version\":1,\"secret\":\"secret_" + n + "\",\"serverTime\":1760001000}" : "") +
                          "}";
            return FakeTransport.Json(201, body);
        }

        private static Harness WithOrigins(bool signing = false)
        {
            Harness h = new Harness();
            int n = 0;
            h.Transport.Routes["/session"] = r => SessionWithOrigin(++n, signing);
            return h;
        }

        // --- Shared vectors -------------------------------------------------------

        [Fact]
        public void EveryHeaderSigningVector()
        {
            Dictionary<string, object> signing = Obj(Vectors, "signing");
            string secret = (string)signing["secret"];
            foreach (object raw in (List<object>)signing["header"])
            {
                Dictionary<string, object> v = RavensightJson.AsObject(raw);
                Assert.Equal((string)v["header"], RavensightDelivery.SignRequest(secret, (long)(double)v["timestamp"], (string)v["body"]));
            }
        }

        [Fact]
        public void EveryBeaconEnvelopeVector()
        {
            Dictionary<string, object> signing = Obj(Vectors, "signing");
            foreach (object raw in (List<object>)signing["beacon"])
            {
                Dictionary<string, object> v = RavensightJson.AsObject(raw);
                Dictionary<string, object> expected = Obj(v, "envelope");
                Dictionary<string, object> envelope = RavensightDelivery.SignedBeaconBody(
                    (string)expected["sessionToken"], (string)signing["secret"], (long)(double)v["t"], (string)v["inner"]);
                Assert.Equal(RavensightJson.Write(expected), RavensightJson.Write(envelope));
            }
        }

        [Fact]
        public void EveryEventSizeVectorMeasuredAsTheServerMeasures()
        {
            foreach (object raw in (List<object>)Obj(Vectors, "anyKeyEventSize")["cases"])
            {
                Dictionary<string, object> v = RavensightJson.AsObject(raw);
                Dictionary<string, object> data = (Dictionary<string, object>)Expand(v["data"]);
                bool fits = RavensightDelivery.EventDataBytes(data) <= RavensightDelivery.MaxEventBytes;
                Assert.True(fits == (v["expect"] == null), (string)v["name"]);
            }
        }

        [Fact]
        public async Task EventSizeVectorsThroughTrack()
        {
            foreach (object raw in (List<object>)Obj(Vectors, "anyKeyEventSize")["cases"])
            {
                Dictionary<string, object> v = RavensightJson.AsObject(raw);
                Harness h = new Harness();
                RavensightConfig config = h.Config();
                config.TrackLifecycleEvents = false;
                RavensightClient client = await h.Started(config);
                bool queued = client.Track("e", (Dictionary<string, object>)Expand(v["data"]));
                Assert.True(queued == (v["expect"] == null), (string)v["name"]);
            }
        }

        [Fact]
        public void TypedTextVectorsAreCutToTheirCaps()
        {
            int cut = 0;
            foreach (object raw in (List<object>)Obj(Vectors, "publicKeyTypedText")["cases"])
            {
                Dictionary<string, object> data = (Dictionary<string, object>)Expand(RavensightJson.AsObject(raw)["data"]);
                object kind;
                if (!data.TryGetValue("rs_kind", out kind) || (string)kind != "error") continue;
                object stack;
                data.TryGetValue("rs_stack", out stack);
                RavensightTypedEvent evt = RavensightTyped.ErrorEvent((string)data["rs_severity"], (string)data["rs_message"], stack as string);
                Assert.True(((string)evt.Data["rs_message"]).Length <= 200);
                object sent;
                if (evt.Data.TryGetValue("rs_stack", out sent))
                {
                    Assert.True(((string)sent).Length <= 2000);
                    if (((string)stack).Length > 2000) cut++;
                }
            }
            Assert.Equal(1, cut); // the 2,001 character stack case conforms once cut
        }

        [Fact]
        public void QueueFileNamesAreFnv1a64()
        {
            Assert.Equal("cbf29ce484222325", RavensightDelivery.QueueKeyHash(""));
            Assert.Equal("af63dc4c8601ec8c", RavensightDelivery.QueueKeyHash("a"));
            Assert.Equal("85944171f73967e8", RavensightDelivery.QueueKeyHash("foobar"));
        }

        // --- Persisted queue and origin replay --------------------------------------------

        [Fact]
        public async Task TheNextLaunchSendsStoredEventsUnderTheirOriginalOrigin()
        {
            string dir = TempDir();
            Harness first = WithOrigins();
            first.Transport.Routes["/track/batch"] = r => FakeTransport.Json(0, null);
            RavensightConfig config = first.Config();
            config.QueueDirectory = dir;
            config.TrackLifecycleEvents = false;
            config.DeviceId = "player-1";
            RavensightClient a = await first.Started(config);
            a.Track("pickup");
            first.Clock.Advance(5);
            a.Track("pickup");
            await a.FlushAsync();
            a.PersistNow();
            string file = a.QueueFilePath;
            Dictionary<string, object> doc = RavensightJson.AsObject(RavensightJson.Parse(File.ReadAllText(file)));
            Assert.Equal(2.0, doc["version"]);
            Dictionary<string, object> group = RavensightJson.AsObject(((List<object>)doc["groups"])[0]);
            Assert.Equal("ses_1", RavensightJson.GetString(group["origin"], "session", null));
            Assert.False(File.ReadAllText(file).Contains("tok_1"), "no session token at rest");
            Assert.Single(Directory.GetFiles(dir));
            // The process ends here without a clean quit.

            Harness second = WithOrigins();
            second.Clock.NowMs = first.Clock.NowMs + 60000;
            second.Transport.Routes["/session"] = r => SessionWithOrigin(9, false);
            RavensightConfig again = second.Config();
            again.QueueDirectory = dir;
            again.TrackLifecycleEvents = false;
            again.DeviceId = "player-1";
            RavensightClient b = second.Client(again);
            Assert.Equal(2, b.QueueCount);
            Assert.Equal(2, b.Stats.Restored);
            b.Begin();
            await b.InitAsync();
            await b.FlushAsync();
            List<Dictionary<string, object>> sent = FakeTransport.EventsOf(second.Transport.To("/track/batch").Last());
            Assert.Equal(2, sent.Count);
            foreach (Dictionary<string, object> e in sent)
            {
                Dictionary<string, object> origin = RavensightJson.AsObject(e["origin"]);
                Assert.Equal("ses_1", origin["session"]);
                Assert.Equal("o1.stamp1", origin["stamp"]);
                Assert.Equal(new[] { "data", "event", "origin", "timestamp" }, e.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            }
            Assert.Equal((double)(first.Clock.NowMs - 5), sent[0]["timestamp"]);
            Assert.False(File.Exists(file), "an empty queue deletes the file");

            b.Track("fresh");
            await b.FlushAsync();
            Assert.False(FakeTransport.EventsOf(second.Transport.To("/track/batch").Last())[0].ContainsKey("origin"));
            Directory.Delete(dir, true);
        }

        [Fact]
        public void AVersionOneFileAMalformedFileAndAnUnknownVersion()
        {
            string dir = TempDir();
            RavensightQueueStore store = new RavensightQueueStore(dir, "gt_live_test");
            long now = 1760000000000L;
            File.WriteAllText(store.FilePath, "{\"version\":1,\"groups\":[{\"sessionToken\":\"x\",\"expiresAtMs\":1,\"events\":[{\"event\":\"old\",\"timestamp\":1759999000,\"data\":{\"rs_event_id\":\"a-1\"}}]}]}");
            RavensightQueueStore.LoadResult v1 = store.Load(now);
            Assert.Single(v1.Events);
            Assert.Null(v1.Events[0].Origin);
            Assert.Equal(1759999000000L, v1.Events[0].Timestamp);
            foreach (string bad in new[] { "{not json", "{\"version\":3,\"groups\":[]}", "[]", "" })
            {
                File.WriteAllText(store.FilePath, bad);
                Assert.Empty(store.Load(now).Events);
            }
            Directory.Delete(dir, true);
        }

        [Fact]
        public void EightyNineDaysAndFiveHundredEvents()
        {
            string dir = TempDir();
            RavensightQueueStore store = new RavensightQueueStore(dir, "gt_live_test");
            long now = 1760000000000L;
            List<RavensightEvent> events = new List<RavensightEvent>();
            RavensightEvent ancient = new RavensightEvent();
            ancient.Name = "ancient";
            ancient.Timestamp = now - 90L * 86400000L;
            ancient.Data = new Dictionary<string, object>();
            events.Add(ancient);
            File.WriteAllText(store.FilePath, RavensightQueueStore.Serialize(events));
            RavensightQueueStore.LoadResult old = store.Load(now);
            Assert.Empty(old.Events);
            Assert.Equal(1, old.Expired);

            events.Clear();
            for (int i = 0; i < 510; i++)
            {
                RavensightEvent e = new RavensightEvent();
                e.Name = "e" + i;
                e.Timestamp = now - 1000 + i;
                e.Data = new Dictionary<string, object>();
                events.Add(e);
            }
            Assert.True(store.Save(events));
            RavensightQueueStore.LoadResult capped = store.Load(now);
            Assert.Equal(500, capped.Events.Count);
            Assert.Equal("e10", capped.Events[0].Name);
            Assert.True(store.Save(new List<RavensightEvent>()));
            Assert.False(File.Exists(store.FilePath));
            Directory.Delete(dir, true);
        }

        [Fact]
        public async Task OptingOutRemovesTheFileAndAnOptInPlayerWritesNothingBeforeTheYes()
        {
            string dir = TempDir();
            Harness h = new Harness();
            h.Transport.Routes["/track/batch"] = r => FakeTransport.Json(0, null);
            RavensightConfig config = h.Config();
            config.QueueDirectory = dir;
            RavensightClient client = await h.Started(config);
            client.PersistNow();
            Assert.True(File.Exists(client.QueueFilePath));
            client.SetTrackingEnabled(false);
            Assert.False(File.Exists(client.QueueFilePath));

            Harness optIn = new Harness();
            RavensightConfig waiting = optIn.Config();
            waiting.Consent = RavensightConsent.OptIn;
            waiting.QueueDirectory = Path.Combine(dir, "optin");
            RavensightClient pending = optIn.Client(waiting);
            pending.Track("nothing");
            pending.PersistNow();
            Assert.Null(pending.QueueFilePath);
            Assert.False(Directory.Exists(waiting.QueueDirectory));
            Directory.Delete(dir, true);
        }

        // --- Response table ------------------------------------------------------------------

        private static async Task<RavensightClient> Quiet(Harness h)
        {
            RavensightConfig config = h.Config();
            config.TrackLifecycleEvents = false;
            return await h.Started(config);
        }

        [Fact]
        public async Task ABadRequestWithAnIndexDropsThatEventOnly()
        {
            Harness h = new Harness();
            int calls = 0;
            h.Transport.Routes["/track/batch"] = r => ++calls == 1
                ? FakeTransport.Json(400, "{\"error\":\"event_not_allowed\",\"index\":1}")
                : FakeTransport.Json(202, "{}");
            RavensightClient client = await Quiet(h);
            client.Track("a"); client.Track("b"); client.Track("c");
            await client.FlushAsync();
            Assert.Equal(new[] { "a", "b", "c", "a", "c" }, h.Transport.SentEventNames().ToArray());
            Assert.Equal(1, client.Stats.Dropped);
            Assert.Equal(0, client.QueueCount);
        }

        [Fact]
        public async Task AnyOtherBadRequestDropsTheRequestsEvents()
        {
            Harness h = new Harness();
            h.Transport.Routes["/track/batch"] = r => FakeTransport.Json(400, "{\"error\":\"invalid_payload\"}");
            RavensightClient client = await Quiet(h);
            client.Track("a"); client.Track("b");
            await client.FlushAsync();
            Assert.Single(h.Transport.To("/track/batch"));
            Assert.Equal(2, client.Stats.Dropped);
        }

        [Fact]
        public async Task ForbiddenOnABatchKeepsTheEventsUntilTheNextLaunch()
        {
            string dir = TempDir();
            Harness h = new Harness();
            h.Transport.Routes["/track/batch"] = r => FakeTransport.Json(403, "{\"error\":\"origin_refused\"}");
            RavensightConfig config = h.Config();
            config.TrackLifecycleEvents = false;
            config.QueueDirectory = dir;
            RavensightClient client = await h.Started(config);
            client.Track("a");
            await client.FlushAsync();
            Assert.Equal("refused", (await client.FlushAsync()).Skipped);
            Assert.Equal(1, client.QueueCount);
            Assert.Single(h.Transport.To("/track/batch"));
            client.Dispose();

            Harness next = new Harness();
            RavensightConfig again = next.Config();
            again.TrackLifecycleEvents = false;
            again.QueueDirectory = dir;
            RavensightClient relaunched = await next.Started(again);
            await relaunched.FlushAsync();
            Assert.Equal(new[] { "a" }, next.Transport.SentEventNames().ToArray());
            Directory.Delete(dir, true);
        }

        [Fact]
        public async Task PayloadTooLargeSplitsAndUnsupportedEncodingResendsPlainOnce()
        {
            Harness h = new Harness();
            int calls = 0;
            h.Transport.Routes["/track/batch"] = r =>
            {
                calls++;
                if (calls == 1) return FakeTransport.Json(415, "{\"error\":\"unsupported_encoding\"}");
                if (calls == 2) return FakeTransport.Json(413, "{\"error\":\"payload_too_large\"}");
                return FakeTransport.Json(202, "{}");
            };
            RavensightClient client = await Quiet(h);
            Dictionary<string, object> big = new Dictionary<string, object>();
            big["pad"] = new string('x', 2000);
            client.Track("a", big);
            client.Track("b");
            await client.FlushAsync();
            List<RavensightRequest> batches = h.Transport.To("/track/batch");
            Assert.NotNull(batches[0].RawBody);
            Assert.Null(batches[1].RawBody);
            Assert.Equal(new[] { 2, 2, 1, 1 }, batches.Select(r => FakeTransport.EventsOf(r).Count).ToArray());
            Assert.Equal(0, client.QueueCount);
        }

        [Fact]
        public async Task ServiceUnavailableWithRetryAfterWaitsExactlyAndFlushCutsOnlyABackoff()
        {
            Harness h = new Harness();
            int calls = 0;
            h.Transport.Routes["/track/batch"] = r =>
            {
                calls++;
                if (calls == 1) return FakeTransport.Json(503, "{\"error\":\"busy\"}", "7");
                if (calls == 2) return FakeTransport.Json(500, "{\"error\":\"boom\"}");
                return FakeTransport.Json(202, "{}");
            };
            RavensightConfig config = h.Config();
            config.TrackLifecycleEvents = false;
            config.Random = () => 0.0;
            RavensightClient client = await h.Started(config);
            client.Track("a");
            await client.FlushAsync();
            Assert.Equal(7000L, client.RetryInMs);
            Assert.Equal("retry_after", (await client.FlushAsync()).Skipped);
            h.Clock.Advance(7000);
            await client.FlushAsync();
            Assert.Equal(5000L, client.RetryInMs);
            Assert.Equal("backoff", (await client.AutoFlushAsync()).Skipped);
            Assert.Equal(1, (await client.FlushAsync()).Sent);
        }

        // --- gzip and signing ---------------------------------------------------------------------

        [Fact]
        public async Task LargeBodiesAreGzippedAndEveryBatchIsSignedOverThePlainText()
        {
            Harness h = WithOrigins(true);
            RavensightClient client = await Quiet(h);
            Assert.True(client.IsSigning);
            client.Track("small");
            await client.FlushAsync();
            Dictionary<string, object> big = new Dictionary<string, object>();
            big["pad"] = new string('y', 3000);
            client.Track("big", big);
            await client.FlushAsync();
            List<RavensightRequest> batches = h.Transport.To("/track/batch");
            Assert.Null(batches[0].RawBody);
            Assert.False(batches[0].Headers.ContainsKey("Content-Encoding"));
            Assert.Equal("gzip", batches[1].Headers["Content-Encoding"]);
            Assert.Equal(batches[1].Body, RavensightDelivery.Gunzip(batches[1].RawBody));
            foreach (RavensightRequest r in batches)
            {
                long ts = long.Parse(r.Headers["X-Ravensight-Timestamp"]);
                // The fake server clock is 1760001000, the device clock 1760000000.
                Assert.Equal(1760001000L, ts);
                Assert.Equal(RavensightDelivery.SignRequest("secret_1", ts, r.Body), r.Headers["X-Ravensight-Signature"]);
            }
            Assert.False((client.QueueFilePath ?? "").Length > 0 && File.ReadAllText(client.QueueFilePath).Contains("secret_1"));
        }

        [Fact]
        public async Task AnUnsignedSessionSendsNoSignature()
        {
            Harness h = new Harness();
            RavensightClient client = await Quiet(h);
            client.Track("a");
            await client.FlushAsync();
            Assert.False(h.Transport.To("/track/batch")[0].Headers.ContainsKey("X-Ravensight-Signature"));
        }

        // --- Exit seam ------------------------------------------------------------------------------

        [Fact]
        public async Task AnExpiredExitMarkerWithAnOriginIsQueuedUnderThatOrigin()
        {
            Harness h = new Harness();
            h.Storage.SetString(RavensightClient.BackgroundMarkerKey,
                "{\"sessionToken\":\"old\",\"expiresAt\":" + (h.Clock.NowMs / 1000 - 1) + ",\"backgroundedAt\":1759990000," +
                "\"origin\":{\"session\":\"ses_old\",\"build\":\"0.9\",\"platform\":\"Android\",\"stamp\":\"o1.old\"}}");
            RavensightConfig config = h.Config();
            config.TrackLifecycleEvents = true;
            RavensightClient client = await h.Started(config);
            await client.FlushAsync();
            Dictionary<string, object> exit = h.Transport.To("/track/batch").SelectMany(FakeTransport.EventsOf)
                .First(e => (string)e["event"] == "game_exited");
            Assert.Equal("ses_old", RavensightJson.GetString(exit["origin"], "session", null));
            Assert.Equal(1759990000000.0, exit["timestamp"]);
        }

        // --- Error events behind gameplay -----------------------------------------------

        private static RavensightLogEntry Thrown(string text)
        {
            RavensightLogEntry entry = new RavensightLogEntry();
            entry.Level = RavensightLogLevel.Exception;
            entry.Text = text;
            entry.Stack = "Game.Run () (at Assets/" + text.Replace(" ", "").Replace(":", "") + ".cs:1)";
            return entry;
        }

        [Fact]
        public async Task Rule1GameplayGoesAheadOfCapturesAndTrackErrorIsGameplay()
        {
            Harness h = new Harness();
            h.Transport.Routes["/track/batch"] = r => FakeTransport.Json(0, null);
            RavensightConfig config = h.Config();
            config.TrackLifecycleEvents = false;
            RavensightClient client = await h.Started(config);
            Assert.True(client.ProcessLog(Thrown("Exception: boom")));
            client.Track("play");
            client.TrackError("error", "own");
            h.Clock.Advance(600000);
            await client.FlushAsync();
            List<string> order = FakeTransport.EventsOf(h.Transport.To("/track/batch").Last())
                .Select(e => (string)e["event"] == "error" ? (string)RavensightJson.AsObject(e["data"])["rs_message"] : (string)e["event"]).ToList();
            Assert.Equal(new[] { "play", "own", "boom" }, order.ToArray());
        }

        [Fact]
        public async Task Rule2AnIncomingCaptureEvictsTheOldestCaptureOrIsDroppedNeverGameplay()
        {
            Harness h = new Harness();
            h.Transport.Routes["/track/batch"] = r => FakeTransport.Json(0, null);
            RavensightConfig config = h.Config();
            config.TrackLifecycleEvents = false;
            config.MaxQueueSize = 3;
            RavensightClient client = await h.Started(config);
            client.Track("a");
            Assert.True(client.ProcessLog(Thrown("Exception: one")));
            client.Track("b");
            Assert.True(client.ProcessLog(Thrown("Exception: two")), "evicts the oldest capture");
            Assert.Equal(3, client.QueueCount);
            client.Track("c");
            Assert.False(client.ProcessLog(Thrown("Exception: three")), "no capture to evict: dropped");
            h.Clock.Advance(600000);
            await client.FlushAsync();
            Assert.Equal(new[] { "a", "b", "c" }, FakeTransport.EventsOf(h.Transport.To("/track/batch").Last()).Select(e => (string)e["event"]).ToArray());
        }

        [Fact]
        public async Task Rule3NoTrailInTheFileAndOptOutAndResetDeleteCaptures()
        {
            string dir = TempDir();
            Harness h = new Harness();
            h.Transport.Routes["/track/batch"] = r => FakeTransport.Json(0, null);
            RavensightConfig config = h.Config();
            config.TrackLifecycleEvents = false;
            config.QueueDirectory = dir;
            RavensightClient client = await h.Started(config);
            Dictionary<string, object> withTrail = new Dictionary<string, object>();
            withTrail["rs_kind"] = "error";
            withTrail["rs_trail"] = "+10 info secret line";
            RavensightEvent evt = new RavensightEvent();
            evt.Name = "error";
            evt.Data = withTrail;
            evt.Timestamp = h.Clock.NowMs;
            evt.Captured = true;
            RavensightQueueStore store = new RavensightQueueStore(dir, "gt_live_test");
            Assert.True(store.Save(new List<RavensightEvent> { evt }));
            string text = File.ReadAllText(store.FilePath);
            Assert.False(text.Contains("rs_trail") || text.Contains("secret line"), "no trail on disk");
            Assert.True(text.Contains("\"auto\":true"), "the flag is stored");
            Assert.True(withTrail.ContainsKey("rs_trail"), "kept in memory");

            File.WriteAllText(store.FilePath, text.Replace("\"rs_kind\":\"error\"", "\"rs_kind\":\"error\",\"rs_trail\":\"old\""));
            RavensightQueueStore.LoadResult loaded = store.Load(h.Clock.NowMs);
            Assert.False(loaded.Events[0].Data.ContainsKey("rs_trail"), "dropped from a restored capture");
            Assert.True(loaded.Events[0].Captured);

            Assert.True(client.ProcessLog(Thrown("Exception: boom")));
            client.ResetDeviceId();
            Assert.False(File.Exists(client.QueueFilePath) && File.ReadAllText(client.QueueFilePath).Contains("boom"), "id reset deletes it");

            Assert.True(client.ProcessLog(Thrown("Exception: again")));
            client.PersistNow();
            Assert.True(File.Exists(client.QueueFilePath));
            client.SetTrackingEnabled(false);
            Assert.False(File.Exists(client.QueueFilePath), "opt-out deletes it");
            Directory.Delete(dir, true);
        }

        [Fact]
        public async Task ACaptureIsWrittenAtOnceAndKeepsItsPlaceBehindGameplayAfterARestart()
        {
            string dir = TempDir();
            Harness h = new Harness();
            h.Transport.Routes["/track/batch"] = r => FakeTransport.Json(0, null);
            RavensightConfig config = h.Config();
            config.TrackLifecycleEvents = false;
            config.QueueDirectory = dir;
            RavensightClient client = await h.Started(config);
            RavensightLogEntry entry = new RavensightLogEntry();
            entry.Level = RavensightLogLevel.Exception;
            entry.Text = "Exception: boom";
            Assert.True(client.ProcessLog(entry));
            // Written at once, inside the one second write interval.
            Assert.True(File.ReadAllText(client.QueueFilePath).Contains("\"auto\":true"));
            client.Dispose();

            Harness next = new Harness();
            next.Transport.Routes["/track/batch"] = r => FakeTransport.Json(0, null);
            RavensightConfig again = next.Config();
            again.TrackLifecycleEvents = false;
            again.QueueDirectory = dir;
            RavensightClient relaunched = await next.Started(again);
            relaunched.Track("play");
            relaunched.PersistNow();
            string text = File.ReadAllText(relaunched.QueueFilePath);
            Assert.True(text.IndexOf("\"play\"", StringComparison.Ordinal) < text.IndexOf("\"error\"", StringComparison.Ordinal),
                "gameplay goes ahead of the restored capture");
            Directory.Delete(dir, true);
        }

        [Fact]
        public async Task ABatchInFlightIsNeverReordered()
        {
            Harness h = new Harness();
            RavensightClient client = null;
            bool inserted = false;
            h.Transport.Routes["/track/batch"] = r =>
            {
                if (!inserted) { inserted = true; client.Track("late"); }
                return FakeTransport.Json(202, "{}");
            };
            RavensightConfig config = h.Config();
            config.TrackLifecycleEvents = false;
            client = h.Client(config);
            client.Begin();
            RavensightLogEntry first = new RavensightLogEntry();
            first.Level = RavensightLogLevel.Exception;
            first.Text = "Exception: first";
            client.ProcessLog(first);
            await client.InitAsync();
            await client.FlushAsync();
            Assert.Equal(new[] { "error", "late" }, h.Transport.SentEventNames().ToArray());
        }

        // --- Ads and attribution ----------------------------------------------------------------------

        [Fact]
        public async Task AdConformanceOneSevenKeys()
        {
            Harness h = new Harness();
            RavensightClient client = await Quiet(h);
            Assert.True(client.TrackAdRevenue("admob", "rewarded", "level_end", "1500", "USD"));
            await client.FlushAsync();
            Dictionary<string, object> data = RavensightJson.AsObject(FakeTransport.EventsOf(h.Transport.To("/track/batch").Last())[0]["data"]);
            Assert.Equal(new[] { "rs_ad_format", "rs_ad_network", "rs_ad_placement", "rs_ad_result", "rs_currency", "rs_event_id", "rs_kind", "rs_revenue_micros" },
                data.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            Assert.Equal("1500", data["rs_revenue_micros"]);
            Assert.Equal("shown", data["rs_ad_result"]);
            Assert.Equal("ad", data["rs_kind"]);
        }

        [Fact]
        public void AdConformanceTwoMicrosOnTheDigits()
        {
            Assert.Equal("1500", RavensightDelivery.MicrosFromRevenue(0.0015));
            Assert.Equal("1234568", RavensightDelivery.MicrosFromRevenue(1.23456789));
            Assert.Equal("300000", RavensightDelivery.MicrosFromRevenue(0.1 + 0.2));
            Assert.Equal("0", RavensightDelivery.MicrosFromRevenue(1e-7));
            Assert.Equal("1", RavensightDelivery.MicrosFromRevenue(5e-7));
            Assert.Null(RavensightDelivery.MicrosFromRevenue(-1));
            Assert.Equal("1500", RavensightDelivery.MicrosFromDecimalString("0.0015"));
            Assert.Equal("1000000", RavensightDelivery.MicrosFromDecimalString("0.9999995"));
            Assert.Equal("1500", RavensightDelivery.MicrosFromDecimalString("1.5E-3"));
            Assert.Equal("500000", RavensightDelivery.MicrosFromDecimalString(".5"));
            Assert.Null(RavensightDelivery.MicrosFromDecimalString("abc"));
            Assert.Null(RavensightDelivery.MicrosFromDecimalString("-1"));
            Assert.Null(RavensightDelivery.MicrosFromDecimalString("1000000000000"));
        }

        [Fact]
        public async Task AdConformanceThreeRevenueWithoutACurrencyIsNeverSent()
        {
            Harness h = new Harness();
            RavensightClient client = await Quiet(h);
            Assert.False(client.TrackAdRevenue("admob", "rewarded", "x", "1500", null));
            Assert.False(client.TrackAd("admob", "banner", "clicked", null, "10", "USD"));
            Assert.False(client.TrackAd("admob", "banner", "shown", null, "1.5", "USD"));
            Assert.False(client.TrackAd("admob", "video", "shown"));
            Assert.False(client.TrackAd("admob", "banner", "seen"));
            Assert.False(client.TrackAd("", "banner", "shown"));
            Assert.False(client.TrackAd("admob", "banner", "shown", ""));
            Assert.False(client.TrackAd("admob", "banner", "shown", null, null, "USD"));
            Assert.Equal(0, client.QueueCount);
            Assert.True(client.TrackAdRevenueFromMediation("applovin", "REWARDED_INTER", "menu", 0.0015, "usd"));
            Assert.True(client.TrackAdMobPaidEvent(2500, "EUR", null, "banner", null));
            await client.FlushAsync();
            List<Dictionary<string, object>> sent = h.Transport.To("/track/batch").SelectMany(FakeTransport.EventsOf).ToList();
            Dictionary<string, object> max = RavensightJson.AsObject(sent[0]["data"]);
            Assert.Equal("rewarded", max["rs_ad_format"]);
            Assert.Equal("1500", max["rs_revenue_micros"]);
            Assert.Equal("USD", max["rs_currency"]);
            Dictionary<string, object> admob = RavensightJson.AsObject(sent[1]["data"]);
            Assert.Equal("admob", admob["rs_ad_network"]);
            Assert.False(admob.ContainsKey("rs_ad_placement"));
        }

        [Fact]
        public void MediationFormatNamesMap()
        {
            Assert.Equal("banner", RavensightDelivery.AdFormatFrom("MREC"));
            Assert.Equal("interstitial", RavensightDelivery.AdFormatFrom("INTER"));
            Assert.Equal("rewarded", RavensightDelivery.AdFormatFrom("rewarded_video"));
            Assert.Equal("native", RavensightDelivery.AdFormatFrom("NATIVE"));
            Assert.Equal("app_open", RavensightDelivery.AdFormatFrom("APP_OPEN"));
            Assert.Equal("other", RavensightDelivery.AdFormatFrom("SPLASH"));
        }

        [Fact]
        public async Task AttributionConformanceFourOncePerDeviceAcrossRestarts()
        {
            Harness h = new Harness();
            RavensightClient first = await Quiet(h);
            Assert.True(first.SetAttribution("store_referrer", "google", "spring", null, null, 1767225600000L));
            Assert.False(first.SetAttribution("organic"));
            await first.FlushAsync();
            Dictionary<string, object> data = RavensightJson.AsObject(FakeTransport.EventsOf(h.Transport.To("/track/batch").Last())[0]["data"]);
            Assert.Equal("attribution", data["rs_kind"]);
            Assert.Equal("store_referrer", data["rs_attr_source"]);
            Assert.Equal("google", data["rs_attr_network"]);
            Assert.Equal("spring", data["rs_campaign"]);
            Assert.Equal(1767225600000.0, data["rs_touch_time"]);
            Assert.False(data.ContainsKey("rs_adset"));

            RavensightConfig again = h.Config();
            again.TrackLifecycleEvents = false;
            RavensightClient second = await h.Started(again);
            Assert.False(second.SetAttribution("organic"));
            second.ResetDeviceId();
            Assert.True(second.SetAttribution("organic"), "a new device id sends its own");
            Assert.False(second.SetAttribution(""));
        }

        // --- Exposure -------------------------------------------------------------------------------

        private const string Etag = "\"\\\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\\\"\"";

        private static void Experiments(Harness h, Func<string> experiments)
        {
            h.Transport.Routes["/ingest/config"] = r => FakeTransport.Json(200,
                "{\"values\":{\"speed\":2},\"experiments\":" + experiments() + ",\"etag\":" + Etag + "}");
        }

        private static Dictionary<string, object> Queued(RavensightClient client, Harness h, string name)
        {
            return h.Transport.To("/track/batch").SelectMany(FakeTransport.EventsOf)
                .Where(e => (string)e["event"] == name).Select(e => RavensightJson.AsObject(e["data"])).LastOrDefault();
        }

        [Fact]
        public async Task ExposureOnlyAfterATypedReadWithOneExposureEvent()
        {
            Harness h = new Harness();
            Experiments(h, () => "[{\"key\":\"difficulty\",\"variant\":\"easy\"}]");
            RavensightClient client = await Quiet(h);
            client.Track("before");
            Assert.Equal(2.0, client.GetConfigNumber("speed", 1));
            client.GetConfigNumber("speed", 1);
            client.Track("after");
            Dictionary<string, object> own = new Dictionary<string, object>();
            own["rs_exp_difficulty"] = "mine";
            client.Track("own", own);
            await client.FlushAsync();
            Assert.False(Queued(client, h, "before").ContainsKey("rs_exp_difficulty"));
            Assert.Equal("easy", Queued(client, h, "after")["rs_exp_difficulty"]);
            Assert.Equal("mine", Queued(client, h, "own")["rs_exp_difficulty"]);
            Assert.Single(h.Transport.SentEventNames().Where(n => n == "rs_exposure"));
            Assert.Equal("easy", Queued(client, h, "rs_exposure")["rs_exp_difficulty"]);
        }

        [Fact]
        public async Task ANewSessionStartsUntaggedAndAStoppedExperimentDropsOut()
        {
            Harness h = new Harness();
            string experiments = "[{\"key\":\"difficulty\",\"variant\":\"easy\"},{\"key\":\"shop\",\"variant\":\"b\"}]";
            Experiments(h, () => experiments);
            RavensightConfig config = h.Config();
            config.TrackLifecycleEvents = false;
            config.SessionTimeoutSeconds = 60;
            RavensightClient client = await h.Started(config);
            client.GetConfigString("x", "");
            experiments = "[{\"key\":\"difficulty\",\"variant\":\"easy\"}]";
            await client.FetchConfigAsync();
            client.Track("stopped");
            await client.FlushAsync();
            Assert.False(Queued(client, h, "stopped").ContainsKey("rs_exp_shop"));
            Assert.Equal("easy", Queued(client, h, "stopped")["rs_exp_difficulty"]);

            client.EnterBackground();
            h.Clock.Advance(61000);
            await client.EnterForegroundAsync();
            client.Track("new_session");
            await client.FlushAsync();
            Assert.False(Queued(client, h, "new_session").ContainsKey("rs_exp_difficulty"));
            client.GetConfigBool("x", false);
            client.Track("read_again");
            await client.FlushAsync();
            Assert.Equal("easy", Queued(client, h, "read_again")["rs_exp_difficulty"]);
            Assert.Equal(2, h.Transport.SentEventNames().Count(n => n == "rs_exposure"));
        }

        [Fact]
        public async Task AQueuedOfflineEventKeepsItsOriginalTag()
        {
            string dir = TempDir();
            Harness h = new Harness();
            Experiments(h, () => "[{\"key\":\"difficulty\",\"variant\":\"easy\"}]");
            h.Transport.Routes["/track/batch"] = r => FakeTransport.Json(0, null);
            RavensightConfig config = h.Config();
            config.TrackLifecycleEvents = false;
            config.QueueDirectory = dir;
            RavensightClient client = await h.Started(config);
            client.GetConfigNumber("speed", 0);
            client.Track("offline_event");
            client.Dispose();

            Harness next = new Harness();
            Experiments(next, () => "[{\"key\":\"difficulty\",\"variant\":\"hard\"}]");
            RavensightConfig again = next.Config();
            again.TrackLifecycleEvents = false;
            again.QueueDirectory = dir;
            RavensightClient relaunched = await next.Started(again);
            await relaunched.FlushAsync();
            Assert.Equal("easy", Queued(relaunched, next, "offline_event")["rs_exp_difficulty"]);
            Directory.Delete(dir, true);
        }

        // --- Device fields ------------------------------------------------------------------------------

        [Fact]
        public async Task TheSessionCarriesCleanedDeviceFields()
        {
            Harness h = new Harness();
            RavensightConfig config = h.Config();
            config.DeviceModel = "  iPhone15,2\u0001 ";
            config.DeviceOsVersion = RavensightDelivery.OsVersionFrom("iOS 17.2");
            config.DeviceGpu = new string('g', 200);
            await h.Started(config);
            Dictionary<string, object> device = RavensightJson.AsObject(FakeTransport.BodyOf(h.Transport.To("/session")[0])["device"]);
            Assert.Equal("iPhone15,2", device["model"]);
            Assert.Equal("17.2", device["osVersion"]);
            Assert.Equal(new string('g', 128), device["gpu"]);

            Harness bare = new Harness();
            await bare.Started();
            Assert.False(FakeTransport.BodyOf(bare.Transport.To("/session")[0]).ContainsKey("device"));
        }

        [Fact]
        public void OsVersionDropsTheOsName()
        {
            Assert.Equal("14.2.1", RavensightDelivery.OsVersionFrom("Mac OS X 14.2.1"));
            Assert.Equal("14 / API-34 (UP1A)", RavensightDelivery.OsVersionFrom("Android OS 14 / API-34 (UP1A)"));
            Assert.Equal("11  (10.0.22631) 64bit", RavensightDelivery.OsVersionFrom("Windows 11  (10.0.22631) 64bit"));
            Assert.Null(RavensightDelivery.OsVersionFrom("Linux"));
            Assert.Null(RavensightDelivery.OsVersionFrom(null));
        }
    }
}
