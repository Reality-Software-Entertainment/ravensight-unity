using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RealitySE
{
    public sealed partial class RavensightClient
    {
        private Dictionary<string, object> _configValues = new Dictionary<string, object>();
        private Dictionary<string, string> _assignments = new Dictionary<string, string>();
        private string _configScope, _configEtag;
        private int _configGeneration, _configRequest;
        public event Action ConfigReady;

        private string ConfigScope()
        {
            using (SHA256 hash = SHA256.Create())
                return "ravensight_config_" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(
                    RavensightJson.Write(new object[] { ApiUrl, _config.IngestKey, DeviceId, _config.Platform, _config.GameVersion })))).Replace("-", "");
        }

        private void ClearConfig(bool forget)
        {
            lock (_gate)
            {
                if (forget && !_inert && DeviceId != null) { _storage.DeleteKey(_configScope ?? ConfigScope()); _storage.Save(); }
                _configGeneration++;
                _configScope = null;
                _configEtag = null;
                _configValues.Clear();
                _assignments.Clear();
            }
        }

        private static bool SafeValue(object value, int depth)
        {
            if (depth > 20) return false;
            if (value == null || value is string || value is bool) return true;
            if (value is double) return !double.IsNaN((double)value) && !double.IsInfinity((double)value);
            var map = value as Dictionary<string, object>;
            if (map != null)
            {
                foreach (var item in map)
                    if (item.Key == "__proto__" || item.Key == "prototype" || item.Key == "constructor" || !SafeValue(item.Value, depth + 1)) return false;
                return true;
            }
            var list = value as List<object>;
            if (list == null) return false;
            foreach (var item in list) if (!SafeValue(item, depth + 1)) return false;
            return true;
        }

        private bool ApplyConfig(string body)
        {
            if (body == null || Encoding.UTF8.GetByteCount(body) > 512000) return false;
            var root = RavensightJson.AsObject(RavensightJson.Parse(body));
            object raw;
            if (root == null || !root.TryGetValue("values", out raw)) return false;
            var values = raw as Dictionary<string, object>;
            var experiments = RavensightJson.GetArray(root, "experiments");
            string etag = RavensightJson.GetString(root, "etag", "");
            if (values == null || values.Count > 50 || experiments == null || experiments.Count > 10 || !Regex.IsMatch(etag, "^\"[a-f0-9]{64}\"$")) return false;
            foreach (var item in values)
                if (!Regex.IsMatch(item.Key, "^[a-z][a-z0-9_]{0,63}$") || item.Key == "constructor" || item.Key == "prototype" || !SafeValue(item.Value, 0) || Encoding.UTF8.GetByteCount(RavensightJson.Write(item.Value)) > 8192) return false;
            var assignments = new Dictionary<string, string>();
            foreach (var item in experiments)
            {
                string key = RavensightJson.GetString(item, "key", "");
                string variant = RavensightJson.GetString(item, "variant", "");
                if (!Regex.IsMatch(key, "^[a-z][a-z0-9_]{0,63}$") || !Regex.IsMatch(variant, "^[a-z][a-z0-9_-]{0,39}$") || assignments.ContainsKey(key)) return false;
                assignments.Add(key, variant);
            }
            _configValues = values; _assignments = assignments; _configEtag = etag;
            return true;
        }

        /// <summary>Refresh public game configuration. Offline failures keep only the same scoped cache.</summary>
        public async Task<bool> FetchConfigAsync()
        {
            int generation, request;
            string scope, etag;
            lock (_gate)
            {
                if (!IsActive || DeviceId == null || _invalidKey) return false;
                scope = ConfigScope();
                if (_configScope != scope)
                {
                    ClearConfig(false);
                    _configScope = scope;
                    ApplyConfig(_storage.GetString(scope));
                }
                generation = _configGeneration; request = ++_configRequest; etag = _configEtag;
            }
            var req = WithPlaytest(new RavensightRequest("GET", ApiUrl + "/ingest/config?deviceId=" + Uri.EscapeDataString(DeviceId)
                + (string.IsNullOrEmpty(_config.Platform) ? "" : "&platform=" + Uri.EscapeDataString(_config.Platform))
                + (string.IsNullOrEmpty(_config.GameVersion) ? "" : "&gameVersion=" + Uri.EscapeDataString(_config.GameVersion)), null)
                .WithHeader("X-API-Key", _config.IngestKey));
            if (etag != null) req.WithHeader("If-None-Match", etag);
            RavensightResponse response;
            try { response = await Send(req); } catch { response = RavensightResponse.NetworkFailure("offline"); }
            lock (_gate)
            {
                if (!IsActive || generation != _configGeneration || request != _configRequest || scope != ConfigScope()) return false;
                if (response.Status == 401 || response.Status == 403) { ClearConfig(true); return false; }
                bool fresh = response.Status == 200 && ApplyConfig(response.Body);
                if (fresh) { _storage.SetString(scope, response.Body); _storage.Save(); }
                Action handler = ConfigReady;
                if (handler != null) { try { handler(); } catch (Exception e) { Emit("config callback failed: " + e.Message); } }
                return fresh || (response.Status == 304 && etag != null);
            }
        }

        public string GetConfigString(string key, string fallback) { lock (_gate) { object v; return IsActive && _configValues.TryGetValue(key, out v) && v is string ? (string)v : fallback; } }
        public bool GetConfigBool(string key, bool fallback) { lock (_gate) { object v; return IsActive && _configValues.TryGetValue(key, out v) && v is bool ? (bool)v : fallback; } }
        public double GetConfigNumber(string key, double fallback) { lock (_gate) { object v; return IsActive && _configValues.TryGetValue(key, out v) && v is double ? (double)v : fallback; } }
        public object GetConfigJson(string key, object fallback) { lock (_gate) { object v; return IsActive && _configValues.TryGetValue(key, out v) && (v is Dictionary<string, object> || v is List<object>) ? RavensightJson.Parse(RavensightJson.Write(v)) : fallback; } }
        private void AddExposureTags(Dictionary<string, object> data)
        {
            lock (_gate)
            {
                foreach (string key in new List<string>(data.Keys)) if (key.StartsWith("rs_exp_", StringComparison.Ordinal)) data.Remove(key);
                if (IsActive) foreach (var item in _assignments) data["rs_exp_" + item.Key] = item.Value;
            }
        }
    }
}
