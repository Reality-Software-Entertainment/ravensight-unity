using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Threading.Tasks;
namespace RealitySE {
    /// <summary>Build once and retain for retries. Screenshot bytes are copied, never captured automatically.</summary>
    public sealed class RavensightReport {
        public readonly string ClientId, Message, Category;
        public readonly long CapturedAt;
        internal readonly byte[] Image;
        internal string Session, Endpoint, Device;
        internal bool Busy;
        public RavensightReport(string message, byte[] screenshotPng = null, string category = "other", string clientId = null, long? capturedAt = null) {
            ClientId = clientId ?? Guid.NewGuid().ToString("N"); CapturedAt = capturedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Message = message; Category = category; Image = screenshotPng == null ? null : (byte[])screenshotPng.Clone();
            if (string.IsNullOrWhiteSpace(message) || message.Length > 500 || !RavensightClient.FeedbackCategories.Contains(category) || !System.Text.RegularExpressions.Regex.IsMatch(ClientId, "^[a-zA-Z0-9_-]{8,80}$")) throw new ArgumentException("Invalid Player Report.");
            // PNG only in this helper. The server still validates and sanitizes the complete image.
            if (Image != null) {
                if (Image.Length < 24 || Image.Length > 1000000 || BitConverter.ToString(Image, 0, 8) != "89-50-4E-47-0D-0A-1A-0A" || Encoding.ASCII.GetString(Image, 12, 4) != "IHDR") throw new ArgumentException("Screenshot must be PNG, at most 1 MB.");
                long w = 0, h = 0; for (int i = 16; i < 20; i++) w = w * 256 + Image[i]; for (int i = 20; i < 24; i++) h = h * 256 + Image[i];
                if (w < 1 || h < 1 || w > 4000000 || h > 4000000 || w * h > 4000000) throw new ArgumentException("Screenshot exceeds 4 million pixels.");
            }
        }
    }
    public sealed class RavensightReportReceipt {
        public string Id, Status, ClientId;
        public bool Complete { get { return Status == "complete"; } }
    }
    public sealed partial class RavensightClient {
        /// <summary>One attempt per step. A pending/processing receipt is not success; resubmit the same object in the same session to check again.</summary>
        public async Task<RavensightReportReceipt> SubmitReportAsync(RavensightReport report) {
            if (report == null) throw new ArgumentNullException("report");
            lock(report) { if (report.Busy) throw new InvalidOperationException("Report already in flight."); report.Busy = true; }
            try {
                if (!IsActive) throw new RavensightException("Tracking disabled", "disabled", 0);
                if (report.CapturedAt < _nowMs()/1000 - 86400 || report.CapturedAt > _nowMs()/1000 + 60) throw new ArgumentException("Report capture time must be within one day.");
                string token = await EnsureSessionAsync();
                if (report.Session == null) { report.Session = token; report.Endpoint = ApiUrl; report.Device = DeviceId; }
                CheckReportScope(report);
                var body = new Dictionary<string, object> { {"clientId",report.ClientId},{"capturedAt",report.CapturedAt},{"message",report.Message},{"category",report.Category},{"image",report.Image != null} };
                if (report.Image != null) { body["bytes"] = report.Image.Length; using(var hash=SHA256.Create()) body["sha256"] = BitConverter.ToString(hash.ComputeHash(report.Image)).Replace("-", "").ToLowerInvariant(); }
                var state = await ReportCall(report, "POST", "", body);
                string id = RavensightJson.GetString(state, "id", "");
                if (string.IsNullOrEmpty(id)) throw new RavensightException("Missing report ID", "invalid_response",0);
                string status = RavensightJson.GetString(state,"status","");
                if (status == "pending" && report.Image != null) {
                    var form = await ReportCall(report,"POST","/"+Uri.EscapeDataString(id)+"/upload",new Dictionary<string,object>());
                    Uri url; if (!Uri.TryCreate(RavensightJson.GetString(form,"url",""),UriKind.Absolute,out url) || url.Scheme != "https" || !string.IsNullOrEmpty(url.UserInfo)) throw new RavensightException("Invalid upload URL","upload_failed",0);
                    var fields = form.ContainsKey("fields") ? form["fields"] as Dictionary<string,object> : null;
                    if (fields == null) throw new RavensightException("Missing upload fields","upload_failed",0);
                    string boundary = "rs"+Guid.NewGuid().ToString("N");
                    byte[] upload;
                    using(var stream = new MemoryStream()) {
                        Action<string> append = text => { var bytes=Encoding.UTF8.GetBytes(text); stream.Write(bytes,0,bytes.Length); };
                        foreach(var field in fields) {
                            if (field.Key.IndexOfAny(new[]{'\r','\n','"'})>=0 || !(field.Value is string)) throw new RavensightException("Invalid upload field","upload_failed",0);
                            append("--"+boundary+"\r\nContent-Disposition: form-data; name=\""+field.Key+"\"\r\n\r\n"+field.Value+"\r\n");
                        }
                        append("--"+boundary+"\r\nContent-Disposition: form-data; name=\"file\"; filename=\"screenshot.png\"\r\nContent-Type: image/png\r\n\r\n"); stream.Write(report.Image,0,report.Image.Length); append("\r\n--"+boundary+"--\r\n"); upload=stream.ToArray();
                    }
                    CheckReportScope(report);
                    var request=new RavensightRequest("POST",url.AbsoluteUri,null).WithHeader("Content-Type","multipart/form-data; boundary="+boundary); request.RawBody=upload; request.NoRedirect=true;
                    var response=await Send(request); CheckReportScope(report);
                    if(response.Status<200 || response.Status>=300) throw new RavensightException("Screenshot upload failed","upload_failed",response.Status);
                    state=await ReportCall(report,"POST","/"+Uri.EscapeDataString(id)+"/finalize",new Dictionary<string,object>());
                    status=RavensightJson.GetString(state,"status","");
                }
                if(status!="complete" && status!="pending" && status!="processing") throw new RavensightException("Report not accepted",status=="canceled"?"report_canceled":"invalid_response",0);
                return new RavensightReportReceipt {Id=id,Status=status,ClientId=report.ClientId};
            } finally { lock(report) report.Busy=false; }
        }
        private void CheckReportScope(RavensightReport report) {
            if(!IsActive) throw new RavensightException("Tracking disabled","disabled",0);
            if(report.Session!=SessionToken || report.Endpoint!=ApiUrl || report.Device!=DeviceId) throw new RavensightException("Report session changed; do not automatically create another submission","report_session_changed",0);
        }
        private async Task<Dictionary<string,object>> ReportCall(RavensightReport report,string method,string path,Dictionary<string,object> body) {
            CheckReportScope(report);
            var response=await Send(WithPlaytest(new RavensightRequest(method,ApiUrl+"/reports"+path,RavensightJson.Write(body)).WithHeader("Content-Type","application/json").WithHeader("X-Session-Token",report.Session)));
            CheckReportScope(report);
            if(response.Status==403) HandleForbidden(response);
            var json=response.Json() as Dictionary<string,object>;
            if(response.Status<200 || response.Status>=300 || json==null) throw new RavensightException("Report request failed",response.ErrorCode("report_failed"),response.Status);
            return json;
        }
    }
}
