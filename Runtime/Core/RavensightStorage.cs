using System.Collections.Generic;

namespace RealitySE
{
    /// <summary>
    /// Where the SDK keeps the few values that outlive a run: the device id,
    /// the player's opt-out and the background marker. The Unity layer backs
    /// it with PlayerPrefs; tests use <see cref="RavensightMemoryStorage"/>.
    /// </summary>
    public interface IRavensightStorage
    {
        /// <summary>The stored string, or null when the key is absent.</summary>
        string GetString(string key);
        void SetString(string key, string value);
        int GetInt(string key, int fallback);
        void SetInt(string key, int value);
        void DeleteKey(string key);
        /// <summary>Writes pending changes to disk.</summary>
        void Save();
    }

    /// <summary>In memory storage. Nothing survives the process.</summary>
    public sealed class RavensightMemoryStorage : IRavensightStorage
    {
        private readonly Dictionary<string, object> _values = new Dictionary<string, object>();
        private readonly object _gate = new object();

        /// <summary>Counts every write and delete, so a test can prove nothing was written.</summary>
        public int Writes { get; private set; }

        public string GetString(string key)
        {
            lock (_gate)
            {
                object value;
                return _values.TryGetValue(key, out value) ? value as string : null;
            }
        }

        public void SetString(string key, string value)
        {
            lock (_gate)
            {
                _values[key] = value;
                Writes++;
            }
        }

        public int GetInt(string key, int fallback)
        {
            lock (_gate)
            {
                object value;
                if (_values.TryGetValue(key, out value) && value is int) return (int)value;
                return fallback;
            }
        }

        public void SetInt(string key, int value)
        {
            lock (_gate)
            {
                _values[key] = value;
                Writes++;
            }
        }

        public void DeleteKey(string key)
        {
            lock (_gate)
            {
                if (_values.Remove(key)) Writes++;
            }
        }

        public bool HasKey(string key)
        {
            lock (_gate) { return _values.ContainsKey(key); }
        }

        public void Save()
        {
        }
    }
}
