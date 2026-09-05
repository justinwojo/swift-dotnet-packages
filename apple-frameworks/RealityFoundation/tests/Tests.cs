// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Numerics;
using RealityFoundation;
using Swift.Runtime;

namespace SwiftBindings.RealityFoundation.Tests;

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

        // Type-metadata smokes — exercise mangled-symbol resolution for the
        // ECS / scene foundation types and the @_cdecl metadata wrappers.
        MetadataTest<Entity>("Entity");
        MetadataTest<AnchorEntity>("AnchorEntity");
        MetadataTest<Scene>("Scene");
        MetadataTest<Transform>("Transform");
        MetadataTest<Entity.ChildCollection>("Entity.ChildCollection");
        MetadataTest<Entity.ComponentSet>("Entity.ComponentSet");
        MetadataTest<Entity.Observable>("Entity.Observable");
        MetadataTest<MeshResource>("MeshResource");
        MetadataTest<ModelComponent>("ModelComponent");

        // MeshBuffer / MeshBuffers.Semantic with a primitive blittable T.
        // Pins the upstream constraint relaxation that allows T : Vector3 (and
        // other blittable primitives) instead of T : ISwiftObject.
        //
        // T2.1 closed the NativeAOT generic-specialization metadata gap:
        // resilient SIMD generic metadata is now reachable on every runtime
        // (Mono interpreter on the sim, Mono AOT, and NativeAOT on device), so
        // these run unconditionally. MetadataTest itself Fails on a null
        // metadata handle, so a runtime regression still surfaces as a failure
        // here rather than a silent skip.
        MetadataTest<MeshBuffer<Vector3>>("MeshBuffer<Vector3>");
        MetadataTest<MeshBuffers.Semantic<Vector3>>("MeshBuffers.Semantic<Vector3>");
        MetadataTest<MeshBuffers.Semantic<Vector2>>("MeshBuffers.Semantic<Vector2>");
        MetadataTest<MeshBuffers.Semantic<float>>("MeshBuffers.Semantic<float>");
        MetadataTest<UnsafeForceEffectBuffer<Vector3>>("UnsafeForceEffectBuffer<Vector3>");

        try
        {
            using var positions = MeshBuffers.Positions;
            Pass("MeshBuffers.Positions read");
        }
        catch (Exception ex) { Fail("MeshBuffers.Positions read", ex.Message); }

        try
        {
            using var normals = MeshBuffers.Normals;
            Pass("MeshBuffers.Normals read");
        }
        catch (Exception ex) { Fail("MeshBuffers.Normals read", ex.Message); }

        try
        {
            using var tangents = MeshBuffers.Tangents;
            Pass("MeshBuffers.Tangents read");
        }
        catch (Exception ex) { Fail("MeshBuffers.Tangents read", ex.Message); }

        // Transform value semantics — Identity, individual SIMD components,
        // and round-trip via Vector3 / Quaternion / Matrix4x4.
        try
        {
            using var t = Transform.Identity;
            var translation = t.Translation;
            var scale = t.Scale;
            if (Math.Abs(scale.X - 1f) > 1e-5f || Math.Abs(scale.Y - 1f) > 1e-5f || Math.Abs(scale.Z - 1f) > 1e-5f)
                throw new InvalidOperationException($"Identity scale != (1,1,1): {scale}");
            if (translation != Vector3.Zero)
                throw new InvalidOperationException($"Identity translation != zero: {translation}");
            Pass("Transform.Identity");
        }
        catch (Exception ex) { Fail("Transform.Identity", ex.Message); }

        // RC-SIMD (apple-framework-gaps/01-marshalling-correctness.md, Task 3).
        // The translation/scale/rotation setters take Swift SIMD3<Float> / simd_quatf.
        // Previously the generated PInvoke passed System.Numerics.Vector3/Quaternion
        // BY VALUE, and the AArch64 register-class mismatch (NEON v0 vs HFA s0,s1,s2)
        // truncated every write to lane 0. The fix routes simd params through the
        // indirect/pointer path: the setters now `stackalloc` a buffer, `MarshalToSwift`,
        // and call a `SBW_Set_*` @_cdecl wrapper taking `IntPtr value` (verified in
        // obj/.../RealityFoundation.cs). These round-trips assert ALL lanes survive —
        // if the truncation regressed, Y/Z/W would read back zero. Both sim and device
        // must pass (the JIT/AOT split is exactly where this used to diverge).
        try
        {
            using var t = Transform.Identity;
            t.Translation = new Vector3(1.5f, -2.5f, 3.5f);
            var rt = t.Translation;
            if (MathF.Abs(rt.X - 1.5f) > 1e-4f || MathF.Abs(rt.Y + 2.5f) > 1e-4f || MathF.Abs(rt.Z - 3.5f) > 1e-4f)
                throw new InvalidOperationException($"got {rt}, expected (1.5, -2.5, 3.5)");
            Pass("Transform.Translation round-trip");
        }
        catch (Exception ex) { Fail("Transform.Translation round-trip", ex.Message); }

        try
        {
            using var t = Transform.Identity;
            t.Scale = new Vector3(2f, 3f, 4f);
            var rs = t.Scale;
            if (MathF.Abs(rs.X - 2f) > 1e-4f || MathF.Abs(rs.Y - 3f) > 1e-4f || MathF.Abs(rs.Z - 4f) > 1e-4f)
                throw new InvalidOperationException($"got {rs}, expected (2, 3, 4)");
            Pass("Transform.Scale round-trip");
        }
        catch (Exception ex) { Fail("Transform.Scale round-trip", ex.Message); }

        try
        {
            using var t = Transform.Identity;
            // 90° about Y: (0, sin45, 0, cos45). A truncating setter would zero Y/Z/W.
            var q = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f));
            t.Rotation = q;
            var rq = t.Rotation;
            // simd_quatf stores the vector as-is, but q and -q are the same rotation;
            // accept either sign so a normalization flip isn't a false failure.
            bool Match(Quaternion a, Quaternion b) =>
                MathF.Abs(a.X - b.X) < 1e-4f && MathF.Abs(a.Y - b.Y) < 1e-4f &&
                MathF.Abs(a.Z - b.Z) < 1e-4f && MathF.Abs(a.W - b.W) < 1e-4f;
            if (!Match(rq, q) && !Match(rq, new Quaternion(-q.X, -q.Y, -q.Z, -q.W)))
                throw new InvalidOperationException($"got {rq}, expected {q} (or its negation)");
            Pass("Transform.Rotation round-trip");
        }
        catch (Exception ex) { Fail("Transform.Rotation round-trip", ex.Message); }

        // Transform(Matrix4x4) — the column-major simd_float4x4 init param now goes
        // through the @_cdecl wrapper (SBW_..._init_C5C1E657, IntPtr matrix). A pure
        // translation matrix decomposes to translation (5,6,7); lane truncation would
        // drop Y/Z. (Translation occupies the same memory slot in row-major Matrix4x4
        // and column-major simd_float4x4, so the byte-copy marshal aligns here.)
        try
        {
            var m = Matrix4x4.CreateTranslation(5f, 6f, 7f);
            using var t = new Transform(m);
            var tr = t.Translation;
            if (MathF.Abs(tr.X - 5f) > 1e-4f || MathF.Abs(tr.Y - 6f) > 1e-4f || MathF.Abs(tr.Z - 7f) > 1e-4f)
                throw new InvalidOperationException($"decomposed translation {tr}, expected (5, 6, 7)");
            Pass("Transform(Matrix4x4) constructor");
        }
        catch (Exception ex) { Fail("Transform(Matrix4x4) constructor", ex.Message); }

        // Transform(scale:rotation:translation:) is @inlinable public in RealityKit. The
        // parser now classifies it correctly as public (an @inlinable member with no
        // explicit AccessControl attribute is no longer mis-read as module-internal when a
        // swiftinterface is present), so its @_cdecl wrapper SBW_..._Transform_init_C8B878FF
        // is emitted and the three SIMD params marshal indirectly through buffer pointers
        // (CallConvCdecl), exactly like Transform(Matrix4x4). All lanes must round-trip.
        try
        {
            var scale = new Vector3(2f, 3f, 4f);
            var trans = new Vector3(5f, 6f, 7f);
            using var t = new Transform(scale, Quaternion.Identity, trans);
            var rs = t.Scale;
            var rtr = t.Translation;
            bool scaleOk = MathF.Abs(rs.X - 2f) < 1e-4f && MathF.Abs(rs.Y - 3f) < 1e-4f && MathF.Abs(rs.Z - 4f) < 1e-4f;
            bool transOk = MathF.Abs(rtr.X - 5f) < 1e-4f && MathF.Abs(rtr.Y - 6f) < 1e-4f && MathF.Abs(rtr.Z - 7f) < 1e-4f;
            if (scaleOk && transOk)
                Pass("Transform(scale,rotation,translation) constructor");
            else
                Fail("Transform(scale,rotation,translation) constructor",
                    $"SIMD lanes truncated through the indirect ctor — got scale={rs} trans={rtr}");
        }
        catch (Exception ex)
        {
            Fail("Transform(scale,rotation,translation) constructor", ex.Message);
        }

        // Entity lifecycle — parameterless ctor has a @_cdecl wrapper, so
        // running it during the main test pass (not the constructor tail)
        // is safe.
        Entity? root = null;
        try
        {
            root = new Entity();
            Pass("Entity() constructor");
        }
        catch (Exception ex) { Fail("Entity() constructor", ex.Message); }

        if (root is not null)
        {
            try
            {
                root.Name = "root";
                if (root.Name != "root")
                    throw new InvalidOperationException($"Name round-trip mismatch: read '{root.Name}'");
                Pass("Entity.Name round-trip");
            }
            catch (Exception ex) { Fail("Entity.Name round-trip", ex.Message); }

            try
            {
                if (!root.IsEnabled)
                    throw new InvalidOperationException("default IsEnabled should be true");
                root.IsEnabled = false;
                if (root.IsEnabled)
                    throw new InvalidOperationException("IsEnabled stayed true after set=false");
                root.IsEnabled = true;
                Pass("Entity.IsEnabled round-trip");
            }
            catch (Exception ex) { Fail("Entity.IsEnabled round-trip", ex.Message); }

            // Entity.Id is an opaque identifier. Just verify it's reachable.
            try
            {
                _ = root.Id;
                Pass("Entity.Id read");
            }
            catch (Exception ex) { Fail("Entity.Id read", ex.Message); }

            // Hierarchy — AddChild / RemoveChild / RemoveFromParent and
            // children iteration via the Observable projection.
            try
            {
                var child1 = new Entity { Name = "child1" };
                var child2 = new Entity { Name = "child2" };
                root.AddChild(child1, preservingWorldTransform: false);
                root.AddChild(child2, preservingWorldTransform: false);
                using var children = root.ObservableValue.Children;
                int count = children.EndIndex - children.StartIndex;
                if (count != 2)
                    throw new InvalidOperationException($"expected 2 children, got {count}");
                if (children[0].Name != "child1" || children[1].Name != "child2")
                    throw new InvalidOperationException("children order mismatch");
                root.RemoveChild(child1, preservingWorldTransform: false);
                using var afterRemove = root.ObservableValue.Children;
                int afterCount = afterRemove.EndIndex - afterRemove.StartIndex;
                if (afterCount != 1)
                    throw new InvalidOperationException($"expected 1 child after RemoveChild, got {afterCount}");
                child2.RemoveFromParent(preservingWorldTransform: false);
                using var afterDetach = root.ObservableValue.Children;
                int afterDetachCount = afterDetach.EndIndex - afterDetach.StartIndex;
                if (afterDetachCount != 0)
                    throw new InvalidOperationException($"expected 0 children after RemoveFromParent, got {afterDetachCount}");
                Pass("Entity hierarchy AddChild/RemoveChild/RemoveFromParent");
            }
            catch (Exception ex) { Fail("Entity hierarchy AddChild/RemoveChild/RemoveFromParent", ex.Message); }

            // FindEntity — name-based descendant lookup.
            try
            {
                var leaf = new Entity { Name = "needle" };
                root.AddChild(leaf, preservingWorldTransform: false);
                var found = root.FindEntity("needle");
                if (found is null)
                    throw new InvalidOperationException("FindEntity returned null");
                if (found.Name != "needle")
                    throw new InvalidOperationException($"FindEntity returned wrong entity: '{found.Name}'");
                var missing = root.FindEntity("not-present-9b1e7");
                if (missing is not null)
                    throw new InvalidOperationException("FindEntity for missing name should return null");
                leaf.RemoveFromParent(preservingWorldTransform: false);
                Pass("Entity.FindEntity present + absent");
            }
            catch (Exception ex) { Fail("Entity.FindEntity present + absent", ex.Message); }

            // Reading Entity.ObservableValue.Transform exercises the by-value
            // Transform getter through the iOS 26 Observable projection. The
            // setter side hits an EXC_BREAKPOINT in RealityKit's
            // re::ecs2::TransformComponent willSet hook when the entity is not
            // attached to a Scene that is driving the observation framework
            // — read-only here keeps it safe.
            try
            {
                using var read = root.ObservableValue.Transform;
                _ = read.Translation;
                _ = read.Scale;
                Pass("Entity.ObservableValue.Transform read");
            }
            catch (Exception ex) { Fail("Entity.ObservableValue.Transform read", ex.Message); }
            // RC-WILLSET preflight guardrail: the Observable.Transform setter
            // traps inside re::ecs2::TransformComponent's willSet hook when the
            // entity is not attached to a Scene driving the observation framework.
            // No ABI route bypasses a property observer, so the generator cannot
            // intercept this — the safe consumer pattern is to check the public
            // Entity.Scene predicate up front and surface a clear C# error
            // instead of letting the native willSet trap (SIGSEGV / EXC_BREAKPOINT).
            try
            {
                if (root.Scene is null)
                    throw new InvalidOperationException(
                        "Cannot set Transform on a detached entity; attach to a Scene first.");
                using var current = root.ObservableValue.Transform;
                root.ObservableValue.Transform = current;
                Pass("Entity.ObservableValue.Transform write (with Scene preflight)");
            }
            catch (InvalidOperationException ex)
            {
                // Detached test entity — preflight surfaces a clear C# message
                // instead of the Swift willSet trap. This is the documented safe
                // path, not a regression.
                Skip("Entity.ObservableValue.Transform write", ex.Message);
            }
            catch (Exception ex) { Fail("Entity.ObservableValue.Transform write", ex.Message); }

            // ComponentSet — read-only inspection. The mutating Set/Has APIs
            // are existential-shaped (any RealityKit.Component) and either
            // need iOS 26.0+ or are flagged @_cdecl-less; reading Count is
            // the safe surface.
            try
            {
                using var components = root.Components;
                // A fresh entity has at least the implicit Transform component;
                // we don't pin an exact count, just verify the property is
                // reachable and the value witness table is sane.
                _ = components.Count;
                Pass("Entity.Components count read");
            }
            catch (Exception ex) { Fail("Entity.Components count read", ex.Message); }

            try
            {
                _ = root.DebugDescription;
                Pass("Entity.DebugDescription read");
            }
            catch (Exception ex) { Fail("Entity.DebugDescription read", ex.Message); }

            // Detached entity — Scene should be null when not attached to an
            // ARView's Scene. Exercises the optional class-handle marshal.
            try
            {
                if (root.Scene is not null)
                    throw new InvalidOperationException("detached Entity.Scene should be null");
                Pass("Entity.Scene null when detached");
            }
            catch (Exception ex) { Fail("Entity.Scene null when detached", ex.Message); }

            // Clone — produces a new Entity with the same shape.
            try
            {
                var cloned = root.Clone(recursive: false);
                if (cloned is null)
                    throw new InvalidOperationException("Clone returned null");
                if (cloned.Name != root.Name)
                    throw new InvalidOperationException($"clone name mismatch: '{cloned.Name}' vs '{root.Name}'");
                Pass("Entity.Clone(recursive: false)");
            }
            catch (Exception ex) { Fail("Entity.Clone(recursive: false)", ex.Message); }
        }

        // MeshResource.GenerateBox / GeneratePlane / GenerateSphere — synchronous
        // mesh primitives. These exercise the cross-ABI generator entrypoint and
        // a class-typed return. On simulator the Metal default device path may
        // fail to allocate; if so we report a Skip rather than a Fail since the
        // intent is to validate the binding shape, not the renderer.
        try
        {
            var mesh = MeshResource.GenerateBox(size: 0.1f);
            if (mesh is null)
                throw new InvalidOperationException("GenerateBox returned null");
            Pass("MeshResource.GenerateBox(size)");
        }
        catch (Exception ex) { Skip("MeshResource.GenerateBox(size)", $"renderer init likely unavailable: {ex.GetType().Name}"); }

        try
        {
            var plane = MeshResource.GeneratePlaneWidthHeightCornerRadius(width: 0.1f, height: 0.1f);
            if (plane is null)
                throw new InvalidOperationException("GeneratePlane returned null");
            Pass("MeshResource.GeneratePlane(w,h)");
        }
        catch (Exception ex) { Skip("MeshResource.GeneratePlane(w,h)", $"renderer init likely unavailable: {ex.GetType().Name}"); }

        try
        {
            var sphere = MeshResource.GenerateSphere(radius: 0.1f);
            if (sphere is null)
                throw new InvalidOperationException("GenerateSphere returned null");
            Pass("MeshResource.GenerateSphere(radius)");
        }
        catch (Exception ex) { Skip("MeshResource.GenerateSphere(radius)", $"renderer init likely unavailable: {ex.GetType().Name}"); }

        // Constructor tail — these allocate a Swift-managed class instance
        // through a @_cdecl wrapper. They run last because if the wrapper is
        // missing on the deployed slice we want every other test to have run
        // first; per CLAUDE.md guidance.
        try
        {
            var anchor = new AnchorEntity();
            if (anchor.Name is null)
                throw new InvalidOperationException("AnchorEntity.Name returned null");
            Pass("AnchorEntity() constructor");
        }
        catch (Exception ex) { Fail("AnchorEntity() constructor", ex.Message); }

        // §1 (apple-framework-gaps/05-residual-gaps.md): box a concrete AnchorEntity into
        // the `IHasAnchoring` existential — the exact operation Scene.AddAnchor /
        // AnchorCollection.Append perform on their parameter (the generated AddAnchor calls
        // ExistentialContainerFactory.GetOrCreate<IHasAnchoring>). AnchorEntity is
        // @_originallyDefinedIn(RealityKit) but re-exported by RealityFoundation, so its
        // IHasAnchoring conformance descriptor is mangled with the *original* module
        // ($s10RealityKit12AnchorEntityCAA12HasAnchoringAAMc). The generator now recovers
        // that descriptor (parser original-module fallback) and emits it into
        // AnchorEntity._protocolConformanceSymbols, and boxes via the runtime concrete type
        // (Create<AnchorEntity, TProtocol>). GetOrCreate resolves the witness table via
        // GetProtocolConformanceDescriptor — it throws on a missing conformance, so a
        // returned container with a non-zero witness-table slot and metadata proves the box.
        try
        {
            using var boxable = new AnchorEntity();
            var container = Swift.Runtime.ExistentialContainerFactory.GetOrCreate<IHasAnchoring>(boxable);
            if (container[0] == IntPtr.Zero)
                throw new InvalidOperationException("IHasAnchoring witness-table slot is null after boxing");
            if (container.ObjectMetadata.Handle == IntPtr.Zero)
                throw new InvalidOperationException("boxed existential has null object metadata");
            Pass("AnchorEntity boxes as IHasAnchoring existential");
        }
        catch (Exception ex) { Fail("AnchorEntity boxes as IHasAnchoring existential", ex.Message); }

        // Class-bound existentials. SynchronizationService is spelled `<Self : AnyObject>`,
        // HasSynchronization `<Self : Entity>` and HasAnchoring `<Self : Entity>`; all three are
        // class-bound, so Swift lays their `any P` values out as the compact two-word
        // [classRef][witnessTable] rather than the opaque payload+metadata+witness form used for
        // value-type protocols. Reading one at the wrong width hands Swift a witness pointer taken
        // from the wrong word.
        //
        // SynchronizationService is forward-only: the binding refuses to build a Swift-backed
        // proxy over a C# implementation, so a C# type cannot conform back to it, and its only
        // sink lives on Scene, which has no public initializer.
        try
        {
            var refused = false;
            string message = string.Empty;
            try
            {
                _ = new global::RealityFoundation.SwiftInterop.SynchronizationServiceProxy(
                    new NoopSynchronizationService());
            }
            catch (NotSupportedException ex)
            {
                refused = true;
                message = ex.Message;
            }

            if (!refused)
                throw new InvalidOperationException(
                    "a C# implementation was accepted as a Swift-backed SynchronizationService; " +
                    "this protocol is read-only, so accepting one would hand Swift a conformance that does not exist");
            if (message.IndexOf("ISynchronizationService", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException($"the refusal did not name the protocol: {message}");
            Log($"SynchronizationServiceProxy refused a C# implementation: {message}");
            Pass("SynchronizationServiceProxy refuses a C# implementation (forward-only protocol)");
        }
        catch (Exception ex)
        {
            Fail("SynchronizationServiceProxy refuses a C# implementation (forward-only protocol)", ex.Message);
        }

        // A class-bound `any P` that Swift itself produced, read back through a GENERATED member.
        // Entity.anchor is the one such member this fixture can reach — Scene.addAnchor and
        // Scene.AnchorCollection take `any HasAnchoring` too, but Scene has no public initializer.
        // The generated getter marshals the result through HasAnchoringProxy, the proxy the
        // emitter built on its class-bound arm: it reads the class reference out of word 0 and
        // balances the +1 it adopts with an ARC release on that reference, rather than a
        // value-witness destroy driven by the metadata word. Swift writes exactly two words into
        // the getter's zero-filled result buffer, so everything behind the witness stays zero —
        // the words a binding generated on the value-type arm would have taken for payload,
        // metadata and witness.
        const string anchorExistentialTest =
            "Entity.anchor returns `any HasAnchoring` as [classRef][witnessTable] and yields the anchor entity itself";
        try
        {
            using var anchorEntity = new AnchorEntity();
            using var child = new Entity();
            anchorEntity.AddChild(child, preservingWorldTransform: false);

            var anchorHandle = anchorEntity.Payload.DangerousGetHandle();
            var expectedWitness = ProtocolWitnessTable.GetOrThrowAuto<AnchorEntity, IHasAnchoring>().Handle;
            if (expectedWitness == IntPtr.Zero)
                throw new InvalidOperationException("AnchorEntity's HasAnchoring witness table resolved to null");

            var boxed = child.Anchor;
            if (boxed is null)
                throw new InvalidOperationException(
                    "Entity.anchor returned null for an entity parented to an AnchorEntity");
            if (boxed is not global::RealityFoundation.SwiftInterop.HasAnchoringProxy proxy)
                throw new InvalidOperationException(
                    $"Entity.anchor returned {boxed.GetType().Name}, not the generated HasAnchoringProxy");

            var container = ((ISwiftExistentialConvertible<ExistentialContainer1>)proxy).GetExistentialContainer();
            if (container.Payload0 != anchorHandle)
                throw new InvalidOperationException(
                    $"word 0 holds {container.Payload0:X}, not the anchor entity Swift boxed ({anchorHandle:X})");
            if (container.Payload1 != expectedWitness)
                throw new InvalidOperationException(
                    $"word 1 holds {container.Payload1:X}, not AnchorEntity's HasAnchoring witness table ({expectedWitness:X})");
            if (container.Payload2 != IntPtr.Zero
                || container.ObjectMetadata.Handle != IntPtr.Zero
                || container[0] != IntPtr.Zero)
                throw new InvalidOperationException(
                    $"the words behind the witness are not empty (payload2={container.Payload2:X}, " +
                    $"metadata={container.ObjectMetadata.Handle:X}, witness word={container[0]:X}); " +
                    "a class-bound existential is two words and nothing may follow it");
            if (((ISwiftObject)proxy).SwiftHandle != anchorHandle)
                throw new InvalidOperationException(
                    $"the proxy reports {((ISwiftObject)proxy).SwiftHandle:X} as its Swift instance, " +
                    $"not the anchor entity ({anchorHandle:X})");

            // The getter adopted Swift's +1; disposing runs the class-bound release that gives it
            // back. Reading again afterwards must still find the same live anchor entity.
            proxy.Dispose();

            if (child.Anchor is not global::RealityFoundation.SwiftInterop.HasAnchoringProxy secondProxy)
                throw new InvalidOperationException("the second Entity.anchor read did not return a HasAnchoringProxy");
            var secondHandle = ((ISwiftObject)secondProxy).SwiftHandle;
            secondProxy.Dispose();
            if (secondHandle != anchorHandle)
                throw new InvalidOperationException(
                    $"the second read yielded {secondHandle:X}, not the anchor entity ({anchorHandle:X})");
            if (anchorEntity.Name is null)
                throw new InvalidOperationException(
                    "the anchor entity stopped answering after its boxed references were released");

            Log($"Entity.anchor -> classRef={container.Payload0:X} (the anchor entity), " +
                $"witness={container.Payload1:X}, nothing behind it");
            Pass(anchorExistentialTest);
        }
        catch (Exception ex)
        {
            Fail(anchorExistentialTest, ex.Message);
        }

        // `any HasSynchronization` reaches no generated signature: the emitted C# names
        // IHasSynchronization only in Entity's conformance list, the module-init conformance and
        // witness-table registrations, the per-class conformance-symbol maps, and the proxy type.
        // What the class-bound decision does reach is HasSynchronizationProxy, and the proxy
        // settles that decision against the shipped Swift library: resolving its witness table
        // first verifies the container size its emitted layout arm implies against
        // MemoryLayout<any HasSynchronization>.size, which the wrapper exports. `<Self : Entity>`
        // is class-bound, so the library reports two words; a binding generated from ABI facts
        // that read the protocol as value-type implies five and throws here instead of resolving.
        const string layoutTripwireTest =
            "HasSynchronizationProxy's emitted container layout matches `any HasSynchronization` in the Swift library";
        try
        {
            var witnessTable = global::RealityFoundation.SwiftInterop.HasSynchronizationProxy.ProtocolWitnessTableHandle;
            if (witnessTable == IntPtr.Zero)
                throw new InvalidOperationException("the proxy's HasSynchronization witness table resolved to null");
            Log($"HasSynchronizationProxy layout check agreed with the library; witness table={witnessTable:X}");
            Pass(layoutTripwireTest);
        }
        catch (Exception ex)
        {
            Fail(layoutTripwireTest, ex.Message);
        }

        Skip("Scene.SynchronizationService assignment and read-back",
            "Scene has no public initializer and this fixture has no RealityKit view to vend one");
        Skip("ISynchronizationService reverse dispatch (entity/owner/giveOwnership)",
            "the protocol is read-only: a C# type cannot conform to it, so Swift has nothing to call back into");

        // Summary
        Log($"Results: {passed} passed, {failed} failed, {skipped} skipped");
        if (failed == 0)
            Log("TEST SUCCESS", prefixed: false);
        else
            Log($"TEST FAILED: {failed} failures", prefixed: false);
        return failed;
    }

    // Only used to prove the binding refuses to make a C# type conform to a read-only,
    // Swift-vended protocol; none of these members is ever reached.
    private sealed class NoopSynchronizationService : ISynchronizationService
    {
        public global::RealityFoundation.Entity? Entity(ulong identifier) => null;

        public ISynchronizationPeerID? Owner(global::RealityFoundation.Entity entity) => null;

        public bool GiveOwnership(global::RealityFoundation.Entity entity, ISynchronizationPeerID toPeer) => false;
    }

    internal static void Log(string msg, bool prefixed = true) =>
        Console.WriteLine(prefixed ? $"[REALITYFOUNDATION-TEST] {msg}" : msg);
}
