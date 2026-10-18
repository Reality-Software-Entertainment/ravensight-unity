using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
namespace RealitySE.Tests
{
    public class RemoteConfigTests
    {
        private const string Etag = "\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"";
        private static string Document(string values = "{\"speed\":3,\"on\":true,\"title\":\"hello\",\"object\":{\"x\":1}}", string experiments = "[{\"key\":\"intro\",\"variant\":\"short\"}]")
        { return "{\"values\":" + values + ",\"experiments\":" + experiments + ",\"etag\":" + RavensightJson.Write(Etag) + "}"; }
        private sealed class Transport : IRavensightTransport
        {
            public readonly FakeTransport Other = new FakeTransport();
            public readonly List<RavensightRequest> Config = new List<RavensightRequest>();
            public Func<RavensightRequest, Task<RavensightResponse>> Reply = r => Task.FromResult(FakeTransport.Json(200, Document()));
            public Task<RavensightResponse> SendAsync(RavensightRequest r)
            { if (!r.Url.Contains("/ingest/config?")) return Other.SendAsync(r); Config.Add(r); return Reply(r); }
        }
        [Fact] public async Task SessionFetchTypedFallbackAndQueuedExposureSnapshot()
        {
            var h = new Harness(); var t = new Transport(); var c = new RavensightClient(h.Config(), t, h.Clock.Now);
            c.Track("before"); int ready = 0; c.ConfigReady += () => ready++;
            await c.InitAsync();
            Assert.Single(t.Config); Assert.Equal("gt_live_test", t.Config[0].Headers["X-API-Key"]);
            Assert.Equal(3d, c.GetConfigNumber("speed", 5)); Assert.Equal("fallback", c.GetConfigString("speed", "fallback"));
            Assert.True(c.GetConfigBool("on", false)); Assert.Equal("hello", c.GetConfigString("title", ""));
            var json = (Dictionary<string, object>)c.GetConfigJson("object", null); json["x"] = 100;
            Assert.Equal(1d, ((Dictionary<string, object>)c.GetConfigJson("object", null))["x"]);
            c.Track("after", new Dictionary<string, object> { { "rs_exp_fake", "bad" } }); await c.FlushAsync();
            var events = t.Other.To("/track/batch").SelectMany(FakeTransport.EventsOf).ToList();
            Assert.False(((Dictionary<string, object>)events[0]["data"]).ContainsKey("rs_exp_intro"));
            var data = (Dictionary<string, object>)events[1]["data"];
            Assert.Equal("short", data["rs_exp_intro"]); Assert.False(data.ContainsKey("rs_exp_fake")); Assert.Equal(1, ready);
        }
        [Fact] public async Task ScopedCacheEtagOfflineAndConsentPurge()
        {
            var h = new Harness(); var t = new Transport(); var c = new RavensightClient(h.Config(), t, h.Clock.Now);
            await c.FetchConfigAsync(); c.Dispose();
            t.Reply = r => Task.FromResult(FakeTransport.Json(304, ""));
            c = new RavensightClient(h.Config(), t, h.Clock.Now); Assert.True(await c.FetchConfigAsync());
            Assert.Equal(Etag, t.Config.Last().Headers["If-None-Match"]); Assert.Equal(3d, c.GetConfigNumber("speed", 0));
            t.Reply = r => Task.FromResult(RavensightResponse.NetworkFailure("offline"));
            await c.FetchConfigAsync(); Assert.Equal(3d, c.GetConfigNumber("speed", 0));
            c.SetTrackingEnabled(false); Assert.Equal(0d, c.GetConfigNumber("speed", 0)); c.SetTrackingEnabled(true);
            await c.FetchConfigAsync(); Assert.Equal(0d, c.GetConfigNumber("speed", 0));
            var config = h.Config(); config.GameVersion = "other"; var other = new RavensightClient(config, t, h.Clock.Now);
            await other.FetchConfigAsync(); Assert.Equal(0d, other.GetConfigNumber("speed", 0));
        }
        [Fact] public async Task LateResponseCannotRestoreResetIdentityOrConsent()
        {
            var h = new Harness(); var t = new Transport(); var c = new RavensightClient(h.Config(), t, h.Clock.Now);
            var delayed = new TaskCompletionSource<RavensightResponse>(); t.Reply = r => delayed.Task;
            var request = c.FetchConfigAsync(); c.ResetDeviceId(); delayed.SetResult(FakeTransport.Json(200, Document()));
            Assert.False(await request); Assert.Equal(0d, c.GetConfigNumber("speed", 0));
            delayed = new TaskCompletionSource<RavensightResponse>(); request = c.FetchConfigAsync(); c.SetTrackingEnabled(false); c.SetTrackingEnabled(true);
            delayed.SetResult(FakeTransport.Json(200, Document())); Assert.False(await request);
        }
        [Fact] public async Task ReservedPropertiesRemainForbiddenButExperimentLabelsAreValues()
        {
            var h = new Harness(); var t = new Transport(); var c = new RavensightClient(h.Config(), t, h.Clock.Now);
            foreach (string word in new[] { "constructor", "prototype" })
            {
                t.Reply = r => Task.FromResult(FakeTransport.Json(200, Document("{\"" + word + "\":1}")));
                Assert.False(await c.FetchConfigAsync());
                t.Reply = r => Task.FromResult(FakeTransport.Json(200, Document("{\"nested\":{\"" + word + "\":1}}")));
                Assert.False(await c.FetchConfigAsync());
                t.Reply = r => Task.FromResult(FakeTransport.Json(200, Document("{}", "[{\"key\":\"" + word + "\",\"variant\":\"" + word + "\"}]")));
                Assert.True(await c.FetchConfigAsync());
            }
        }
        [Fact] public async Task InvalidDocumentsAreAtomicAndRefusalsClearCache()
        {
            var h = new Harness(); var t = new Transport(); var c = new RavensightClient(h.Config(), t, h.Clock.Now); await c.FetchConfigAsync();
            foreach (string bad in new[] { Document("{\"speed\":{\"constructor\":1}}"), Document("{\"speed\":\"" + new string('a',8193) + "\"}"), Document("{}", "[{\"key\":\"intro\",\"variant\":\"INVALID\"}]") })
            { t.Reply = r => Task.FromResult(FakeTransport.Json(200, bad)); Assert.False(await c.FetchConfigAsync()); Assert.Equal(3d, c.GetConfigNumber("speed", 0)); }
            t.Reply = r => Task.FromResult(FakeTransport.Json(403, "{}")); Assert.False(await c.FetchConfigAsync()); Assert.Equal(0d, c.GetConfigNumber("speed", 0));
        }
        [Fact] public async Task BoundsAndNewestResponseWin()
        {
            var h = new Harness(); var t = new Transport(); var c = new RavensightClient(h.Config(), t, h.Clock.Now);
            var first = new TaskCompletionSource<RavensightResponse>(); t.Reply = r => first.Task;
            var pending = c.FetchConfigAsync(); t.Reply = r => Task.FromResult(FakeTransport.Json(200, Document("{\"speed\":7}")));
            await c.FetchConfigAsync(); first.SetResult(FakeTransport.Json(200, Document())); Assert.False(await pending);
            Assert.Equal(7d, c.GetConfigNumber("speed", 0));
            string manyKeys = "{" + string.Join(",", Enumerable.Range(0, 51).Select(i => "\"key_" + i + "\":1")) + "}";
            string manyExperiments = "[" + string.Join(",", Enumerable.Range(0, 11).Select(i => "{\"key\":\"exp_" + i + "\",\"variant\":\"a\"}")) + "]";
            string deep = new string('[', 21) + "0" + new string(']', 21);
            foreach (string bad in new[] { Document(manyKeys), Document("{}", manyExperiments), Document("{\"deep\":" + deep + "}"), Document() + "trailing" })
            { t.Reply = r => Task.FromResult(FakeTransport.Json(200, bad)); Assert.False(await c.FetchConfigAsync()); Assert.Equal(7d, c.GetConfigNumber("speed", 0)); }
        }
        [Fact] public async Task OptInMakesNoConfigRequestOrCacheReadBeforeConsent()
        {
            var h = new Harness(); var t = new Transport(); var config = h.Config(); config.Consent = RavensightConsent.OptIn;
            var c = new RavensightClient(config, t, h.Clock.Now); Assert.False(await c.FetchConfigAsync()); Assert.Empty(t.Config);
            Assert.Equal(0d, c.GetConfigNumber("speed", 0));
        }
    }
}
