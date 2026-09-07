# StoreKit native control

A pure-Swift iOS app (no .NET, no binding) that issues the same StoreKit calls the C# test suite
times out on — `AppTransaction.shared`, `Transaction.currentEntitlements`, `Transaction.all`, plus
`Product.products(for:)` — through the same `simctl install` / `simctl launch --console` path the
validators use. It exists to answer one question whenever the StoreKit2 suite hangs on those calls:
*is the hang ours, or is it StoreKit's behaviour in this environment?*

```
./run.sh                 # first booted simulator
./run.sh <UDID>          # a specific one
```

## Reference result (2026-09-06, iPhone 17 Pro Max simulator, iOS 26.3 runtime, no Sandbox account, no `.storekit` config)

```
[SK-CONTROL]    0.5s Product.products(for:) resolved: 0 product(s)
[SK-CONTROL]  150.0s verdict: AppTransaction.shared -> NO RESULT after 150s (native call never completed)
[SK-CONTROL]  150.0s verdict: Transaction.currentEntitlements -> NO RESULT after 150s (native call never completed)
[SK-CONTROL]  150.0s verdict: Transaction.all -> NO RESULT after 150s (native call never completed)
[SK-CONTROL]  150.0s verdict: Product.products(for:) -> 0 product(s)
```

The native calls produced no result within this 150-second observation window; the log phrase
"never completed" refers only to that window. This matches the C# suite's three 120-second
timeouts and empty product list on the recorded host. It does not establish indefinite hangs on
all unconfigured hosts, native cleanup after managed cancellation, or successful product lookup
and purchase flows with a configured store backend. The
daemon side agrees: `storekitd`'s log shows it accepting `appTransactionForClient:ignoreCache:reply:`,
`enumerateCurrentReceiptsForClient:…` and `enumerateReceiptsForClient:…` from both the .NET app and
this control, with no reply observed during the recorded window.

```
xcrun simctl spawn <UDID> log show --last 10m --predicate 'process == "storekitd"' --style compact \
  | grep -E 'Accepting new connection|StoreKitServiceConnection|Initialized with server'
```

Look for `Initialized with server Sandbox bundle ID …` — the daemon has fallen back to Sandbox with
no account to serve, which is the environment these hangs belong to. A different result from this
control (calls completing, or the daemon replying) while the C# suite still hangs would point back
at the binding.
