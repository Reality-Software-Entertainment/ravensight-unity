# Ravensight for Unity

Official Unity SDK for [Ravensight](https://ravensight.io) player analytics.

Sessions, batched events, an offline queue, rate limit handling and a server
side kill switch, in one MonoBehaviour and no third party dependencies.

* API base: `https://api.ravensight.io/api/v1`
* Docs: https://ravensight.io/docs/
* Unity 2021.3 or newer

## Verification status

The protocol logic in `Runtime/Core` is written and
reviewed against the live API contract, and it is deliberately free of any
UnityEngine dependency so it can be tested on its own. It has **not** yet been
verified inside the Unity Editor or in a player build.

Treat it as pending in editor verification: read the code before you ship it in
a release build, and please open an issue with anything you hit. The JavaScript
and Godot SDKs speak the same protocol and are further along.

## Install

Unity Package Manager, add by git URL:

```
https://github.com/Reality-Software-Entertainment/ravensight-unity.git
```

Window > Package Manager > `+` > Add package from git URL, paste the line above.

Or add it to `Packages/manifest.json` yourself:

```json
{
  "dependencies": {
    "com.realityse.ravensight": "https://github.com/Reality-Software-Entertainment/ravensight-unity.git"
  }
}
```

To pin a version, append `#v0.1.0` to the URL.

## Quickstart

1. Create an empty GameObject in your first scene, name it `Ravensight`.
2. Add the **Ravensight Analytics** component to it.
3. Paste your publishable ingest key (`gt_live_...`) into the Ingest Key field.

That is the whole setup. The component survives scene loads, reads the server
kill switch, opens a session and starts flushing on a timer.

Then track from anywhere:

```csharp
using System.Collections.Generic;
using RealitySE;

public class LevelController : MonoBehaviour
{
    private void OnLevelCleared(int level, int deaths, float seconds)
    {
        Ravensight.Track("level_completed", new Dictionary<string, object>
        {
            { "level", level },
            { "deaths", deaths },
            { "seconds", seconds },
        });
    }
}
```

Prefer to configure it in code instead of the Inspector? Call this once, before
your first event:

```csharp
Ravensight.Initialize("gt_live_your_key");
```

## API

Everything game code needs is static on `Ravensight`:

| Call | What it does |
| --- | --- |
| `Ravensight.Track(name, data)` | Queues an event. `data` is optional. Returns false when tracking is off. |
| `Ravensight.Flush()` | Sends queued events now. Returns a `Task<RavensightFlushResult>`. |
| `Ravensight.SubmitFeedback(message, category, rating)` | Posts player feedback. Throws `RavensightException` on failure. |
| `Ravensight.FetchSuggestions()` | EXPERIMENTAL. AI generated design suggestions for your game. |
| `Ravensight.SetEnabled(bool)` | Local opt in and opt out for a privacy toggle. |
| `Ravensight.IsReady` | True once a session exists. |
| `Ravensight.Client` | The underlying `RavensightClient` for anything else. |

Event values can be strings, numbers, bools, nulls, nested dictionaries or
lists. They are serialized by the SDK's own JSON writer, because Unity's
`JsonUtility` cannot round trip a dictionary.

Feedback, awaited from an async method:

```csharp
try
{
    await Ravensight.SubmitFeedback("The boss fight drags", "balance", 3);
}
catch (RavensightException e)
{
    Debug.LogWarning("feedback rejected: " + e.Code);
}
```

## Inspector settings

| Field | Default | Meaning |
| --- | --- | --- |
| Api Url | `https://api.ravensight.io/api/v1` | Only change this if you self host. `/api/v1` is appended when omitted. |
| Ingest Key | none | Your publishable `gt_live_...` key. |
| Game Version | empty | Empty uses `Application.version`. |
| Auto Flush Seconds | `15` | `0` flushes only on demand. |
| Max Queue Size | `500` | Offline queue cap. Oldest events are dropped first. |
| Request Timeout Seconds | `15` | Per request timeout. |
| Track Lifecycle Events | on | Sends `game_started`, `game_paused`, `game_resumed`, `game_exited`. |
| Verbose Logging | off | Logs SDK activity to the console. |

## About your ingest key

The `gt_live_...` key is publishable. It is safe to ship inside a build: it can
only open sessions and read the tracking kill switch. It cannot read analytics,
read feedback or touch your account. Rotate it from the dashboard at any time.

## How delivery works

* **Batching.** Up to 50 events per request, the server hard limit. A flush
  keeps sending batches until the queue drains.
* **Offline queue.** Events accumulate up to Max Queue Size. Past that the
  oldest are dropped so the newest are always kept.
* **Never dropped on failure.** Events leave the queue only after the server
  answers `202`.
* **Session expiry.** A `401` clears the session, opens a new one and re sends
  the same batch.
* **Rate limits.** A `429` honors `Retry-After` in seconds. With no such header
  the SDK backs off exponentially from 10 seconds to a 5 minute ceiling. The
  queue is held, never discarded.
* **Oversize batches.** A `400` halves the batch and retries down to a single
  event. An event still rejected on its own is dropped so it cannot block
  everything behind it.
* **Kill switch.** `GET /settings` is read once at boot. If the server has
  tracking off for your game, the queue is cleared and nothing is sent.

Flushes happen on the timer, on `OnApplicationPause`, on focus loss and on
`OnApplicationQuit`. The quit flush is best effort: Unity has no synchronous
HTTP, so a process that exits immediately can lose the last batch. Anything not
delivered is gone, since the queue lives in memory only.

## Architecture

```
Runtime/
  Core/                         no UnityEngine dependency, enforced by asmdef
    RavensightClient.cs         sessions, batching, backoff, the whole protocol
    RavensightTransport.cs      IRavensightTransport plus request and response
    RavensightJson.cs           small JSON reader and writer
  Ravensight.cs                 MonoBehaviour singleton, PlayerPrefs, timers
  RavensightUnityTransport.cs   IRavensightTransport over UnityWebRequest
```

`Runtime/Core` is its own assembly with `noEngineReferences` set, so a stray
`using UnityEngine` in the protocol layer is a compile error rather than a
surprise. That is what makes the logic testable without the editor: hand
`RavensightClient` a fake `IRavensightTransport` and a fake clock and you can
drive every retry path in a plain unit test.

Compile check the engine free half from a terminal, if you have the .NET SDK or
Mono installed:

```bash
Tools/compile-check.sh
```

## Device id and privacy

The SDK generates a random device id on first run and stores it in
`PlayerPrefs` under `ravensight_device_id`. No hardware identifier, advertising
id, IP based fingerprint or personal data is collected by the SDK itself. Only
the events you choose to send leave the device.

`Ravensight.SetEnabled(false)` stops all sending and discards anything still
queued, so an opt out does not leave player data sitting in memory.

## WebGL

WebGL builds work, with two caveats:

* `PlayerPrefs` is backed by IndexedDB and some browsers clear it aggressively,
  so returning players may look new.
* `OnApplicationQuit` is unreliable in a browser tab. Flush at points you
  control, such as the end of a level, rather than relying on quit.

## License

MIT. Copyright 2026 Reality Software Entertainment.
