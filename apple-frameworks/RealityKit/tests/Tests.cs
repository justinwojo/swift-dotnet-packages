// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Numerics;
using RealityFoundation;
using RealityKit;
using Swift.Runtime;
// Disambiguate against UIKit-provided CoreGraphics.CGRect (implicit using).
// ARView's @_cdecl ctor wrapper takes the Swift-marshalled CGRect.
using CGRect = Swift.CGRect;

namespace SwiftBindings.RealityKit.Tests;

internal static class Tests
{
    public static int Run()
    {
        int passed = 0, failed = 0, skipped = 0;

        void Pass(string name)
        {
            passed++;
            Log($"PASS: {name}");
        }

        void Fail(string name, string error)
        {
            failed++;
            Log($"FAIL: {name} — {error}");
        }

        void Skip(string name, string reason)
        {
            skipped++;
            Log($"SKIP: {name} — {reason}");
        }

        void MetadataTest<T>(string name) where T : ISwiftObject
        {
            try
            {
                var md = SwiftObjectHelper<T>.GetTypeMetadata();
                if (md.Handle == IntPtr.Zero)
                    throw new InvalidOperationException("metadata handle is null");
                Pass($"{name} metadata");
            }
            catch (Exception ex) { Fail($"{name} metadata", ex.Message); }
        }

        // Type-metadata smokes — RealityKit's own surface. The runtime
        // cross-module edge into RealityFoundation is exercised below by
        // ARView.Scene / Environment / CameraTransform reads, which return
        // RealityFoundation types through RealityKit @_cdecl wrappers.
        MetadataTest<ARView>("ARView");
        MetadataTest<ARView.EnvironmentType>("ARView.EnvironmentType");
        MetadataTest<ARView.DebugOptionsType>("ARView.DebugOptionsType");
        MetadataTest<ARView.RenderOptionsType>("ARView.RenderOptionsType");
        MetadataTest<EntityTranslationGestureRecognizer>("EntityTranslationGestureRecognizer");

        // Plain-enum case values — pin Swift case order. ARView.CameraModeType
        // is iOS 13.0+ so safely under the deployment floor.
        try
        {
            if ((int)ARView.CameraModeType.Ar != 0)
                throw new InvalidOperationException($"Ar expected 0, got {(int)ARView.CameraModeType.Ar}");
            if ((int)ARView.CameraModeType.NonAR != 1)
                throw new InvalidOperationException($"NonAR expected 1, got {(int)ARView.CameraModeType.NonAR}");
            Pass("ARView.CameraModeType case values");
        }
        catch (Exception ex) { Fail("ARView.CameraModeType case values", ex.Message); }

        // ARView lifecycle. The class is a UIView subclass; constructing it on
        // a simulator does not start an ARSession (that requires a back camera),
        // but the view itself, its Scene, and the Environment value-projection
        // are all expected to be reachable. We use NonAR so the simulator path
        // does not need to instantiate an AR session under the hood.
        ARView? arView = null;
        try
        {
            var bounds = new CGRect(0, 0, 320, 240);
            arView = new ARView(bounds, ARView.CameraModeType.NonAR, automaticallyConfigureSession: false);
            if (arView is null)
                throw new InvalidOperationException("ARView ctor returned null");
            Pass("ARView(CGRect, .NonAR, false) constructor");
        }
        catch (Exception ex) { Fail("ARView(CGRect, .NonAR, false) constructor", ex.Message); }

        if (arView is not null)
        {
            // Scene access — RealityFoundation.Scene navigated through the
            // RealityKit ARView. Validates the cross-module class projection.
            try
            {
                var scene = arView.Scene;
                if (scene is null)
                    throw new InvalidOperationException("ARView.Scene returned null");
                _ = scene.Name; // Read a string property to round-trip the Utf8Slice.
                Pass("ARView.Scene access");
            }
            catch (Exception ex) { Fail("ARView.Scene access", ex.Message); }

            // Environment.SceneUnderstanding — this is the property whose
            // wrapper required the upstream `let obj` -> `var obj` mutating-
            // getter fix (Session 1). Reading it end-to-end pins that fix
            // against future regressions.
            try
            {
                using var env = arView.Environment;
                using var su = env.SceneUnderstanding;
                Pass("ARView.Environment.SceneUnderstanding read");
            }
            catch (Exception ex) { Fail("ARView.Environment.SceneUnderstanding read", ex.Message); }

            try
            {
                using var env = arView.Environment;
                using var bg = env.Background;
                Pass("ARView.Environment.Background read");
            }
            catch (Exception ex) { Fail("ARView.Environment.Background read", ex.Message); }

            // CameraTransform — Transform value-projection through ARView, as
            // distinct from a fresh Transform() built locally. Reads exercise
            // the SIMD-typed by-value getters under real scene state.
            try
            {
                using var camera = arView.CameraTransform;
                _ = camera.Translation;
                _ = camera.Rotation;
                _ = camera.Scale;
                Pass("ARView.CameraTransform read");
            }
            catch (Exception ex) { Fail("ARView.CameraTransform read", ex.Message); }

            try
            {
                using var debug = arView.DebugOptions;
                Pass("ARView.DebugOptions read");
            }
            catch (Exception ex) { Fail("ARView.DebugOptions read", ex.Message); }

            try
            {
                using var ro = arView.RenderOptions;
                Pass("ARView.RenderOptions read");
            }
            catch (Exception ex) { Fail("ARView.RenderOptions read", ex.Message); }

            // Cross-module runtime traversal: walk the Scene → AnchorCollection
            // chain on a RealityFoundation.Scene reached through a
            // RealityKit.ARView, and construct a RealityFoundation.AnchorEntity
            // that takes a RealityFoundation.Entity child. The test app
            // explicitly references both csprojs, so this validates the
            // cross-module type identity at runtime — not transitive package
            // resolution.
            try
            {
                var scene = arView.Scene;
                using var anchors = scene.Anchors;
                int initialCount = anchors.EndIndex - anchors.StartIndex;
                if (initialCount < 0)
                    throw new InvalidOperationException($"Scene.Anchors count negative: {initialCount}");

                var anchor = new AnchorEntity();
                anchor.Name = "anchor1";
                var entity = new Entity();
                entity.Name = "child";
                anchor.AddChild(entity, preservingWorldTransform: false);
                if (anchor.Name != "anchor1")
                    throw new InvalidOperationException($"AnchorEntity.Name round-trip mismatch: '{anchor.Name}'");
                Pass("Scene.Anchors traversal + AnchorEntity construction");
            }
            catch (Exception ex) { Fail("Scene.Anchors traversal + AnchorEntity construction", ex.Message); }

            // §1 (apple-framework-gaps/05-residual-gaps.md): live end-to-end counterpart of
            // the RealityFoundation boxing test — a real RealityFoundation.Scene vended by
            // the ARView. Scene.AddAnchor / RemoveAnchor box their AnchorEntity argument into
            // the IHasAnchoring existential before the @_cdecl call. This previously threw
            // because AnchorEntity's cross-module (@_originallyDefinedIn RealityKit)
            // IHasAnchoring conformance descriptor was dropped, leaving
            // _protocolConformanceSymbols empty. With the descriptor recovered and emitted,
            // the box resolves and both calls complete. We don't assert the live anchor count
            // (a NonAR sim scene registers anchors on its own update cycle, not synchronously),
            // so the meaningful assertion is that the existential round-trip does not throw.
            try
            {
                var scene = arView.Scene;
                using var anchor = new AnchorEntity();
                anchor.Name = "roundtrip-anchor";
                scene.AddAnchor(anchor);
                scene.RemoveAnchor(anchor);
                Pass("Scene.AddAnchor/RemoveAnchor(IHasAnchoring) round-trip");
            }
            catch (Exception ex) { Fail("Scene.AddAnchor/RemoveAnchor(IHasAnchoring) round-trip", ex.Message); }

            // Project an arbitrary world point. The result is meaningful only
            // when an AR session is running; we just exercise the cdecl
            // entrypoint and the optional CGPoint marshal-back. A null return
            // is a valid outcome (nothing to project against in a NonAR sim).
            try
            {
                _ = arView.Project(new Vector3(0f, 0f, -1f));
                Pass("ARView.Project(Vector3) cdecl");
            }
            catch (Exception ex) { Fail("ARView.Project(Vector3) cdecl", ex.Message); }
        }

        // Constructor tail — exercises the ARView base ctor without the
        // CameraMode / automaticallyConfigureSession overload. This matches
        // the convention in CLAUDE.md to put constructors last; if the @_cdecl
        // wrapper for the simpler init is missing, all tests above still ran.
        try
        {
            using (new ARView(new CGRect(0, 0, 100, 100)))
            {
                // No assertion beyond "ctor did not throw".
            }
            Pass("ARView(CGRect) constructor");
        }
        catch (Exception ex) { Fail("ARView(CGRect) constructor", ex.Message); }

        // §5b (apple-framework-gaps/05-residual-gaps.md): RealityFoundation protocols
        // whose class-superclass constraint (`<Self : RealityKit.Entity>`) is recorded
        // only in the protocol's genericSig — not in InheritedProtocols — were
        // mis-classified, so the EveryEntityProtocol existential carrier and the
        // HasCollisionProxy were never emitted. Every gesture-recognizer `.entity`
        // getter (and ARView.InstallGestures' entity parameter) collapsed to a throw
        // stub: `Protocol proxy not available: EveryProtocol conformance was not emitted.`
        // With genericSig superclass detection fixed, HasCollisionProxy is emitted and
        // the gesture entity round-trips both directions:
        //   forward  (C#→Swift): InstallGestures boxes a ModelEntity into the
        //                        RealityFoundation.IHasCollision existential.
        //   backward (Swift→C#): the installed recognizer's .Entity getter materializes
        //                        a HasCollisionProxy from the Swift existential container.
        // Both paths threw unconditionally before the fix, so reaching them without an
        // exception is itself the proof; we additionally assert the forward call vends
        // recognizers and the backward read completes.
        if (arView is not null)
        {
            try
            {
                using var model = new ModelEntity(); // RealityFoundation.IHasCollision conformer
                var recognizers = arView.InstallGestures(ARView.EntityGestures.Translation, model);
                if (recognizers is null)
                    throw new InvalidOperationException("InstallGestures returned null");
                Pass("ARView.InstallGestures(IHasCollision) existential boxing");

                if (recognizers.Count == 0)
                    throw new InvalidOperationException(
                        ".translation gestures produced no recognizers to read .Entity from");

                // Backward path: read .Entity off each installed recognizer. A null
                // value is an acceptable RealityKit outcome (NonAR sim, no collision
                // shape); the §5b assertion is that the getter executes its proxy-
                // materialization body instead of throwing the old "proxy not available".
                int read = 0;
                foreach (var r in recognizers)
                {
                    _ = r.Entity; // Swift→C# HasCollisionProxy materialization
                    read++;
                }
                Pass($"EntityGestureRecognizer.Entity proxy read ({read} recognizer(s))");
            }
            catch (Exception ex) { Fail("EntityGestureRecognizer entity round-trip", ex.Message); }

            // §06 T2.4 (apple-framework-gaps/06-remaining-work.md): the carrier *identity*
            // round-trip on real RealityKit input. InstallGestures only proves the carrier
            // doesn't throw; it does not prove that the EveryEntityProtocol existential
            // preserves the bound entity's identity across the Swift→C# materialisation.
            // This drives the real EntityTranslationGestureRecognizer (a UIPanGestureRecognizer
            // subclass whose `.entity` storage is the carrier's payload word), exercising both
            // directions explicitly:
            //   forward  (C#→Swift): `recognizer.Entity = entity` boxes the ModelEntity into a
            //                        HasCollisionProxy + ExistentialContainer1 and forwards
            //                        the container to the Swift @_cdecl setter (the carrier
            //                        path a gesture-recognised callback would take to assign
            //                        the hit-tested entity).
            //   backward (Swift→C#): `recognizer.Entity` reads the Swift-stored existential
            //                        container back out, materialises a fresh HasCollisionProxy,
            //                        and exposes it through IHasCollision (the carrier path a
            //                        user's action method would take to read `recognizer.entity`
            //                        after the gesture fires).
            // Identity is asserted on the underlying Swift handle: the proxy's SwiftHandle
            // (= ExistentialContainer1.Payload0, the class-instance pointer of the boxed
            // ModelEntity) must equal the source ModelEntity's SwiftHandle (= the same Swift
            // class instance). That is the strongest assertion possible without UIKit target/
            // action plumbing — anything weaker (just non-null) does not distinguish a working
            // round-trip from a stray non-null pointer.
            try
            {
                using var entityA = new ModelEntity();
                entityA.Name = "gesture-target-A";
                using var entityB = new ModelEntity();
                entityB.Name = "gesture-target-B";

                using var recognizer = new EntityTranslationGestureRecognizer(target: null, action: null);

                // Forward: assign entityA via the C# setter (boxes ModelEntity → HasCollisionProxy
                // → ExistentialContainer1 → Swift @_cdecl setter).
                recognizer.Entity = entityA;

                // Backward: read .Entity through the EveryEntityProtocol carrier.
                var readA = recognizer.Entity;
                if (readA is null)
                    throw new InvalidOperationException("recognizer.Entity returned null after set to entityA");

                var expectedHandleA = ((Swift.Runtime.ISwiftObject)entityA).SwiftHandle;
                var actualHandleA = ((Swift.Runtime.ISwiftObject)readA).SwiftHandle;
                if (expectedHandleA != actualHandleA)
                    throw new InvalidOperationException(
                        $"EveryEntityProtocol carrier identity mismatch (A): expected handle {expectedHandleA:X}, got {actualHandleA:X}");

                // Repeat with entityB to rule out a stale-read or cached-pointer false-pass.
                recognizer.Entity = entityB;
                var readB = recognizer.Entity;
                if (readB is null)
                    throw new InvalidOperationException("recognizer.Entity returned null after set to entityB");

                var expectedHandleB = ((Swift.Runtime.ISwiftObject)entityB).SwiftHandle;
                var actualHandleB = ((Swift.Runtime.ISwiftObject)readB).SwiftHandle;
                if (expectedHandleB != actualHandleB)
                    throw new InvalidOperationException(
                        $"EveryEntityProtocol carrier identity mismatch (B): expected handle {expectedHandleB:X}, got {actualHandleB:X}");

                if (expectedHandleA == expectedHandleB)
                    throw new InvalidOperationException(
                        "Test invariant broken: distinct ModelEntity instances share a SwiftHandle");

                // Null path: clearing the Entity through the carrier round-trips as null,
                // not as a dangling proxy over the previous payload.
                recognizer.Entity = null;
                var readNull = recognizer.Entity;
                if (readNull is not null)
                    throw new InvalidOperationException(
                        "recognizer.Entity returned non-null after clearing through the carrier");

                Pass("EntityTranslationGestureRecognizer.Entity carrier identity round-trip (A → B → null)");
            }
            catch (Exception ex)
            {
                Fail("EntityTranslationGestureRecognizer.Entity carrier identity round-trip", ex.Message);
            }
        }

        // Summary
        Log($"Results: {passed} passed, {failed} failed, {skipped} skipped");
        if (failed == 0)
            Log("TEST SUCCESS", prefixed: false);
        else
            Log($"TEST FAILED: {failed} failures", prefixed: false);
        return failed;
    }

    internal static void Log(string msg, bool prefixed = true) =>
        Console.WriteLine(prefixed ? $"[REALITYKIT-TEST] {msg}" : msg);
}
