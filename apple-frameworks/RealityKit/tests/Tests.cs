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
            // resolution. Scene.AddAnchor is skipped because its `any
            // HasAnchoring` parameter has no conformance projection on the
            // AnchorEntity binding; the existential box would refuse the cast.
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
            // Pin the gap so future SDK rebuilds remember to re-validate.
            Skip("Scene.AddAnchor(IHasAnchoring) call",
                "AnchorEntity binding does not project HasAnchoring; existential box refuses the cast");

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
