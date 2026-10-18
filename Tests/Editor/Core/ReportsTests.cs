using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
namespace RealitySE.Tests {
 public class ReportsTests {
  private static byte[] Png() { var b=new byte[24]; Array.Copy(new byte[]{137,80,78,71,13,10,26,10},b,8); Array.Copy(Encoding.ASCII.GetBytes("IHDR"),0,b,12,4); b[19]=1;b[23]=1;return b; }
  [Fact] public async Task UploadIsCredentialFreeAndRetryKeepsIdentity() {
   var h=new Harness();var c=await h.Started();
   h.Transport.Routes["/reports"]=r=>FakeTransport.Json(201,"{\"id\":\"r\",\"status\":\"pending\"}");
   h.Transport.Routes["/reports/r/upload"]=r=>FakeTransport.Json(200,"{\"url\":\"https://storage.example/upload\",\"fields\":{\"key\":\"image\"}}");
   h.Transport.Routes["https://storage.example/upload"]=r=>FakeTransport.Json(204,"");
   h.Transport.Routes["/reports/r/finalize"]=r=>FakeTransport.Json(202,"{\"id\":\"r\",\"status\":\"processing\"}");
   var report=new RavensightReport("Broken",Png(),capturedAt:h.Clock.NowMs/1000);
   var receipt=await c.SubmitReportAsync(report);Assert.False(receipt.Complete);Assert.Equal("processing",receipt.Status);
   var upload=h.Transport.Requests.Single(r=>r.Url.StartsWith("https://storage.example"));Assert.Single(upload.Headers);Assert.True(upload.NoRedirect);Assert.Contains("name=\"file\"",Encoding.UTF8.GetString(upload.RawBody));
   var first=h.Transport.To("/reports").Last().Body;
   h.Transport.Routes["/reports"]=r=>FakeTransport.Json(201,"{\"id\":\"r\",\"status\":\"complete\"}");
   Assert.True((await c.SubmitReportAsync(report)).Complete);Assert.Equal(first,h.Transport.To("/reports").Last().Body);
   c.ResetDeviceId();await Assert.ThrowsAsync<RavensightException>(()=>c.SubmitReportAsync(report));
  }
  [Fact] public async Task UnsafeUploadNeverFallsBackToText() {
   var h=new Harness();var c=await h.Started();h.Transport.Routes["/reports"]=r=>FakeTransport.Json(201,"{\"id\":\"r\",\"status\":\"pending\"}");
   h.Transport.Routes["/reports/r/upload"]=r=>FakeTransport.Json(200,"{\"url\":\"http://storage.example/upload\",\"fields\":{}}");
   await Assert.ThrowsAsync<RavensightException>(()=>c.SubmitReportAsync(new RavensightReport("x",Png(),capturedAt:h.Clock.NowMs/1000)));
   Assert.Single(h.Transport.To("/reports"));Assert.Empty(h.Transport.To("/feedback"));Assert.Empty(h.Transport.To("/reports/r/finalize"));
   Assert.Throws<ArgumentException>(()=>new RavensightReport("x",new byte[1000001]));
  }
 }
}
