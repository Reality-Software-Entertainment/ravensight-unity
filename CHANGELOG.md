# Changelog

All notable changes to this package are documented here.

## [0.2.0] - 2026-10-10

Brings the Unity SDK to the contract every Ravensight SDK now follows. Pre-1.0,
so this minor version contains breaking changes.

### Breaking

* `game_paused` and `game_resumed` are no longer sent by default. Turn on the
  new **Track Pause Events** Inspector field to keep them. The automatic events
  are now `game_started` and `game_exited` only.
* The player's privacy choice is `Ravensight.SetTrackingEnabled(bool)`, saved in
  `PlayerPrefs` (`ravensight_tracking_enabled`) and read on every launch before
  anything is sent. `SetEnabled` keeps working as the developer's runtime
  switch and is not saved; move a player toggle to `SetTrackingEnabled`.
* Editor play mode no longer sends anything unless **Send From Editor** is on.
* The default Api Url is now the bare host `https://api.ravensight.io`. Any
  saved value still works: whitespace, trailing slashes and a trailing
  `/api/v1` are stripped, then `/api/v1` is appended.
* `SubmitFeedback` checks the category and rating locally and throws
  `invalid_category` or `invalid_rating` with no request; while tracking is off
  it throws `disabled` (was `tracking_disabled`).
* `FetchSuggestions` returns an empty list with no request while tracking is
  off, and when the component has no client, instead of throwing.
* `RavensightClient.FlushAsync()` is now the explicit flush; the timer uses
  the new `AutoFlushAsync()`. `FlushAsync(true)` no longer skips a server's
  `Retry-After`.

### Added

* `enabledOnStart` Inspector field and `Initialize(key, enabled)` overloads. A
  disabled client makes no request of any kind; enabling it later runs the
  startup check then.
* `Ravensight.StatusReason`, `Ravensight.StatusChanged` and
  `Ravensight.LastInitResult`, with the shared reason values `disabled`,
  `invalid_key`, `tracking_disabled`, `offline`, `rate_limited`,
  `session_failed` and `playtest_job_closed`.
* `Ravensight.ResetDeviceId()`: a new random id, saved, with the queue cleared
  and the session dropped.
* `Ravensight.IsTrackingEnabled` for a settings toggle's initial state.
* Playtest support: `RAVENSIGHT_PLAYTEST_TOKEN` and `RAVENSIGHT_PLAYTEST_RUN_ID`,
  the `--ravensight-playtest-token=` and `--ravensight-playtest-run-id=`
  arguments, and `Ravensight.SetPlaytestContext` for mobile. A run uses the
  device id `pt-<runId>` in memory, sends `X-Ravensight-Playtest` on every
  request, writes nothing to `PlayerPrefs`, and stops on
  `403 playtest_job_closed`. `Ravensight.IsPlaytest`.
* iOS and Android background rule: a marker on background, a new session after
  30 minutes away (`sessionTimeoutSeconds`), and the exit of an app killed in
  the background reported on the next launch.
* Desktop quits are held for at most 1.5 seconds while the last batch, with
  `game_exited`, is sent.
* `Ravensight.SessionToken`, and `RavensightConfig.JoinToken` for a headless
  server build, sent as `joinToken` with a fallback to a plain session on
  `401 invalid_join_token`.
* `Track` calls made before the component wakes are held (up to 500) and
  queued once the player's saved choice has been read.
* `IRavensightStorage`, `RavensightMemoryStorage` and
  `RavensightPlayerPrefsStorage`; `RavensightClient.FeedbackCategories`;
  `RavensightClient.SdkVersion`.
* An engine free xUnit suite in `Tests~/Core`, `Tools/test.sh`, and a CI
  workflow that runs it with the compile check (58 tests passing in CI). The
  Unity layer is still not compiled or run in the Unity Editor: see the
  README's verification status.
* README sections on automatic events, startup outcome, levels and deaths
  (`rs_kind`), telling players, playtest, game servers and running the tests.

### Fixed

* An opted-out or disabled player no longer sends `GET /settings` or
  `POST /session` at launch, and `SetEnabled` called before the component
  woke is no longer lost.
* A refused key (`401`, or a `403` that is not the kill switch or a playtest
  refusal) stops automatic sending and logs one warning, instead of retrying
  for the whole run. A `/settings` `401` no longer assumes tracking is on.
* `403 tracking_disabled` from any request is the kill switch.
* `game_started` is queued at the real start time, first in the queue, instead
  of after the network round trip; `game_exited` is no longer cleared from the
  queue by `OnDestroy` before it is sent.
* A `503` with `Retry-After` waits that long; a third `401` in a row on a batch
  backs off instead of stopping silently.
* `Ravensight.Initialize` configured the component after `Awake` had already
  run, so it always warned about a missing key. It now configures it first.
* Device ids come from the platform's secure random generator. Saved ids are
  never rewritten.
* The README and sample feedback examples used categories the server rejects;
  they now use `suggestion`.
* The startup requests now go out from `Start` rather than `Awake`, where the
  transport's runner was not yet enabled and every boot request failed at once.
  The transport now only needs the runner's GameObject to be active.
* The desktop quit hold waits for the queue to empty (or 1.5 seconds), so a
  timer flush already in flight no longer ends the hold early.
* The Editor hold also applies when `Ravensight.Client.Enabled` is set directly.
* `Ravensight.SubmitFeedback` before the component exists returns a failed task
  (`disabled`) instead of throwing synchronously.

### Known gap

* The package ships without Unity `.meta` files, so an install by git URL or
  from a registry imports nothing. Before 0.2.0 is announced, the owner must
  open the package once in a Unity Editor and commit the generated `.meta`
  files.

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
