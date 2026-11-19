# Held combined release, October 17, 2026

Public-build support and typed events have been combined locally. This record
is verification evidence, not a publication or deployment record.

Validated: 110 .NET core tests, 110 Unity EditMode tests, 6 Unity PlayMode tests.

The merge preserves public-key consent and inert-without-key behavior,
stable event IDs, typed event helpers and opt-in performance sampling.
JavaScript now defers performance listeners until consent; Android health
summaries carry the same deduplication IDs as other events.

Physical-device and live-service validation is not implied by these checks.
Publishing, tags, marketplace submission and deployments remain held pending
owner approval. Older README test counts describe their earlier branch runs;
this file records the combined candidate.

## 0.3.1 remote config and experiments

Validated locally: 116 .NET core tests passed (six remote-config regressions),
netstandard2.0 core compile passed with zero warnings/errors, and all runtime
sources including the Unity facade and transport compiled against the installed
Unity 6000.6.5f1 assemblies with zero warnings/errors.

The six tests cover automatic startup, strict getters and defensive copies,
queue-time exposure snapshots, scoped offline cache and exact ETags, consent
purge, identity/reset and overlapping request races, atomic malformed-response
rejection, size/depth/count bounds, authorization refusal and opt-in inactivity.

A new Unity EditMode run was attempted but stopped after Unity's licensing
client initialization timed out; it did not produce a test result. The earlier
110 EditMode / 6 PlayMode results above do not validate this new change. Real
service/device tests and a fresh Unity Test Runner run remain outstanding.
No push, tag, publication or deployment performed.

Config identifier compatibility audit: 7 focused .NET config tests passed; no production validator change was needed.
Config property keys reject constructor/prototype, including nested JSON properties.
Experiment keys and variant labels may use those words as string values, matching
the server contract. Regression coverage distinguishes those cases. Publishing remains held.

Screenshot Player Reports: 119 .NET core tests passed. All Runtime C# sources, including the facade and UnityWebRequest transport, compile against installed Unity 6000.6.5f1 assemblies with C# 7.3, zero warnings/errors. Unity EditMode execution attempted but blocked by local license initialization timeout; this is not an Editor execution pass. No live storage provider or physical-device screenshot run was performed. Existing held 0.3.1 candidate now includes this feature; no tag, push or publication occurred.

## 0.6.0: identify players who sign in (November 19, 2026)

- Branch `feat/identify`, built to the platform's identify contract version 2.
- `Tools/test.sh` (.NET, engine free): **234 passed**, 0 failed (199 before).
  `Tools/compile-check.sh`: Runtime/Core compiles as C# 7.3 for netstandard2.0.
  All Runtime sources, the MonoBehaviour layer included, compile against the
  Unity 6000.6.5f1 engine assemblies.
- Not run in the Unity Editor or its Test Runner, and not on a device. The new
  `.meta` files were written by hand with fresh GUIDs, not by the Editor.
- No live Ravensight service was used. Nothing was published, tagged or deployed.

### Campaign links (same 0.6.0)

- Branch `feat/campaign-link`, on `feat/identify` at a69745a, built to the
  campaign link contract (the JS SDK is the reference).
- `Tools/test.sh` (.NET, engine free): **301 passed**, 0 failed, 64 of them
  campaign link cases (`Tests/Editor/Core/CampaignLinkTests.cs`): the
  `rs_link` parsing table, validation, once per session, kept after a failed
  or token-less answer, opt in, opt out, disabled, no key, a playtest run
  and a joined session.
- `Tools/compile-check.sh`: Runtime/Core compiles as C# 7.3 for
  netstandard2.0, 0 warnings. All Runtime sources compile against the Unity
  6000.6.5f1 Editor engine assemblies, and again against its WebGL player
  assemblies with `UNITY_WEBGL` defined, so the jslib import compiles; 0
  warnings either way.
- `Plugins/WebGL/RavensightLandingSearch.jslib` was loaded on a host with
  stand-ins for the Emscripten helpers and returned `location.search`
  unchanged (the `#` part excluded), and an empty string with no `window` or
  a throwing `location`. It has not run in a WebGL player or a browser.
- Not run in the Unity Editor or its Test Runner, and not on a device. The new
  `.meta` files were written by hand with fresh GUIDs, not by the Editor.
