## Unreleased

Follows the platform's SDK delivery, ads and attribution, exposure and device
contracts. The server releases for them ship first; see "Server
prerequisites".

### Changes for 0.4.0 games

* The queue is persisted by default in
  `Application.persistentDataPath/Ravensight/queue-<hash>.json`: version 2 of
  the delivery contract, atomic writes, 500 events, 89 days. Persist Queue in
  the Inspector turns it off.
* Event timestamps are unix milliseconds (were seconds). `TrackAt` still takes
  seconds; `TrackAtMs` is new.
* Retry waits are the backoff times a random factor between 0.5 and 1 (were
  plus or minus 20 percent). `Flush()`, background and quit cut a backoff
  short; a server `Retry-After` is never cut short and reports
  `Skipped = "retry_after"`.
* A `400` with `index` drops that event only; only `400 batch_too_large`
  halves the batch; any other `400` drops the request's events. A batch
  `403` keeps the events and stops sending until the next launch
  (`Skipped = "refused"`). One new session after a batch `401`, then the
  backoff (was two). `413` splits; `415` resends once uncompressed.
* Experiment tags follow the exposure contract: a fetch alone tags nothing;
  the first typed config read in a session sends one `rs_exposure` and tags
  that session's later events. A well formed `rs_exp_<key>` of the game's own
  is kept (it was discarded).
* The error queue rules every SDK shares hold in the persisted queue: the
  capture flag is stored with each event, so a restored capture still queues
  behind gameplay and is evicted first; `rs_trail` is never written to the
  queue file (a restored capture has none); a capture is written to the file
  at once; an opt-out and `ResetDeviceId` delete it. Captures are stamped in
  milliseconds like every event.
* Origin replay: events carry the session they were tracked under; a saved exit
  whose session expired is queued under its origin.
* gzip for batches of 1,024 bytes or more; request signing when the session
  answers with `signing`, secret in memory only.
* `EventBytes` measures as the server does: a typed error's message and stack
  count one byte a character.
* `TrackAd`, `TrackAdRevenue`, `TrackAdRevenueFromMediation`,
  `TrackAdMobPaidEvent`, `SetAttribution` (once per device) and
  `RavensightDelivery.MicrosFromDecimalString`, `MicrosFromRevenue`,
  `AdFormatFrom`.
* `device` on `POST /session`: `SystemInfo.deviceModel`, the version part of
  `SystemInfo.operatingSystem` and `SystemInfo.graphicsDeviceName`.

* The queue file is excluded from iCloud and device backups on iOS after
  every write; `RavensightConfig.QueueFileWritten` is the hook. The README
  gives the Android Auto Backup rules a game adds.
* A full queue never evicts a capture inside a batch in flight (0.4.0 could).

### Server prerequisites

* `rs_exposure` must be in `SDK_AUTOMATIC_EVENTS` before release, or a public
  key's allowlist refuses it.
* The Phase 2 delivery release and the Phase 5 `rs_ad_*` and `rs_attr_*`
  keys must be deployed first.

## 0.3.1 (held)

* Add session-start remote config, scoped ETag/offline cache, strict typed getters and config-ready notification.
* Tag new events with active experiment assignments; isolate consent and identity changes from stale responses.

# Changelog

All notable changes to this package are documented here.

## [0.4.0] - 2026-11-06

### Added

* **Automatic error capture, on by default.** Exceptions Unity logs become
  `error` events with `rs_source` `uncaught`; `Debug.LogError` and failed
  asserts become `log` at `warning`. Unhandled `AppDomain` exceptions and
  unobserved task exceptions are observed (never marked observed). Warnings
  and log lines only feed the optional trail. The hooks run only with a key,
  after consent, while the SDK is enabled and while the game's Errors switch
  (`errorCapture.enabled` on the settings or session answer, absent means
  on) is on. Boot errors before that answer are held, at most 5.
* Device limits per session: 30 captures, 3 per error, 10 trails, 6 a
  minute. Scrubbed on the device before queueing (emails, tokens, `gt_`
  keys, long digit-bearing base64 or hex runs that are not file paths, IPs with a digit, home paths, URL credentials, query
  and fragment; input cut to 4 times the field limit first). The SDK's own lines and stacks are never captured. Once the caps are spent an
  error costs no scrub or key work, and the buffer between the hooks and the
  main thread keeps a separate quota for errors so heavy logging cannot push
  them out.
