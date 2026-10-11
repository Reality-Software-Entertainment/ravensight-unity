using UnityEngine;

namespace RealitySE
{
    /// <summary>IRavensightStorage over PlayerPrefs. Main thread only, like PlayerPrefs itself.</summary>
    public sealed class RavensightPlayerPrefsStorage : IRavensightStorage
    {
        public string GetString(string key)
        {
            return PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key, null) : null;
        }

        public void SetString(string key, string value)
        {
            PlayerPrefs.SetString(key, value ?? "");
        }

        public int GetInt(string key, int fallback)
        {
            return PlayerPrefs.GetInt(key, fallback);
        }

        public void SetInt(string key, int value)
        {
            PlayerPrefs.SetInt(key, value);
        }

        public void DeleteKey(string key)
        {
            PlayerPrefs.DeleteKey(key);
        }

        public void Save()
        {
            PlayerPrefs.Save();
        }
    }
}
