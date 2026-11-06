using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RealitySE
{
    /// <summary>
    /// The persisted queue: one JSON file per ingest key,
    /// <c>queue-&lt;16 hex&gt;.json</c> in the configured directory (the Unity
    /// layer uses Application.persistentDataPath/Ravensight), written through
    /// a temporary file and a rename so a partial write never replaces a good
    /// one, and read back once at start. Format version 2 of the delivery
    /// contract: groups of events, each with the origin of the session it was
    /// queued under. The session's signing secret is never written here.
    /// </summary>
    public sealed class RavensightQueueStore
    {
        /// <summary>The most events ever written, across every group.</summary>
        public const int MaxEvents = 500;
        /// <summary>Events older than this are dropped at load: the server refuses anything past 90 days.</summary>
        public const long MaxAgeMs = 89L * 86400L * 1000L;
        public const int FormatVersion = 2;
        /// <summary>The capture trail: never written to the queue file.</summary>
        public const string TrailKey = "rs_trail";

        private readonly object _gate = new object();

        public readonly string FilePath;

        /// <summary>The one temporary file a write goes through: the queue file's path plus ".tmp".</summary>
        public string TempPath { get { return FilePath + ".tmp"; } }

        /// <summary>
        /// Marks a path excluded from backups and says whether it worked. Null
        /// where the platform has no app backup to opt out of. The Unity layer
        /// sets it on iOS.
        /// </summary>
        public Func<string, bool> ExcludeFromBackup;

        /// <summary>A warning sink: it is never given a path or queue contents.</summary>
        public Action<string> Warn;

        private bool _warned;

        public RavensightQueueStore(string directory, string ingestKey)
        {
            FilePath = Path.Combine(directory, "queue-" + RavensightDelivery.QueueKeyHash(ingestKey) + ".json");
        }

        private bool Exclude(string path)
        {
            Func<string, bool> exclude = ExcludeFromBackup;
            if (exclude == null) return true;
            try { return exclude(path); }
            catch (Exception) { return false; }
        }

        private void WarnOnce(string message)
        {
            if (_warned) return;
            _warned = true;
            Action<string> warn = Warn;
            if (warn != null) warn(message);
        }

        /// <summary>
        /// At load: marks the queue folder and an existing queue file excluded
        /// from backups, so files written by older versions are covered too.
        /// </summary>
        public void ExcludeExisting()
        {
            try
            {
                string directory = Path.GetDirectoryName(FilePath);
                bool ok = true;
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)) ok = Exclude(directory);
                if (File.Exists(FilePath)) ok = Exclude(FilePath) && ok;
                if (!ok) WarnOnce("Ravensight: the queue folder could not be excluded from backups.");
            }
            catch (Exception)
            {
                // A missing folder has nothing to exclude.
            }
        }

        /// <summary>What a load found.</summary>
        public sealed class LoadResult
        {
            public readonly List<RavensightEvent> Events = new List<RavensightEvent>();
            /// <summary>Dropped as older than 89 days.</summary>
            public int Expired;
            /// <summary>Dropped past the 500 event cap, oldest first.</summary>
            public int Trimmed;
        }

        /// <summary>
        /// Reads the file. A missing, unreadable, malformed or unknown version
        /// file is an empty queue, never an exception. A version 1 file is read
        /// as groups without an origin, and second timestamps as milliseconds.
        /// </summary>
        public LoadResult Load(long nowMs)
        {
            LoadResult result = new LoadResult();
            string text;
            try
            {
                lock (_gate)
                {
                    if (!File.Exists(FilePath)) return result;
                    text = File.ReadAllText(FilePath, Encoding.UTF8);
                }
            }
            catch (Exception)
            {
                return result;
            }
            Dictionary<string, object> root = RavensightJson.AsObject(RavensightJson.Parse(text));
            double version;
            if (root == null || !RavensightJson.TryGetDouble(root, "version", out version)) return result;
            if (version != 1 && version != 2) return result;
            List<object> groups = RavensightJson.GetArray(root, "groups");
            if (groups == null) return result;

            foreach (object rawGroup in groups)
            {
                Dictionary<string, object> group = RavensightJson.AsObject(rawGroup);
                if (group == null) continue;
                Dictionary<string, object> origin = null;
                object rawOrigin;
                if (version == 2 && group.TryGetValue("origin", out rawOrigin))
                {
                    origin = RavensightJson.AsObject(rawOrigin);
                    if (origin != null && !(RavensightJson.GetString(origin, "session", null) is string)) origin = null;
                }
                List<object> events = RavensightJson.GetArray(group, "events");
                if (events == null) continue;
                foreach (object rawEvent in events)
                {
                    Dictionary<string, object> entry = RavensightJson.AsObject(rawEvent);
                    string name = RavensightJson.GetString(entry, "event", null);
                    double stamp;
                    if (string.IsNullOrEmpty(name) || !RavensightJson.TryGetDouble(entry, "timestamp", out stamp) || stamp <= 0) continue;
                    long timestamp = (long)stamp;
                    if (timestamp < 100000000000L) timestamp *= 1000L;
                    if (nowMs - timestamp > MaxAgeMs)
                    {
                        result.Expired++;
                        continue;
                    }
                    object rawData;
                    Dictionary<string, object> data = entry.TryGetValue("data", out rawData)
                        ? RavensightJson.AsObject(rawData) : null;
                    RavensightEvent evt = new RavensightEvent();
                    evt.Name = name;
                    evt.Data = data ?? new Dictionary<string, object>();
                    // A trail never comes back from the file, even from an older one.
                    evt.Data.Remove(TrailKey);
                    evt.Timestamp = timestamp;
                    evt.Origin = origin;
                    bool captured;
                    evt.Captured = RavensightJson.TryGetBool(entry, "auto", out captured) && captured;
                    result.Events.Add(evt);
                }
            }
            if (result.Events.Count > MaxEvents)
            {
                result.Trimmed = result.Events.Count - MaxEvents;
                result.Events.RemoveRange(0, result.Trimmed);
            }
            return result;
        }

        /// <summary>
        /// Writes the events, grouped by origin in order, keeping the newest
        /// 500. An empty queue deletes the file. Returns false when the write
        /// failed; the queue in memory is unaffected either way.
        /// </summary>
        public bool Save(IList<RavensightEvent> events)
        {
            try
            {
                lock (_gate)
                {
                    if (events == null || events.Count == 0)
                    {
                        DeleteFiles();
                        return true;
                    }
                    string text = Serialize(events);
                    string directory = Path.GetDirectoryName(FilePath);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    string temp = TempPath;
                    try
                    {
                        File.WriteAllText(temp, text, new UTF8Encoding(false));
                        // Excluded before it takes the queue file's place: the
                        // attribute moves with the file on a same-volume rename.
                        bool excluded = Exclude(temp);
                        if (!File.Exists(FilePath))
                        {
                            File.Move(temp, FilePath);
                        }
                        else
                        {
                            try
                            {
                                // An atomic swap where the file system has one.
                                File.Replace(temp, FilePath, null);
                            }
                            catch (Exception)
                            {
                                // A platform without File.Replace: the old file is
                                // whole until this point, and the new one is complete.
                                File.Delete(FilePath);
                                File.Move(temp, FilePath);
                            }
                        }
                        if (!excluded)
                        {
                            WarnOnce("Ravensight: could not exclude the queue file from backups; retrying on the final file.");
                            if (!Exclude(FilePath)) WarnAlways("Ravensight: the queue file could not be excluded from backups.");
                        }
                        return true;
                    }
                    catch (Exception)
                    {
                        // A failed write never leaves a temporary file behind.
                        try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
                        return false;
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void WarnAlways(string message)
        {
            Action<string> warn = Warn;
            if (warn != null) warn(message);
        }

        /// <summary>Deletes the queue file and the temporary file a write may have left. Call under the gate.</summary>
        private void DeleteFiles()
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            if (File.Exists(TempPath)) File.Delete(TempPath);
        }

        public static string Serialize(IList<RavensightEvent> events)
        {
            int start = Math.Max(0, events.Count - MaxEvents);
            List<object> groups = new List<object>();
            Dictionary<string, object> current = null;
            string currentSession = null;
            for (int i = start; i < events.Count; i++)
            {
                RavensightEvent evt = events[i];
                string session = evt.Origin == null ? "" : RavensightJson.GetString(evt.Origin, "session", "");
                if (current == null || session != currentSession)
                {
                    current = new Dictionary<string, object>();
                    if (evt.Origin != null) current["origin"] = evt.Origin;
                    current["events"] = new List<object>();
                    groups.Add(current);
                    currentSession = session;
                }
                Dictionary<string, object> entry = new Dictionary<string, object>();
                entry["event"] = evt.Name;
                entry["timestamp"] = evt.Timestamp;
                Dictionary<string, object> data = evt.Data ?? new Dictionary<string, object>();
                if (data.ContainsKey(TrailKey))
                {
                    // A trail is never written to the queue file: it stays in memory only.
                    data = new Dictionary<string, object>(data);
                    data.Remove(TrailKey);
                }
                entry["data"] = data;
                if (evt.Captured) entry["auto"] = true;
                ((List<object>)current["events"]).Add(entry);
            }
            Dictionary<string, object> root = new Dictionary<string, object>();
            root["version"] = FormatVersion;
            root["groups"] = groups;
            return RavensightJson.Write(root);
        }

        public bool Exists
        {
            get
            {
                try { return File.Exists(FilePath); }
                catch (Exception) { return false; }
            }
        }

        public void Delete()
        {
            try
            {
                lock (_gate)
                {
                    DeleteFiles();
                }
            }
            catch (Exception)
            {
                // Nothing to do: the next save overwrites it.
            }
        }
    }
}