* A home folder scrubs as the whole name, spaces included, and
  `C:\Documents and Settings\<name>` is a home folder, with either slash
  (scrub contract 5, vectors 6).
* The trail: the last 20 lines, sent with a captured error only when the
  game's `errorCapture.trail` is on.
* `RavensightConfig.ErrorCapture`, `RavensightOptions.ErrorCapture`, an
  **Error Capture** Inspector field, `Ravensight.SetErrorCapture(bool)` and
  `Ravensight.IsErrorCaptureActive`.
* Tests: `Tests/Editor/Core/ErrorCaptureTests.cs` (43 cases, including the shared `scrub-vectors.json` fixture).

### Changed

* Gameplay events are queued ahead of captured errors, and a full queue
  drops a captured error before any gameplay event.
* A `413` answer to a batch is handled like a `400`: split, then drop the
  one event the server refuses.

### Verified

* 162 .NET Core tests pass, and `Runtime/Core` compiles as C# 7.3 for
  netstandard2.0, both on the .NET 8 SDK that ships inside Unity 6000.6.5f1.
  All Runtime sources compile against the Unity 6000.6.5f1 engine assemblies
  with zero warnings.
* Not run: the Unity Editor (EditMode or PlayMode), a player build, or a
  real error against a local API. The hooks themselves are parse only.

## [0.3.0] - 2026-10-16

For games whose builds or source anyone can download. A game that sets
only its key and none of the new options behaves as on 0.2.1, except for
the changes listed first.

### Changes for 0.2.1 games

* A `401` or `403` to the startup settings check or to `POST /session`
  (other than `tracking_disabled` and a refused playtest token) now ends the
  run: the queue is cleared, nothing more is sent, and `IsActive` and the new
  `IsEnabled` are false. In 0.2.1 the events stayed queued and `Flush()`
  made one more attempt. The status reason is still `invalid_key`, logged
  once. A batch `401` still opens a new session first; a later batch `403`
  still only pauses the automatic flush.
* Retry waits carry plus or minus 20 percent jitter, never under half a
  second and never over 5 minutes. `Retry-After` is still obeyed exactly.
* Every event's data gains an `rs_event_id` key. The caller's dictionary is
  never changed: the SDK queues a copy.
* An event whose data is over 8,192 bytes as JSON is dropped before it is
  queued: `Track` returns false, `Stats.Dropped` counts it and one warning
  is logged for the run. In 0.2.1 it was sent and dropped on the server's
  `400`.
* `RavensightClient` no longer throws on an empty `IngestKey`; it starts
  inert instead.

### Added

- Screenshot Player Reports with session-bound retry identity and credential-free presigned multipart uploads.

* Declared the Unity Web Request module dependency so installation also compiles in minimal projects.

* **Inert without a key.** An empty Ingest Key (or `Ravensight.Initialize`
  with a null or empty key) reads and writes nothing in `PlayerPrefs`,
  builds no network transport, makes no request and runs no flush timer.
  The status reason is `no_key`. One warning, silenced by the new
  **Optional** field (`RavensightOptions.Optional`, `RavensightConfig.Optional`).
* **`Ravensight.IsEnabled`** and `RavensightClient.IsEnabled`, true only while
  the SDK is collecting. `Ravensight.DeviceId`.
* **Consent** (`RavensightConsent.OptOut`, the default, or `OptIn`), an
  Inspector field and a `RavensightOptions` and `RavensightConfig` member.
  With Opt In nothing is sent, no device id is made and tracked events are
  dropped until `SetTrackingEnabled(true)`. The yes is saved in
  `PlayerPrefs` as `ravensight_optin`; a no removes it and keeps the device
  id. Events tracked right after the yes are queued while the settings check
  runs.
* **Distribution**, an Inspector field and option: a label of at most 40
  characters, sent on `POST /session` as `distribution`. A label that is too
  long or has a control character is not sent and is logged once.
