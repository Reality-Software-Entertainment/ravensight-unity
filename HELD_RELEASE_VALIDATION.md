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
