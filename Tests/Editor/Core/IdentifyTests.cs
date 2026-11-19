using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace RealitySE.Tests
{
    /// <summary>
    /// Identify and ResetIdentity against the binding vectors (identify-vectors.json,
    /// copied from the platform repo's docs/identify/identify-vectors.json, version 2)
    /// and session answers that carry, change or leave out identify.salt.
    /// </summary>
    public class IdentifyTests
    {
        private static object _vectors;

        private static object Vectors
        {
            get
            {
                if (_vectors == null)
                {
                    string path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ThisFile()), "identify-vectors.json");
                    _vectors = RavensightJson.Parse(System.IO.File.ReadAllText(path));
                }
                return _vectors;
            }
        }

        private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "")
        {
            return path;
        }

        private static string Salt { get { return RavensightJson.GetString(Vectors, "salt", null); } }

        private static object SaltsDiffer { get { return RavensightJson.AsObject(Vectors)["salts_differ"]; } }

        private static Dictionary<string, object> Step(int index, string startsWith)
        {
            Dictionary<string, object> step = RavensightJson.AsObject(RavensightJson.GetArray(Vectors, "sequence")[index]);
            Assert.True(((string)step["step"]).StartsWith(startsWith, StringComparison.Ordinal), (string)step["step"]);
            return step;
        }

        private static string ExpectedSend(Dictionary<string, object> step)
        {
            object expect = RavensightJson.AsObject(step)["expect"];
            return RavensightJson.GetString(RavensightJson.GetArray(expect, "sends")[0], "accountHash", null);
        }

        private static string ExpectedStored(Dictionary<string, object> step)
        {
            Dictionary<string, object> expect = RavensightJson.AsObject(step["expect"]);
            return RavensightJson.GetString(expect["stored"], "accountHash", null);
        }

        /// <summary>Numbers sessions S1, S2, ... and answers each with Salt (null: no identify object).</summary>
        private sealed class SaltServer
        {
            public string Salt = IdentifyTests.Salt;
            public int Sessions;
            public Func<RavensightRequest, RavensightResponse> IdentifyAnswer;

            public void Install(FakeTransport transport)
            {
                transport.Routes["/session"] = request =>
                {
                    Sessions++;
                    string body = "{\"token\":\"S" + Sessions + "\",\"expiresIn\":86400";
                    if (Salt != null) body += ",\"identify\":{\"salt\":\"" + Salt + "\"}";
                    return FakeTransport.Json(201, body + "}");
                };
                transport.Routes["/identify"] = request => IdentifyAnswer != null
                    ? IdentifyAnswer(request)
                    : FakeTransport.Json(200, "{\"playerId\":\"px_0\",\"linked\":true,\"verified\":false,\"repeat\":false}");
            }
        }

        private static List<KeyValuePair<string, string>> Sends(Harness h)
        {
            return h.Transport.To("/identify")
                .Select(r => new KeyValuePair<string, string>(
                    r.Headers["X-Session-Token"],
                    RavensightJson.GetString(FakeTransport.BodyOf(r), "accountHash", null)))
                .ToList();
        }

        private static string StoredHash(Harness h)
        {
            string text = h.Storage.GetString(RavensightClient.IdentityKey);
            return text == null ? null : RavensightJson.GetString(RavensightJson.Parse(text), "accountHash", null);
        }

        private static string StoredSalt(Harness h)
        {
            string text = h.Storage.GetString(RavensightClient.IdentityKey);
            return text == null ? null : RavensightJson.GetString(RavensightJson.Parse(text), "salt", null);
        }

        private static (Harness, SaltServer) Setup()
        {
            Harness h = new Harness();
            SaltServer server = new SaltServer();
            server.Install(h.Transport);
            return (h, server);
        }

        private static async Task OpenSession(RavensightClient client)
        {
            client.Track("tick");
            await client.FlushAsync();
        }

        private static ArgumentException Refused(Action call)
        {
            try
            {
                call();
            }
            catch (ArgumentException e)
            {
                if (e.GetType() == typeof(ArgumentException)) return e;
                throw;
            }
            Assert.True(false, "expected an ArgumentException, nothing was thrown");
            return null;
        }

        // --- 1. accept vectors ------------------------------------------------------

        [Fact]
        public void VectorsAreVersion2()
        {
            double version;
            Assert.True(RavensightJson.TryGetDouble(Vectors, "version", out version));
            Assert.Equal(2d, version);
            Assert.Equal(64, Salt.Length);
        }

        [Fact]
        public void EveryAcceptVectorHashesToItsWire()
        {
            int checkedCount = 0;
            foreach (object entry in RavensightJson.GetArray(Vectors, "accept"))
            {
                // The integer case: C# takes a string only.
                string id = RavensightJson.AsObject(entry)["accountId"] as string;
                if (id == null) continue;
                Assert.Null(RavensightClient.IdentifyRefusal(id));
                string wire = RavensightJson.GetString(entry, "wire", null);
                Assert.Equal(wire, RavensightClient.IdentifyAccountHash(Salt, id));
                Assert.Equal(RavensightJson.GetString(entry, "playerId", null), "px_" + wire.Substring(0, 32));
                checkedCount++;
            }
            Assert.Equal(10, checkedCount);

            string account = RavensightJson.GetString(SaltsDiffer, "accountId", null);
            Assert.Equal(RavensightJson.GetString(SaltsDiffer, "wire_a", null),
                RavensightClient.IdentifyAccountHash(RavensightJson.GetString(SaltsDiffer, "salt_a", null), account));
            Assert.Equal(RavensightJson.GetString(SaltsDiffer, "wire_b", null),
                RavensightClient.IdentifyAccountHash(RavensightJson.GetString(SaltsDiffer, "salt_b", null), account));
        }

        [Fact]
        public async Task EveryAcceptVectorReachesTheWireThroughAClient()
        {
            foreach (object entry in RavensightJson.GetArray(Vectors, "accept"))
            {
                string id = RavensightJson.AsObject(entry)["accountId"] as string;
                if (id == null) continue;
                var (h, _) = Setup();
                RavensightClient client = h.Client();
                client.Begin();
                client.Identify(id);
                await client.InitAsync();
                Assert.Single(Sends(h));
                Assert.Equal(RavensightJson.GetString(entry, "wire", null), Sends(h)[0].Value);
                Assert.Equal(RavensightJson.GetString(entry, "wire", null), StoredHash(h));
            }
        }

        // --- 2. refuse vectors ------------------------------------------------------

        [Fact]
        public async Task EveryRefuseVectorIsRefusedWithNothingSentOrStored()
        {
            int checkedCount = 0;
            foreach (object entry in RavensightJson.GetArray(Vectors, "refuse"))
            {
                object raw = RavensightJson.AsObject(entry)["accountId"];
                // A fraction cannot be passed as a string; null can, and is refused.
                if (raw != null && !(raw is string)) continue;
                string id = (string)raw;
                string reason = RavensightJson.GetString(entry, "reason", null);
                Assert.Equal(reason, RavensightClient.IdentifyRefusal(id));

                var (h, _) = Setup();
                RavensightClient client = await h.Started();
                string device = client.DeviceId;
                int writes = h.Storage.Writes;
                ArgumentException refused = Refused(() => client.Identify(id));
                Assert.Equal("accountId", refused.ParamName);
                Assert.True(refused.Message.Contains(reason), refused.Message);
                Assert.False(client.IsIdentified);
                await OpenSession(client);
                Assert.Empty(Sends(h));
                Assert.Null(h.Storage.GetString(RavensightClient.IdentityKey));
                Assert.Equal(device, client.DeviceId);
                checkedCount++;
            }
            Assert.Equal(8, checkedCount);
        }

        [Fact]
        public async Task ARefusalKeepsWhatIsStored()
        {
            var (h, _) = Setup();
            RavensightClient client = await h.Started();
            client.Identify("u1");
            string before = h.Storage.GetString(RavensightClient.IdentityKey);
            Assert.NotNull(before);
            Refused(() => client.Identify(new string('a', 257)));
            Refused(() => client.Identify(""));
            Assert.Equal(before, h.Storage.GetString(RavensightClient.IdentityKey));
            Assert.True(client.IsIdentified);
        }

        // --- 3. the sequence --------------------------------------------------------

        [Fact]
        public async Task TheSequenceHolds()
        {
            var (h, server) = Setup();

            // 0: identify before any session: held in memory only.
            Step(0, "init with a key and consent");
            RavensightClient client = h.Client();
            client.Begin();
            client.Identify("u1");
            Assert.True(client.IsIdentified);
            Assert.Empty(Sends(h));
            Assert.Null(h.Storage.GetString(RavensightClient.IdentityKey));

            // 1: S1 opens with the salt: hashed, stored, sent on S1.
            var s1 = Step(1, "session S1 opens");
            await client.InitAsync();
            Assert.Single(Sends(h));
            Assert.Equal("S1", Sends(h)[0].Key);
            Assert.Equal(ExpectedSend(s1), Sends(h)[0].Value);
            Assert.Equal(ExpectedStored(s1), StoredHash(h));
            Assert.Equal(Salt, StoredSalt(h));

            // 2: the same account again in S1: nothing.
            Step(2, "identify(\"u1\") again in S1");
            string device = client.DeviceId;
            client.Identify("u1");
            await OpenSession(client);
            Assert.Single(Sends(h));
            Assert.Equal(device, client.DeviceId);

            // 3: relaunch, S2 opens with the same salt, no call: sent on S2.
            var s3 = Step(3, "relaunch, session S2 opens");
            client.Dispose();
            client = await h.Started();
            Assert.True(client.IsIdentified);
            Assert.Equal(2, Sends(h).Count);
            Assert.Equal("S2", Sends(h)[1].Key);
            Assert.Equal(ExpectedSend(s3), Sends(h)[1].Value);
            Assert.Equal(device, client.DeviceId);

            // 4: an email is refused; nothing changes.
            var s4 = Step(4, "identify(\"player@example.com\")");
            Refused(() => client.Identify("player@example.com"));
            Assert.Equal(2, Sends(h).Count);
            Assert.Equal(ExpectedStored(s4), StoredHash(h));
            Assert.Equal(device, client.DeviceId);

            // 5: another account: fresh device and session S3, sent on S3.
            var s5 = Step(5, "identify(\"u2\")");
            client.Identify("u2");
            Assert.NotEqual(device, client.DeviceId);
            Assert.Equal(ExpectedStored(s5), StoredHash(h));
            await OpenSession(client);
            Assert.Equal(3, Sends(h).Count);
            Assert.Equal("S3", Sends(h)[2].Key);
            Assert.Equal(ExpectedSend(s5), Sends(h)[2].Value);

            // 6: ResetIdentity: fresh device, S4, nothing sent, nothing stored.
            Step(6, "resetIdentity()");
            string beforeReset = client.DeviceId;
            client.ResetIdentity();
            Assert.NotEqual(beforeReset, client.DeviceId);
            Assert.False(client.IsIdentified);
            Assert.Null(h.Storage.GetString(RavensightClient.IdentityKey));
            await OpenSession(client);
            Assert.Equal(4, server.Sessions);
            Assert.Equal(3, Sends(h).Count);

            // 7: reset again with nothing stored: nothing at all.
            Step(7, "resetIdentity() again");
            string kept = client.DeviceId;
            client.ResetIdentity();
            Assert.Equal(kept, client.DeviceId);
            await OpenSession(client);
            Assert.Equal(4, server.Sessions);
            Assert.Equal(3, Sends(h).Count);

            // 8: u3 answered 429 needs_funds: stored, sent once on S4, no loop.
            var s8 = Step(8, "identify(\"u3\")");
            server.IdentifyAnswer = r => FakeTransport.Json(429, "{\"error\":\"quota_exceeded\",\"reason\":\"needs_funds\"}");
            client.Identify("u3");
            Assert.Equal(4, Sends(h).Count);
            Assert.Equal("S4", Sends(h)[3].Key);
            Assert.Equal(ExpectedSend(s8), Sends(h)[3].Value);
            Assert.Equal(ExpectedStored(s8), StoredHash(h));
            await OpenSession(client);
            await client.FlushAsync();
            Assert.Equal(4, Sends(h).Count);

            // 9: relaunch under another salt: the stored identity is deleted, never sent.
            Step(9, "relaunch; session S5 answers a DIFFERENT salt");
            server.IdentifyAnswer = null;
            server.Salt = RavensightJson.GetString(SaltsDiffer, "salt_b", null);
            client.Dispose();
            client = await h.Started();
            Assert.Equal(4, Sends(h).Count);
            Assert.Null(h.Storage.GetString(RavensightClient.IdentityKey));
            Assert.False(client.IsIdentified);

            // 10: an answer with no salt: unavailable this launch, the held id dropped.
            Step(10, "identify(\"u4\")");
            server.Salt = null;
            client.Dispose();
            client = h.Client();
            client.Begin();
            client.Identify("u4");
            await client.InitAsync();
            Assert.False(client.IsIdentified);
            client.Identify("u4");
            await OpenSession(client);
            Assert.False(client.IsIdentified);
            Assert.Equal(4, Sends(h).Count);
            Assert.Null(h.Storage.GetString(RavensightClient.IdentityKey));

            // 11: opt out deletes the stored identity.
            Step(11, "opt out");
            server.Salt = Salt;
            client.Dispose();
            client = await h.Started();
            client.Identify("u1");
            Assert.Equal(5, Sends(h).Count);
            Assert.NotNull(h.Storage.GetString(RavensightClient.IdentityKey));
            client.Enabled = false;
            Assert.Null(h.Storage.GetString(RavensightClient.IdentityKey));
            Assert.False(client.IsIdentified);
            client.Enabled = true;
            await OpenSession(client);
            Assert.Equal(5, Sends(h).Count);
        }

        [Fact]
        public async Task NeedsFundsIsRetriedAtTheNextSession()
        {
            var (h, server) = Setup();
            int answered = 0;
            server.IdentifyAnswer = r => ++answered == 1
                ? FakeTransport.Json(429, "{\"error\":\"quota_exceeded\",\"reason\":\"needs_funds\"}")
                : FakeTransport.Json(200, "{\"playerId\":\"px_0\"}");
            RavensightClient client = await h.Started();
            client.Identify("u3");
            Assert.Single(Sends(h));
            await OpenSession(client);
            Assert.Single(Sends(h));
            // ResetDeviceId keeps the identity: the next session identifies again.
            client.ResetDeviceId();
            Assert.True(client.IsIdentified);
            await OpenSession(client);
            Assert.Equal(2, Sends(h).Count);
            Assert.Equal("S2", Sends(h)[1].Key);
            Assert.Equal(Sends(h)[0].Value, Sends(h)[1].Value);
        }

        [Theory]
        [InlineData(400, "{\"error\":\"invalid_account_hash\"}", false)]
        [InlineData(400, "{\"error\":\"no_session\"}", true)]
        [InlineData(404, "{}", false)]
        [InlineData(409, "{\"error\":\"session_taken\"}", true)]
        [InlineData(409, "{\"error\":\"synthetic_session\"}", true)]
        [InlineData(429, "{\"error\":\"identify_limited\"}", true)]
        [InlineData(429, "{\"error\":\"rate_limited\"}", true)]
        [InlineData(503, "{}", true)]
        [InlineData(0, "", true)]
        public async Task OtherAnswersFollowTheTable(int status, string body, bool nextSessionRetries)
        {
            var (h, server) = Setup();
            server.IdentifyAnswer = r => FakeTransport.Json(status, body);
            RavensightClient client = await h.Started();
            client.Identify("u1");
            Assert.Single(Sends(h));
            await OpenSession(client);
            Assert.Single(Sends(h)); // never in a loop
            Assert.NotNull(h.Storage.GetString(RavensightClient.IdentityKey)); // kept
            client.ResetDeviceId();
            await OpenSession(client);
            Assert.Equal(nextSessionRetries ? 2 : 1, Sends(h).Count);
        }

        // --- 4. the request ----------------------------------------------------------

        [Fact]
        public async Task TheRequestCarriesTheKeyTheSessionAndOnlyTheHash()
        {
            var (h, _) = Setup();
            RavensightClient client = await h.Started();
            client.Identify("12345"); // in an open session with the salt known: at once
            RavensightRequest request = Assert.Single(h.Transport.To("/identify"));
            Assert.Equal("POST", request.Method);
            Assert.Equal("https://api.ravensight.io/api/v1/identify", request.Url);
            Assert.Equal("gt_live_test", request.Headers["X-API-Key"]);
            Assert.Equal("S1", request.Headers["X-Session-Token"]);
            string wire = RavensightJson.GetString(RavensightJson.GetArray(Vectors, "accept")[0], "wire", null);
            Assert.Equal("{\"accountHash\":\"" + wire + "\"}", request.Body);
            Assert.False(request.Body.Contains("deviceId"));
            Assert.False(request.Body.Contains(client.DeviceId));
        }

        [Fact]
        public async Task IdentifyNeverEntersTheEventQueue()
        {
            var (h, server) = Setup();
            server.IdentifyAnswer = r => FakeTransport.Json(0, "");
            RavensightClient client = await h.Started();
            int queued = client.QueueCount;
            client.Identify("u1");
            Assert.Single(Sends(h));
            Assert.Equal(queued, client.QueueCount);
            client.Track("after");
            await client.FlushAsync();
            Assert.Contains("after", h.Transport.SentEventNames());
            Assert.False(h.Transport.To("/track/batch").Any(r => r.Body.Contains(Sends(h)[0].Value)));
            Assert.False(h.Storage.GetString(RavensightClient.IdentityKey).Contains("u1\""));
        }

        // --- 5. no key, no consent ---------------------------------------------------

        [Fact]
        public void WithoutAKeyNothingIsSentOrStored()
        {
            var (h, _) = Setup();
            RavensightConfig config = h.Config();
            config.IngestKey = null;
            config.Optional = true;
            RavensightClient client = h.Client(config);
            client.Identify("u1");
            Refused(() => client.Identify("a@b"));
            client.ResetIdentity();
            client.Identify("u1");
            Assert.Empty(h.Transport.Requests);
            Assert.Equal(0, h.Storage.Writes);
        }

        [Fact]
        public async Task BeforeConsentTheIdIsHeldThenSentOnTheYes()
        {
            var (h, _) = Setup();
            RavensightConfig config = h.Config();
            config.Consent = RavensightConsent.OptIn;
            RavensightClient client = h.Client(config);
            client.Begin();
            client.Identify("u1");
            Assert.True(client.IsIdentified);
            await client.InitAsync();
            Assert.Empty(h.Transport.Requests);
            Assert.Null(h.Storage.GetString(RavensightClient.IdentityKey));
            client.SetTrackingEnabled(true);
            await client.InitAsync();
            Assert.Single(Sends(h));
            Assert.Equal(ExpectedSend(Step(1, "session S1 opens")), Sends(h)[0].Value);
            Assert.NotNull(h.Storage.GetString(RavensightClient.IdentityKey));
        }

        [Fact]
        public async Task ConsentWithdrawnDeletesTheIdentityAndTheHeldId()
        {
            var (h, _) = Setup();
            RavensightClient client = await h.Started();
            client.Identify("u1");
            Assert.Single(Sends(h));
            client.SetTrackingEnabled(false);
            Assert.Null(h.Storage.GetString(RavensightClient.IdentityKey));
            Assert.False(client.IsIdentified);
            // A held id goes too.
            client.Identify("u2");
            Assert.True(client.IsIdentified);
            client.SetTrackingEnabled(false);
            Assert.False(client.IsIdentified);
            client.SetTrackingEnabled(true);
            await OpenSession(client);
            Assert.Single(Sends(h));
        }

        [Fact]
        public async Task ResetIdentityDropsAHeldIdToo()
        {
            var (h, _) = Setup();
            RavensightClient client = h.Client();
            client.Begin();
            string device = client.DeviceId;
            client.Identify("u1"); // held: no session yet
            client.ResetIdentity();
            Assert.False(client.IsIdentified);
            Assert.NotEqual(device, client.DeviceId);
            await client.InitAsync();
            await OpenSession(client);
            Assert.Empty(Sends(h));
        }

        // --- a late answer ------------------------------------------------------------

        /// <summary>Holds the first /identify answer until the test releases it.</summary>
        private sealed class GatedTransport : IRavensightTransport
        {
            public readonly FakeTransport Inner;
            public readonly TaskCompletionSource<RavensightResponse> Gate = new TaskCompletionSource<RavensightResponse>();
            private int _identifies;

            public GatedTransport(FakeTransport inner)
            {
                Inner = inner;
            }

            public Task<RavensightResponse> SendAsync(RavensightRequest request)
            {
                if (FakeTransport.PathOf(request) == "/identify" && ++_identifies == 1)
                {
                    Inner.Requests.Add(request);
                    return Gate.Task;
                }
                return Inner.SendAsync(request);
            }
        }

        [Theory]
        [InlineData(200)]
        [InlineData(404)]
        public async Task ALateAnswerAfterResetDoesNotMarkTheNewSession(int lateStatus)
        {
            var (h, _) = Setup();
            GatedTransport gated = new GatedTransport(h.Transport);
            RavensightClient client = new RavensightClient(h.Config(), gated, h.Clock.Now);
            client.Begin();
            await client.InitAsync();
            client.Identify("u1");
            Assert.Single(Sends(h));
            // Sign out and in as another account while the u1 answer is in flight.
            client.ResetIdentity();
            client.Identify("u2");
            gated.Gate.SetResult(FakeTransport.Json(lateStatus, lateStatus == 200 ? "{\"playerId\":\"px_0\"}" : "{}"));
            await OpenSession(client);
            Assert.Equal(2, Sends(h).Count);
            Assert.NotEqual(Sends(h)[0].Key, Sends(h)[1].Key);
            Assert.NotEqual(Sends(h)[0].Value, Sends(h)[1].Value);
            Assert.Equal(Sends(h)[1].Value, StoredHash(h));
        }
    }
}