* **`rs_event_id`** on every event, including the automatic ones: the first
  16 characters of the device id, the start time in hex, an 8 hex character
  launch nonce from `System.Security.Cryptography.RandomNumberGenerator`, and
  a counter. A batch sent again carries the same ids.
* `Ravensight.Initialize(key, RavensightOptions)`.
* README: a "Public builds" section and a compatibility table.
* Tests: `Tests/Editor/Core/PublicBuildTests.cs` (28 cases), one more delivery case and two Play Mode
  cases, an inert component and an opt-in boot.

### Verified

* Unity 6000.6.5f1 in batch mode, the package as a local dependency of a
  fresh project: **87 EditMode tests and 6 PlayMode tests passing**, with no
  compiler warnings from the package.
* Not run for 0.3.0: the .NET suite and C# 7.3 compile check (no .NET SDK on
  the machine; CI runs them), a player build, a git URL install, and
  everything 0.2.1 already listed as not run.

### Typed events and performance sampling (included in this release)

### Added

* Typed event helpers, thin wrappers over `Track` that set the reserved `rs_*`
  properties the dashboard reads: `Ravensight.TrackPurchase`,
  `TrackResource`, `TrackProgression` and `TrackError`. Each takes an optional
  extra data map (the reserved keys win over it) and an optional event name.
  They return what `Track` returns, never throw on bad input, and use the same
  queue, batching, retries and opt out. The builders live in
  `RavensightTyped` in `Runtime/Core`, so they are tested without the Editor.
* Opt in performance sampling. Turn on the Performance field (or set
  `RavensightConfig.Performance`) and the component samples the unscaled frame
  time once a second in `Update`, keeping at most 600 samples. It sends one
  `health` event per session, with median and 10th percentile frames per
  second, memory in MB and boot time in milliseconds, when the app goes to the
  background (iOS and Android) or quits (desktop). Nothing is sent when no
  sample was taken, and it is off by default.
* The session request now carries `"sdk": "unity/<version>"`.

### Changed

* `SdkVersion` and the package version are 0.3.0.

## [0.2.1] - 2026-10-10

Verified in the Unity Editor. No change to the public API or to what is sent.

### Added

* Unity `.meta` files, generated by Unity 6000.6.5f1, so an install by git URL
  imports the package again. In 0.2.0 it imported nothing.
  If you copied 0.2.0 into your project instead, the component's GUID
  changes when you switch: scenes and prefabs show "Missing (Mono Script)"
  and the SDK stops starting. Keep your local `Runtime/Ravensight.cs.meta`
  GUID, or re-add the component and its Inspector fields after switching.
  See the README's "Upgrading from a copied 0.2.0".
* The Core test suite runs in the Unity Test Runner as well as on .NET. The
  sources moved from `Tests~/Core` to `Tests/Editor/Core`;
  `Tests/Editor/XunitShim.cs` maps their xUnit calls onto Unity's NUnit, and
  `Tests~/Core` keeps the .NET project, which compiles the same files.
* Play Mode tests in `Tests/Runtime` boot the `Ravensight` component itself:
  the startup requests leave from `Start`, in order, while it is active and
  enabled; nothing is sent with Send From Editor off; the same boot over
  `UnityWebRequest` against a local server; and a refused request ends as
  `offline`. Both test assemblies compile only for a project that lists the
  package in `testables`.

### Fixed

* A request Unity refuses to send (plain HTTP to a remote host in a project
  that disallows it, the default) hung the SDK: the transport called
  `SendWebRequest` outside its error handling, so the request never finished
  and the startup check waited forever. It now fails as a network error and
  the SDK reports `offline`.

### Verified

* Unity 6000.6.5f1 in batch mode: an Editor compile of both assemblies and the
  sample with no errors or warnings, player script compiles for macOS and
  WebGL, **58 EditMode tests and 4 PlayMode tests passing**, the same 58 on
  .NET in CI, a git URL install into a fresh project, and a macOS player run
  against a local server (boot, events, and `game_exited` on quit).
* Not run: iOS or Android (modules not installed), a device, a WebGL build in
  a browser, IL2CPP, Windows or Linux players, and any Unity version other
  than 6000.6.5f1, although `package.json` declares 2021.3. See the README's
  verification status.

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
