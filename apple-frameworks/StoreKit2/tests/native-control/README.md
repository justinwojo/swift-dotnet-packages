# StoreKit Sandbox native control

This pure-Swift control is the fail-closed readiness prerequisite for the command-line iOS and
tvOS StoreKit2 lanes. The harness builds it with the managed app's exact bundle id, installs it on
the exact simulator the managed app will use, and requires one fresh run-token receipt before the
managed app is installed over it.

The receipt is emitted only after all four operations complete inside 60 seconds:

- verified `AppTransaction.shared`, matching the app bundle and reporting Sandbox;
- `Product.products(for:)`, returning the exact configured product;
- finite `Transaction.currentEntitlements`, with every yielded transaction verified as Sandbox;
- finite `Transaction.all`, with every yielded transaction verified as Sandbox.

Empty transaction sequences are valid and are recorded as zero. They prove bounded StoreKit
readiness, not transaction-payload coverage. A timeout, production/Xcode environment, unverified
transaction, missing product, wrong bundle, stale token, or missing receipt is a non-pass and stops
the managed launch.

Manual use:

```text
STOREKIT_SANDBOX_PRODUCT_ID=com.example.app.product ./run.sh ios [iOS-simulator-UDID]
STOREKIT_SANDBOX_PRODUCT_ID=com.example.app.product ./run.sh tvos [tvOS-simulator-UDID]
```

The manual runner applies a 75-second outer launch deadline, terminates the control on every exit,
validates the fresh receipt, and retains its log and SHA-256 summary under
`artifacts/storekit-sandbox-readiness/`. Product identifiers are validated with the same syntax and
placeholder exclusions as the Nuke preflight, so a `.storekit` path is rejected as configuration.

Prerequisites are external: configure that exact product in App Store Connect for
`com.swiftbindings.storekit2.tests`, and sign the target simulator into a Sandbox tester account.
There is no public API to inspect or inject that login. The live four-operation receipt therefore
establishes observed Sandbox readiness for this run; the product id alone does not.

A `.storekit` file is intentionally unsupported here. Xcode activates StoreKit configuration state
when it launches a scheme; passing a path through `simctl`, `mlaunch`, `dotnet`, or this script does
not reproduce that state. An Xcode-driven control may be maintained separately, but it cannot
substitute for this shipping command-line lane.
