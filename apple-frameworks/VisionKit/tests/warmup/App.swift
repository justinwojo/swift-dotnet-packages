// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Pure-Swift mediaanalysisd warm-up for the VisionKit simulator lane. The first image analysis
// after a simulator boot waits for mediaanalysisd's first-run migration, whose duration no app
// budget can bound (0.04 s to several minutes observed). The harness runs this throwaway app once
// per simulator boot, before the managed VisionKit app, so the managed suite measures the binding
// on a warm daemon. It uses no .NET and no binding: a broken binding cannot hide behind a failed
// warm-up, and the warm-up cannot pass on the binding's behalf.

import UIKit
import VisionKit

private func log(_ line: String) {
    print("[VK-WARMUP] \(line)")
    fflush(stdout)
}

private func monotonicMs() -> Double { Double(clock_gettime_nsec_np(CLOCK_UPTIME_RAW)) / 1_000_000 }

private func makeImage() -> UIImage {
    let format = UIGraphicsImageRendererFormat()
    format.scale = 1
    return UIGraphicsImageRenderer(size: CGSize(width: 320, height: 120), format: format).image { context in
        UIColor.white.setFill()
        context.fill(CGRect(x: 0, y: 0, width: 320, height: 120))
        let attributes: [NSAttributedString.Key: Any] = [
            .font: UIFont.boldSystemFont(ofSize: 40),
            .foregroundColor: UIColor.black,
        ]
        ("HELLO 12345" as NSString).draw(at: CGPoint(x: 16, y: 34), withAttributes: attributes)
    }
}

@objc(VKWarmupSceneDelegate)
final class SceneDelegate: UIResponder, UIWindowSceneDelegate {
    var window: UIWindow?

    func scene(_ scene: UIScene, willConnectTo session: UISceneSession,
               options connectionOptions: UIScene.ConnectionOptions) {
        guard let windowScene = scene as? UIWindowScene else { return }
        let window = UIWindow(windowScene: windowScene)
        let controller = UIViewController()
        controller.view.backgroundColor = .white
        window.rootViewController = controller
        window.makeKeyAndVisible()
        self.window = window
    }
}

@main
final class AppDelegate: UIResponder, UIApplicationDelegate {
    private var heartbeat: DispatchSourceTimer?

    func application(
        _ application: UIApplication,
        didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]?
    ) -> Bool {
        let start = monotonicMs()
        log("start supported=\(ImageAnalyzer.isSupported)")
        guard ImageAnalyzer.isSupported else {
            // Nothing to warm: the managed suite reports the environment itself.
            log("READY schema=1 supported=false ms=0")
            log("TEST SUCCESS")
            DispatchQueue.main.async { exit(0) }
            return true
        }

        // A line every 10 s keeps the harness log readable while the daemon migrates.
        let timer = DispatchSource.makeTimerSource(queue: .main)
        timer.schedule(deadline: .now() + 10, repeating: 10)
        timer.setEventHandler { log("waiting ms=\(Int(monotonicMs() - start))") }
        timer.resume()
        heartbeat = timer

        let image = makeImage()
        Task { @MainActor in
            do {
                let analyzer = ImageAnalyzer()
                let configuration = ImageAnalyzer.Configuration([.text, .machineReadableCode])
                let analysis = try await analyzer.analyze(image, configuration: configuration)
                let elapsed = Int(monotonicMs() - start)
                let transcript = analysis.transcript.replacingOccurrences(of: "\n", with: " ")
                log("READY schema=1 supported=true outcome=analyzed ms=\(elapsed) transcript=\"\(transcript)\"")
            } catch {
                // VisionKit answered, so the daemon is past its migration. Whether the refusal is
                // legitimate is the managed suite's verdict, not the warm-up's.
                let detail = "\(error)".replacingOccurrences(of: "\n", with: " ")
                log("READY schema=1 supported=true outcome=refused ms=\(Int(monotonicMs() - start)) error=\"\(detail)\"")
            }
            log("TEST SUCCESS")
            self.heartbeat?.cancel()
            exit(0)
        }
        return true
    }
}
