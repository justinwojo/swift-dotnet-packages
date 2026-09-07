// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Native control for the StoreKit2 test-app hangs. The C# suite's AppTransaction.shared,
// Transaction.currentEntitlements and Transaction.all probes time out in a simulator launched
// outside Xcode with no Sandbox account; this pure-Swift app issues the same StoreKit calls with
// zero .NET involvement, installed and launched through the same `simctl install` /
// `simctl launch --console` path, so the two outcomes can be compared directly.
//
// Deliberately minimal machinery: every probe is its own detached task, progress is reported by a
// plain GCD timer on the main queue (independent of the Swift concurrency runtime), and the app
// exits itself after the deadline so a hang cannot be confused with a harness stall.
import UIKit
import StoreKit

final class Board: @unchecked Sendable {
    private let lock = NSLock()
    private var results: [String: String] = [:]
    let names: [String]
    init(names: [String]) { self.names = names }
    func set(_ name: String, _ value: String) { lock.lock(); results[name] = value; lock.unlock() }
    func snapshot() -> [String: String] { lock.lock(); defer { lock.unlock() }; return results }
}

let started = Date()
let deadlineSeconds: Double = 150
let board = Board(names: ["AppTransaction.shared", "Transaction.currentEntitlements", "Transaction.all", "Product.products(for:)"])

func log(_ line: String) {
    let t = String(format: "%6.1fs", Date().timeIntervalSince(started))
    print("[SK-CONTROL] \(t) \(line)")
    fflush(stdout)
}

@main
final class AppDelegate: UIResponder, UIApplicationDelegate {
    var window: UIWindow?
    var heartbeat: DispatchSourceTimer?

    func application(_ application: UIApplication,
                     didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]?) -> Bool {
        window = UIWindow(frame: UIScreen.main.bounds)
        window?.rootViewController = UIViewController()
        window?.makeKeyAndVisible()
        log("bundle=\(Bundle.main.bundleIdentifier ?? "nil") state=\(application.applicationState.rawValue) start")

        startProbes()

        let timer = DispatchSource.makeTimerSource(queue: .main)
        timer.schedule(deadline: .now() + 10, repeating: 10)
        timer.setEventHandler {
            let snap = board.snapshot()
            let pending = board.names.filter { snap[$0] == nil }
            log("heartbeat: \(snap.count)/\(board.names.count) probes resolved; pending=\(pending)")
            if pending.isEmpty {
                log("done"); exit(0)
            }
        }
        timer.resume()
        heartbeat = timer

        DispatchQueue.main.asyncAfter(deadline: .now() + deadlineSeconds) {
            let snap = board.snapshot()
            for name in board.names {
                log("verdict: \(name) -> \(snap[name] ?? "NO RESULT after \(Int(deadlineSeconds))s (native call never completed)")")
            }
            log("done"); exit(0)
        }
        return true
    }

    func startProbes() {
        Task.detached {
            do {
                let result = try await AppTransaction.shared
                switch result {
                case .verified(let t): board.set("AppTransaction.shared", "verified bundleID=\(t.bundleID)")
                case .unverified(let t, let e): board.set("AppTransaction.shared", "unverified bundleID=\(t.bundleID) error=\(e)")
                }
            } catch {
                board.set("AppTransaction.shared", "threw \(error)")
            }
            log("AppTransaction.shared resolved: \(board.snapshot()["AppTransaction.shared"]!)")
        }
        Task.detached {
            var n = 0
            for await _ in Transaction.currentEntitlements { n += 1; if n > 64 { break } }
            board.set("Transaction.currentEntitlements", "sequence terminated after \(n) element(s)")
            log("Transaction.currentEntitlements resolved: \(n) element(s)")
        }
        Task.detached {
            var n = 0
            for await _ in Transaction.all { n += 1; if n > 64 { break } }
            board.set("Transaction.all", "sequence terminated after \(n) element(s)")
            log("Transaction.all resolved: \(n) element(s)")
        }
        Task.detached {
            do {
                let products = try await Product.products(for: ["com.swiftbindings.storekit2.control.nonexistent"])
                board.set("Product.products(for:)", "\(products.count) product(s)")
            } catch {
                board.set("Product.products(for:)", "threw \(error)")
            }
            log("Product.products(for:) resolved: \(board.snapshot()["Product.products(for:)"]!)")
        }
    }
}
