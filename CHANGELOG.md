# Changelog

All notable changes to this package are documented here.

## [0.1.0] - 2026-08-28

First public beta.

### Added

* `Ravensight` MonoBehaviour singleton with `DontDestroyOnLoad`, an Inspector
  configured ingest key, and static `Track`, `Flush`, `SubmitFeedback`,
  `FetchSuggestions` and `SetEnabled` helpers.
* `RavensightClient`, the complete protocol implementation with no UnityEngine
  dependency: sessions, batching at the 50 event server limit, the offline
  queue with a drop oldest cap, re authentication on 401, `Retry-After` on 429,
  exponential backoff from 10 seconds to 5 minutes, and oversize batch
  splitting on 400.
* `IRavensightTransport` with a `RavensightUnityTransport` implementation over
  `UnityWebRequest`.
* Server side kill switch honored at boot via `GET /settings`.
* Device id persisted in `PlayerPrefs`.
* Automatic flush on interval, `OnApplicationPause`, `OnApplicationFocus` and
  `OnApplicationQuit`.

### Known limitations

* Not yet verified inside the Unity Editor or in a player build. See the beta
  notice in the README.
* The final flush on quit is best effort. Unity has no synchronous HTTP, so a
  process that exits immediately can lose the last batch.
