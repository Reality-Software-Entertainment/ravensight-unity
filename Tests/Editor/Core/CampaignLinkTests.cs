using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace RealitySE.Tests
{
    /// <summary>
    /// The campaign link: one "campaignLink" field on POST /session, from the
    /// WebGL page's rs_link or from SetCampaignLink, sent with the next session
    /// this client opens for its own player and cleared only by a 201 answer.
    /// The JS SDK is the reference.
    /// </summary>
    public class CampaignLinkTests
    {
        private const string LinkId = "64b0000000000000000000a1";
        private const string OtherId = "64b0000000000000000000b2";

        private static RavensightConfig Quiet(Harness h)
        {
            RavensightConfig config = h.Config();
            config.TrackLifecycleEvents = false;
            config.SessionTimeoutSeconds = 60;
            return config;
        }

        /// <summary>The campaignLink of every POST /session so far, null where none was sent.</summary>
        private static List<string> SentLinks(Harness h)
        {
            return h.Transport.To("/session")
                .Select(r =>
                {
                    Dictionary<string, object> body = FakeTransport.BodyOf(r);
                    return body.ContainsKey("campaignLink") ? (string)body["campaignLink"] : null;
                })
                .ToList();
        }

        /// <summary>Background past the session timeout and back: the client opens a new session.</summary>
        private static async Task RenewSession(Harness h, RavensightClient client)
        {
            client.EnterBackground();
            h.Clock.Advance(61000);
            await client.EnterForegroundAsync();
            client.Track("after_renewal");
            await client.FlushAsync();
        }

        // --- Parsing the page's location.search -------------------------------------------

        [Theory]
        [InlineData("?lang=en&rs_link=64B0000000000000000000A1&utm_source=x", LinkId)]
        [InlineData("?rs_link=abc", null)]
        [InlineData("?rs_link=gggggggggggggggggggggggg", null)]
        [InlineData("?ref=x", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        [InlineData("?rs_link=64b0000000000000000000a1&rs_link=64b0000000000000000000b2", LinkId)]
        [InlineData("?a=1&rs_link=64b0000000000000000000a1#frag", LinkId)]
        [InlineData("?rs_link=%36%34b0000000000000000000a1", LinkId)]
        // URLSearchParams.get: the first rs_link wins even when it is not an id.
        [InlineData("?rs_link=abc&rs_link=64b0000000000000000000a1", null)]
        // "+" is a space, so the value is no longer 24 hex characters.
        [InlineData("?rs_link=+64b0000000000000000000a1", null)]
        [InlineData("?rs_link=64b0000000000000000000a1+", null)]
        [InlineData("?rs_link=%2064b0000000000000000000a1", null)]
        // The name is decoded too, and matched exactly.
        [InlineData("?rs%5Flink=64b0000000000000000000a1", LinkId)]
        [InlineData("?RS_LINK=64b0000000000000000000a1", null)]
        [InlineData("rs_link=64b0000000000000000000a1", LinkId)]
        [InlineData("?&&rs_link=64b0000000000000000000a1&", LinkId)]
        [InlineData("?rs_link", null)]
        [InlineData("?rs_link=", null)]
        [InlineData("?rs_link=%zz%", null)]
        [InlineData("?rs_link=64b0000000000000000000a1=", null)]
        [InlineData("?rs_link=64b0000000000000000000a", null)]
        [InlineData("?rs_link=64b0000000000000000000a1f", null)]
        public void CampaignLinkFromSearchFollowsLocationSearch(string search, string expected)
        {
            Assert.Equal(expected, RavensightClient.CampaignLinkFromSearch(search));
        }

        [Theory]
        [InlineData("64B0000000000000000000A1", LinkId)]
        [InlineData(LinkId, LinkId)]
        [InlineData("", null)]
        [InlineData(null, null)]
        [InlineData("64b0000000000000000000a", null)]
        [InlineData("64b0000000000000000000a1f", null)]
        [InlineData("gggggggggggggggggggggggg", null)]
        [InlineData(" 64b0000000000000000000a1", null)]
        [InlineData("64b0000000000000000000a1 ", null)]
        public void ValidCampaignLinkAcceptsOnly24HexCharacters(string id, string expected)
        {
            Assert.Equal(expected, RavensightClient.ValidCampaignLink(id));
        }

        // --- Once, lowercased, on the first session ----------------------------------------

        [Fact]
        public async Task TheSetterSendsTheIdLowercasedOnTheFirstSessionOnly()
        {
            Harness h = new Harness();
            RavensightClient client = h.Client(Quiet(h));
            Assert.True(client.SetCampaignLink(LinkId.ToUpperInvariant()));
            client.Begin();
            await client.InitAsync();
            await RenewSession(h, client);

            Assert.Equal(new string[] { LinkId, null }, SentLinks(h).ToArray());
            // Same request, same headers: nothing else about the session changes.
            Assert.Equal("gt_live_test", h.Transport.To("/session")[0].Headers["X-API-Key"]);
        }

        [Fact]
        public async Task TheWebGlPageLinkGoesOnceAndTheLaterSessionCarriesNone()
        {
            Harness h = new Harness();
            RavensightConfig config = Quiet(h);
            config.LandingSearch = "?lang=en&rs_link=64B0000000000000000000A1&utm_source=x";
            RavensightClient client = await h.Started(config);
            await RenewSession(h, client);

            Assert.Equal(new string[] { LinkId, null }, SentLinks(h).ToArray());
        }

        [Fact]
        public async Task NoLinkMeansNoField()
        {
            Harness h = new Harness();
            RavensightConfig config = Quiet(h);
            config.LandingSearch = "?ref=x";
            await h.Started(config);
            Assert.Equal(new string[] { null }, SentLinks(h).ToArray());
        }

        [Fact]
        public async Task ALinkSetAgainAfterTheFirstSessionGoesWithTheNextOne()
        {
            Harness h = new Harness();
            RavensightClient client = await h.Started(Quiet(h));
            Assert.True(client.SetCampaignLink(OtherId));
            await RenewSession(h, client);
            await RenewSession(h, client);

            Assert.Equal(new string[] { null, OtherId, null }, SentLinks(h).ToArray());
        }

        // --- Invalid input -------------------------------------------------------------------

        // Empty, 23 and 25 characters, 24 that are not hex, spaces either side, null.
        // (InlineData, not MemberData: Tests/Editor/XunitShim.cs maps only InlineData.)
        [Theory]
        [InlineData("")]
        [InlineData("aaaaaaaaaaaaaaaaaaaaaaa")]
        [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaa")]
        [InlineData("gggggggggggggggggggggggg")]
        [InlineData(" " + LinkId)]
        [InlineData(LinkId + " ")]
        [InlineData(null)]
        public async Task AnInvalidIdIsRefusedSendsNothingAndThrowsNothing(string id)
        {
            Harness h = new Harness();
            RavensightClient client = h.Client(Quiet(h));
            Assert.False(client.SetCampaignLink(id));
            client.Begin();
            await client.InitAsync();
            Assert.Equal(new string[] { null }, SentLinks(h).ToArray());
        }

        [Theory]
        [InlineData("")]
        [InlineData("aaaaaaaaaaaaaaaaaaaaaaa")]
        [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaa")]
        [InlineData("gggggggggggggggggggggggg")]
        [InlineData(" " + LinkId)]
        [InlineData(LinkId + " ")]
        [InlineData(null)]
        public async Task AnInvalidIdKeepsTheEarlierPendingLink(string id)
        {
            Harness h = new Harness();
            RavensightClient client = h.Client(Quiet(h));
            Assert.True(client.SetCampaignLink(LinkId));
            Assert.False(client.SetCampaignLink(id));
            client.Begin();
            await client.InitAsync();
            Assert.Equal(new string[] { LinkId }, SentLinks(h).ToArray());
        }

        [Fact]
        public async Task AValidCallReplacesThePendingLinkIncludingThePagesOwn()
        {
            Harness h = new Harness();
            RavensightConfig config = Quiet(h);
            config.LandingSearch = "?rs_link=" + LinkId;
            RavensightClient client = h.Client(config);
            Assert.True(client.SetCampaignLink(OtherId.ToUpperInvariant()));
            client.Begin();
            await client.InitAsync();
            Assert.Equal(new string[] { OtherId }, SentLinks(h).ToArray());
        }

        // --- Cleared only by a 201 --------------------------------------------------------

        [Theory]
        [InlineData(500)]
        [InlineData(503)]
        [InlineData(429)]
        [InlineData(0)]
        public async Task AFailedSessionKeepsTheLinkForTheNextAttempt(int failure)
        {
            Harness h = new Harness();
            int calls = 0;
            h.Transport.Routes["/session"] = r => ++calls == 1
                ? FakeTransport.Json(failure, failure == 0 ? null : "{\"error\":\"internal_error\"}")
                : FakeTransport.Json(201, "{\"token\":\"tok_" + calls + "\",\"expiresIn\":86400}");
            RavensightClient client = h.Client(Quiet(h));
            Assert.True(client.SetCampaignLink(LinkId));
            client.Begin();
            await client.InitAsync();
            Assert.Null(client.SessionToken);

            h.Clock.Advance(600000);
            client.Track("retry");
            await client.FlushAsync();
            Assert.Equal("tok_2", client.SessionToken);
            await RenewSession(h, client);

            Assert.Equal(new string[] { LinkId, LinkId, null }, SentLinks(h).ToArray());
        }

        [Fact]
        public async Task A201WithoutATokenKeepsTheLink()
        {
            Harness h = new Harness();
            int calls = 0;
            h.Transport.Routes["/session"] = r => ++calls == 1
                ? FakeTransport.Json(201, "{}")
                : FakeTransport.Json(201, "{\"token\":\"tok_" + calls + "\",\"expiresIn\":86400}");
            RavensightClient client = h.Client(Quiet(h));
            Assert.True(client.SetCampaignLink(LinkId));
            client.Begin();
            await client.InitAsync();
            h.Clock.Advance(600000);
            client.Track("retry");
            await client.FlushAsync();

            Assert.Equal(new string[] { LinkId, LinkId }, SentLinks(h).ToArray());
        }

        [Fact]
        public async Task ALinkSetWhileTheSessionIsInFlightWaitsForTheNextSession()
        {
            Harness h = new Harness();
            RavensightClient client = null;
            int calls = 0;
            h.Transport.Routes["/session"] = r =>
            {
                calls++;
                // The game handles a deep link while the first session is on the wire.
                if (calls == 1) Assert.True(client.SetCampaignLink(OtherId));
                return FakeTransport.Json(201, "{\"token\":\"tok_" + calls + "\",\"expiresIn\":86400}");
            };
            client = h.Client(Quiet(h));
            Assert.True(client.SetCampaignLink(LinkId));
            client.Begin();
            await client.InitAsync();
            await RenewSession(h, client);
            await RenewSession(h, client);

            Assert.Equal(new string[] { LinkId, OtherId, null }, SentLinks(h).ToArray());
        }

        // --- Consent, opt-out and the key ------------------------------------------------

        [Fact]
        public async Task OptInSendsTheLinkWithTheFirstSessionAfterTheYes()
        {
            Harness h = new Harness();
            RavensightConfig config = Quiet(h);
            config.Consent = RavensightConsent.OptIn;
            config.LandingSearch = "?rs_link=" + LinkId;
            RavensightClient client = h.Client(config);
            client.Begin();
            await client.InitAsync();
            Assert.Empty(h.Transport.To("/session"));

            client.SetTrackingEnabled(true);
            await client.InitAsync();
            Assert.Equal(new string[] { LinkId }, SentLinks(h).ToArray());
        }

        [Fact]
        public async Task AnOptedOutPlayerSendsNothing()
        {
            Harness h = new Harness();
            h.Storage.SetInt(RavensightClient.TrackingEnabledKey, 0);
            RavensightConfig config = Quiet(h);
            config.LandingSearch = "?rs_link=" + LinkId;
            RavensightClient client = h.Client(config);
            Assert.True(client.SetCampaignLink(OtherId));
            client.Begin();
            await client.InitAsync();
            client.Track("x");
            await client.FlushAsync();
            Assert.Empty(h.Transport.Requests);
        }

        [Fact]
        public async Task ADisabledClientSendsNothingAndTheLinkGoesWhenItIsEnabled()
        {
            Harness h = new Harness();
            RavensightConfig config = Quiet(h);
            config.Enabled = false;
            RavensightClient client = h.Client(config);
            Assert.True(client.SetCampaignLink(LinkId));
            client.Begin();
            await client.InitAsync();
            Assert.Empty(h.Transport.Requests);

            client.Enabled = true;
            await client.InitAsync();
            Assert.Equal(new string[] { LinkId }, SentLinks(h).ToArray());
        }

        [Fact]
        public async Task NoKeySendsNothing()
        {
            Harness h = new Harness();
            RavensightConfig config = Quiet(h);
            config.IngestKey = "";
            config.LandingSearch = "?rs_link=" + LinkId;
            RavensightClient client = h.Client(config);
            Assert.True(client.SetCampaignLink(OtherId));
            client.Begin();
            await client.InitAsync();
            await client.FlushAsync();
            Assert.Empty(h.Transport.Requests);
        }

        // --- Never on a playtest run or a joined session ---------------------------------

        [Fact]
        public async Task APlaytestRunNeverSendsTheLink()
        {
            Harness h = new Harness();
            RavensightConfig config = Quiet(h);
            config.Playtest = new RavensightPlaytestContext("pt_token_abc", "run_42", "pt_job_1", "speedrunner");
            config.LandingSearch = "?rs_link=" + LinkId;
            RavensightClient client = h.Client(config);
            Assert.True(client.SetCampaignLink(OtherId));
            client.Begin();
            await client.InitAsync();
            await RenewSession(h, client);

            Assert.Equal(new string[] { null, null }, SentLinks(h).ToArray());
        }

        [Fact]
        public async Task AJoinedSessionDoesNotCarryOrClearTheLink()
        {
            Harness h = new Harness();
            RavensightConfig config = Quiet(h);
            config.JoinToken = "player_session_token";
            RavensightClient client = h.Client(config);
            Assert.True(client.SetCampaignLink(LinkId));
            client.Begin();
            await client.InitAsync();
            await RenewSession(h, client);

            List<RavensightRequest> sessions = h.Transport.To("/session");
            Assert.Equal("player_session_token", FakeTransport.BodyOf(sessions[0])["joinToken"]);
            Assert.False(FakeTransport.BodyOf(sessions[1]).ContainsKey("joinToken"));
            Assert.Equal(new string[] { null, LinkId }, SentLinks(h).ToArray());
        }

        // --- Nothing kept ----------------------------------------------------------------------

        [Fact]
        public async Task NothingIsWrittenToStorage()
        {
            Harness h = new Harness();
            RavensightClient client = h.Client(Quiet(h));
            int before = h.Storage.Writes;
            Assert.True(client.SetCampaignLink(LinkId));
            Assert.False(client.SetCampaignLink("nope"));
            Assert.Equal(before, h.Storage.Writes);
            client.Begin();
            await client.InitAsync();
            Assert.Equal(new string[] { LinkId }, SentLinks(h).ToArray());

            // A new client on the same storage has nothing pending.
            Harness next = new Harness();
            next.Storage = h.Storage;
            await next.Started(Quiet(next));
            Assert.Equal(new string[] { null }, SentLinks(next).ToArray());
        }
    }
}
