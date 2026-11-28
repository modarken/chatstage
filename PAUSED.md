# Release work checkpoint

Paused at the owner's request before PR completion, merge, tagging or publication.
Branch: `feat/headless-layout`; related issue: #2. Intended version: `0.3.0-preview.1`.

## Implemented so far

- Renderless `ChatStageSession` / `ChatStageSessionContext`, reusable flat transcript,
  message and composer, templates and CSS parts, focus/Stop/new-thread controls,
  draft binding and cancellable prompt/attachment interception.
- Opt-in `ChatStageChat.Unstyled`, public templates, class/style/attribute pass-through,
  options and default chat compatibility. Custom WPF layout sample.
- Bit scheduler coalesces layout invalidations; default chat skips text-only root
  renders. `ChatStageOptions.RenderInterval` and `UiOptions` added.
- Final-text custom-event reconciliation and explicit `ReplaceMessageText`.
- `AgUiTransport.RestoreApprovalsAsync` reconstructs standard cards locally, waits
  for answers, and resumes using only recorded interrupt/call IDs. No HTTP before an answer.
- AG-UI user content now includes URL/inline attachments.
- Focus-free HTML rendering, controller, scheduler and consumer regression tests.
- In-process transparent composition / Blazor Server comparison probe, using only
  the supported `BlazorWebView.UseCompositionControl` API.

## Verification and measurements

Last full Release build passed; Blazor tests **38/38** and integration tests **26/26**
passed separately. Subsequent small composer binding, stylesheet, solution and
new-conversation edits still need a final build/test pass. `git diff --check` passed.
Terminal RUN_ERROR regression leaves both session and conversation non-generating;
the reported stuck flag was not reproduced with the pinned dependencies.

Probe: Release, Windows/.NET 11, 640x480 transparent composition window positioned
off-screen, never activated; one warm-up second followed by five seconds streaming
at a requested 2 ms/delta, no pacing, three sequential runs each. CPU is percent of
one core; memory is private bytes, not working set. Browser values include all
processes returned by that probe's WebView2 environment. These are local synthetic
measurements, not production latency/CPU guarantees; off-screen rendering is a limit.

| Scenario (median) | Root renders | .NET CPU | .NET + WebView2 CPU | Total private MiB |
|---|---:|---:|---:|---:|
| Original 0.2 wrapper, thread notification per delta | 1952 | 75.52% | 111.23% | 388.8 |
| New wrapper, thread notification per delta | 101 | 49.92% | 68.70% | 373.9 |
| New wrapper, text only, in-process composition | 4 | 40.30% | 55.16% | 345.2 |
| New wrapper, text only, Blazor Server composition | 7 | 41.22% | 55.77% | 362.1 |

The original wrapper already avoids root invalidation for ordinary text deltas;
the coalescing comparison deliberately exercises thread-level Changed notifications.
The hybrid/server CPU difference is within noise; memory is lower in this probe.
`LegacyChat.razor` preserves the old wrapper for reproducibility, without internal
registry access. Run one command at a time:

```sh
dotnet run --project samples/ChatStage.Samples.OverlayProbe -c Release -- hybrid before thread
dotnet run --project samples/ChatStage.Samples.OverlayProbe -c Release -- hybrid after thread
dotnet run --project samples/ChatStage.Samples.OverlayProbe -c Release -- hybrid after text
dotnet run --project samples/ChatStage.Samples.OverlayProbe -c Release -- server after text
```

## Remaining before release

1. Review and finish the API/lifecycle work; add explicit thread-switch/new-thread,
   asynchronous interception/draft-binding, attachments-on-wire, restored approval
   cancellation/multiple approvals, and unchanged default-layout coverage.
2. Review the context's accepted-submission detection (currently assumes synchronous
   insertion), streaming interval changes and disposal/unsubscription behavior.
   Consider thread-qualified approval waiter keys for simultaneous sessions.
3. README/API styling tables, custom layout, final-text event payload example,
   restored approvals, UiOptions/image policy, and overlay instructions are unfinished.
   Bit's image allow-list is host-based and permits click-to-load for other hosts;
   strict same-origin loading also requires host CSP `img-src 'self'`.
4. Document `/ui/` hosting with `<base href="/ui/">` and normal static-asset paths;
   `StaticWebAssetBasePath` alters SDK-generated scoped-CSS relative imports. The new
   explicit `_content/ChatStage.Blazor/chatstage.css` includes the default layout as
   a fallback, but the scoped-CSS/sub-path scenario still needs verification.
   Include CSP `script-src 'wasm-unsafe-eval'` and `base-uri 'self'` with other required sources.
5. Overlay decision agreed with coordinator: use standard BlazorWebView composition,
   no custom WebViewManager implementation. Pinned WPF 11.0.10 exposes composition
   and initialization profile/environment options, **no existing environment instance
   parameter**. Document this limit; do not claim arbitrary environment injection.
6. Finish README and CHANGELOG, review the measured numbers in the PR, run the final
   builds/tests sequentially, get green CI, then merge only after work is resumed.
   Tag `v0.3.0-preview.1`, watch Release to completion and verify both nuget.org packages.

No release or tag has been created. Do not resume automatically.
