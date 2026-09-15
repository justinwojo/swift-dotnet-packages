// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import Lottie

private let b11GradientKeypath =
    AnimationKeypath(keypath: "B11 Gradient Layer.B11 Shape Group.B11 Unique Gradient.Colors")

/// Installs the native control arm on a view created by the managed test. The view,
/// fixture, frame and renderer therefore stay identical across the differential; only
/// the closure's language/runtime boundary changes.
@_cdecl("SBLottieB11InstallPureSwiftDynamicGradient")
public func installB11PureSwiftDynamicGradient(_ opaqueView: UnsafeMutableRawPointer?) -> Int32 {
    precondition(Thread.isMainThread, "B11 Lottie differential must run on the main thread")
    guard let opaqueView else { return -1 }
    return MainActor.assumeIsolated {
        let view = Unmanaged<LottieAnimationView>.fromOpaque(opaqueView).takeUnretainedValue()
        var calls: Int32 = 0
        let provider = GradientValueProvider(block: { _ in
            calls += 1
            return [
                LottieColor(r: 0, g: 1, b: 0, a: 1),
                LottieColor(r: 1, g: 1, b: 0, a: 1),
            ]
        })
        view.setValueProvider(provider, keypath: b11GradientKeypath)
        view.forceDisplayUpdate()
        return calls
    }
}
