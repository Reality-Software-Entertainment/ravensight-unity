using System.Collections.Generic;
using RealitySE;
using UnityEngine;

/// <summary>
/// A tour of the Ravensight API in one file. Drop this on any GameObject in
/// a scene that also has the Ravensight Analytics component, then press
/// play and watch the console with Verbose Logging turned on.
/// </summary>
public sealed class RavensightQuickStart : MonoBehaviour
{
    [SerializeField] private int level = 3;

    private void Start()
    {
        // Events queue immediately. Calls made before the Ravensight component
        // wakes are held and queued when it does, and everything is sent once
        // a session exists, so you never wait for the SDK before tracking.
        Ravensight.Track("sample_scene_loaded");

        Ravensight.Track("level_started", new Dictionary<string, object>
        {
            { "level", level },
            { "difficulty", "normal" },
            { "assists", new List<object> { "aim", "checkpoint" } },
        });
    }

    /// <summary>Call this from a button when the player clears a level.</summary>
    public void OnLevelCleared(int deaths, float seconds)
    {
        Ravensight.Track("level_completed", new Dictionary<string, object>
        {
            { "level", level },
            { "deaths", deaths },
            { "seconds", seconds },
        });

        // Optional. The timer would send these within 15 seconds anyway.
        Ravensight.Flush();
    }

    /// <summary>Call this from an in game feedback form.</summary>
    public async void OnFeedbackSubmitted(string message)
    {
        try
        {
            await Ravensight.SubmitFeedback(message, "suggestion", 4);
            Debug.Log("Thanks, feedback sent.");
        }
        catch (RavensightException e)
        {
            Debug.LogWarning("Ravensight: feedback rejected (" + e.Code + ")");
        }
    }

    /// <summary>Wire this to a privacy toggle in your settings menu. Remembered across launches.</summary>
    public void OnAnalyticsToggled(bool allowed)
    {
        Ravensight.SetTrackingEnabled(allowed);
    }

    /// <summary>Wire this to a "Forget me" button: a fresh random identity from now on.</summary>
    public void OnForgetMe()
    {
        Ravensight.ResetDeviceId();
    }
}
