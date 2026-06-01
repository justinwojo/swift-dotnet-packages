// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Hand-rolled partials that layer factory ctors onto the generated
// MusicCatalogSearchRequest, MusicLibrarySearchRequest, and
// MusicCatalogSearchSuggestionsRequest shells. The generator emits the
// storage + ISwiftObject plumbing but cannot synthesise initializers
// whose parameter is `[any P.Type]` — an array of existential metatypes
// (binding report skips them as "UnsupportedExistential").
//
// The companion Swift shim file (MusicKitShims.swift, staged into the
// per-framework wrapper) carries SBW_MusicKitShims_* @_cdecl trampolines
// that accept a stable UInt32 bitmask and decode it into the conformer
// `Type.self` value the Swift initializer wants. Bit ordering is
// append-only forever; unknown bits cause the shim to return non-zero
// and this partial to throw ArgumentException.
//
// Storage ownership matches the AttributedString shim pattern:
//
//   * C# allocates a heap slot sized by the request struct's value
//     witness table (via SwiftObjectHelper<T>.GetTypeMetadata().Size);
//   * the shim writes the initialized struct into the slot;
//   * SwiftSafeHandle takes sole ownership through the generated
//     private `T(SwiftHandle)` ctor and frees the slot on Dispose via
//     the type's destroy witness.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Swift.Runtime;

namespace MusicKit;

/// <summary>
/// Conformers of <c>MusicCatalogSearchable</c> usable as a
/// <see cref="MusicCatalogSearchRequest"/> result type filter. The bit
/// values form a stable, append-only ABI shared with the Swift shim —
/// future additions must take the next unassigned high bit.
/// </summary>
[Flags]
public enum MusicCatalogSearchTypes : uint
{
    None        = 0u,
    Album       = 1u << 0,
    Artist      = 1u << 1,
    Curator     = 1u << 2,
    MusicVideo  = 1u << 3,
    Playlist    = 1u << 4,
    RadioShow   = 1u << 5,
    RecordLabel = 1u << 6,
    Song        = 1u << 7,
    Station     = 1u << 8,
}

/// <summary>
/// Conformers of <c>MusicLibrarySearchable</c> usable as a
/// <see cref="MusicLibrarySearchRequest"/> result type filter. Stable,
/// append-only bit values mirroring the Swift shim's decoder.
/// </summary>
[Flags]
public enum MusicLibrarySearchTypes : uint
{
    None       = 0u,
    Album      = 1u << 0,
    Artist     = 1u << 1,
    MusicVideo = 1u << 2,
    Playlist   = 1u << 3,
    Song       = 1u << 4,
}

public partial class MusicCatalogSearchRequest
{
    private const uint CatalogSearchValidMask =
        (uint)(MusicCatalogSearchTypes.Album | MusicCatalogSearchTypes.Artist
            | MusicCatalogSearchTypes.Curator | MusicCatalogSearchTypes.MusicVideo
            | MusicCatalogSearchTypes.Playlist | MusicCatalogSearchTypes.RadioShow
            | MusicCatalogSearchTypes.RecordLabel | MusicCatalogSearchTypes.Song
            | MusicCatalogSearchTypes.Station);

    /// <summary>
    /// Constructs a <see cref="MusicCatalogSearchRequest"/> projection of
    /// the Swift <c>init(term: String, types: [any MusicCatalogSearchable.Type])</c>.
    /// The <paramref name="types"/> bitmask is decoded into the
    /// corresponding Swift metatype array inside the shim.
    /// </summary>
    /// <param name="term">Search term. A null reference is normalised
    /// to the empty string to match the empty-search behaviour of the
    /// Swift API.</param>
    /// <param name="types">Bitmask of MusicKit catalog item kinds to
    /// include in the result. A zero mask is legal and produces an
    /// unfiltered search (Apple's API also accepts <c>[]</c>).</param>
    /// <exception cref="ArgumentException">A bit outside the
    /// MusicCatalogSearchTypes enumeration is set.</exception>
    public static unsafe MusicCatalogSearchRequest Create(string term, MusicCatalogSearchTypes types)
    {
        uint stray = (uint)types & ~CatalogSearchValidMask;
        if (stray != 0)
        {
            throw new ArgumentException(
                $"MusicCatalogSearchTypes carries unknown bits: 0x{stray:X8}.",
                nameof(types));
        }
        var metadata = SwiftObjectHelper<MusicCatalogSearchRequest>.GetTypeMetadata();
        var heap = NativeMemory.Alloc((nuint)metadata.Size);
        bool initialized = false;
        try
        {
            var utf8 = string.IsNullOrEmpty(term)
                ? Array.Empty<byte>()
                : Encoding.UTF8.GetBytes(term);
            int status;
            fixed (byte* utf8Ptr = utf8)
            {
                status = MusicKitShimsNative.CatalogSearchRequestInit(
                    utf8Ptr, utf8.Length, (uint)types, heap);
            }
            if (status != 0)
            {
                throw new ArgumentException(
                    $"SBW_MusicKitShims_CatalogSearchRequest_Init returned status {status}.",
                    nameof(types));
            }
            initialized = true;
        }
        catch
        {
            if (initialized)
                metadata.ValueWitnessTable->Destroy(heap, metadata);
            NativeMemory.Free(heap);
            throw;
        }
        return new MusicCatalogSearchRequest(new SwiftHandle((IntPtr)heap));
    }
}

public partial class MusicLibrarySearchRequest
{
    private const uint LibrarySearchValidMask =
        (uint)(MusicLibrarySearchTypes.Album | MusicLibrarySearchTypes.Artist
            | MusicLibrarySearchTypes.MusicVideo | MusicLibrarySearchTypes.Playlist
            | MusicLibrarySearchTypes.Song);

    /// <summary>
    /// Constructs a <see cref="MusicLibrarySearchRequest"/> projection of
    /// the Swift <c>init(term: String, types: [any MusicLibrarySearchable.Type])</c>.
    /// </summary>
    /// <param name="term">Search term. Null is normalised to empty.</param>
    /// <param name="types">Bitmask of library item kinds to include.</param>
    /// <exception cref="ArgumentException">A bit outside the
    /// MusicLibrarySearchTypes enumeration is set.</exception>
    public static unsafe MusicLibrarySearchRequest Create(string term, MusicLibrarySearchTypes types)
    {
        uint stray = (uint)types & ~LibrarySearchValidMask;
        if (stray != 0)
        {
            throw new ArgumentException(
                $"MusicLibrarySearchTypes carries unknown bits: 0x{stray:X8}.",
                nameof(types));
        }
        var metadata = SwiftObjectHelper<MusicLibrarySearchRequest>.GetTypeMetadata();
        var heap = NativeMemory.Alloc((nuint)metadata.Size);
        bool initialized = false;
        try
        {
            var utf8 = string.IsNullOrEmpty(term)
                ? Array.Empty<byte>()
                : Encoding.UTF8.GetBytes(term);
            int status;
            fixed (byte* utf8Ptr = utf8)
            {
                status = MusicKitShimsNative.LibrarySearchRequestInit(
                    utf8Ptr, utf8.Length, (uint)types, heap);
            }
            if (status != 0)
            {
                throw new ArgumentException(
                    $"SBW_MusicKitShims_LibrarySearchRequest_Init returned status {status}.",
                    nameof(types));
            }
            initialized = true;
        }
        catch
        {
            if (initialized)
                metadata.ValueWitnessTable->Destroy(heap, metadata);
            NativeMemory.Free(heap);
            throw;
        }
        return new MusicLibrarySearchRequest(new SwiftHandle((IntPtr)heap));
    }
}

public partial class MusicCatalogSearchSuggestionsRequest
{
    private const uint CatalogSearchValidMask =
        (uint)(MusicCatalogSearchTypes.Album | MusicCatalogSearchTypes.Artist
            | MusicCatalogSearchTypes.Curator | MusicCatalogSearchTypes.MusicVideo
            | MusicCatalogSearchTypes.Playlist | MusicCatalogSearchTypes.RadioShow
            | MusicCatalogSearchTypes.RecordLabel | MusicCatalogSearchTypes.Song
            | MusicCatalogSearchTypes.Station);

    /// <summary>
    /// Constructs a <see cref="MusicCatalogSearchSuggestionsRequest"/>
    /// projection of the Swift
    /// <c>init(term: String, includingTopResultsOfTypes: [any MusicCatalogSearchable.Type])</c>.
    /// </summary>
    /// <param name="term">Search term. Null is normalised to empty.</param>
    /// <param name="includingTopResultsOfTypes">Bitmask of catalog
    /// item kinds to include as suggested top results. A zero mask is
    /// legal (Apple's API defaults to <c>[]</c>).</param>
    /// <exception cref="ArgumentException">A bit outside the
    /// MusicCatalogSearchTypes enumeration is set.</exception>
    public static unsafe MusicCatalogSearchSuggestionsRequest Create(
        string term, MusicCatalogSearchTypes includingTopResultsOfTypes)
    {
        uint stray = (uint)includingTopResultsOfTypes & ~CatalogSearchValidMask;
        if (stray != 0)
        {
            throw new ArgumentException(
                $"MusicCatalogSearchTypes carries unknown bits: 0x{stray:X8}.",
                nameof(includingTopResultsOfTypes));
        }
        var metadata = SwiftObjectHelper<MusicCatalogSearchSuggestionsRequest>.GetTypeMetadata();
        var heap = NativeMemory.Alloc((nuint)metadata.Size);
        bool initialized = false;
        try
        {
            var utf8 = string.IsNullOrEmpty(term)
                ? Array.Empty<byte>()
                : Encoding.UTF8.GetBytes(term);
            int status;
            fixed (byte* utf8Ptr = utf8)
            {
                status = MusicKitShimsNative.CatalogSearchSuggestionsRequestInit(
                    utf8Ptr, utf8.Length, (uint)includingTopResultsOfTypes, heap);
            }
            if (status != 0)
            {
                throw new ArgumentException(
                    $"SBW_MusicKitShims_CatalogSearchSuggestionsRequest_Init returned status {status}.",
                    nameof(includingTopResultsOfTypes));
            }
            initialized = true;
        }
        catch
        {
            if (initialized)
                metadata.ValueWitnessTable->Destroy(heap, metadata);
            NativeMemory.Free(heap);
            throw;
        }
        return new MusicCatalogSearchSuggestionsRequest(new SwiftHandle((IntPtr)heap));
    }
}

internal static unsafe partial class MusicKitShimsNative
{
    [LibraryImport("MusicKitSwiftBindings", EntryPoint = "SBW_MusicKitShims_CatalogSearchRequest_Init")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int CatalogSearchRequestInit(byte* utf8Ptr, int utf8Len, uint mask, void* outBuffer);

    [LibraryImport("MusicKitSwiftBindings", EntryPoint = "SBW_MusicKitShims_LibrarySearchRequest_Init")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int LibrarySearchRequestInit(byte* utf8Ptr, int utf8Len, uint mask, void* outBuffer);

    [LibraryImport("MusicKitSwiftBindings", EntryPoint = "SBW_MusicKitShims_CatalogSearchSuggestionsRequest_Init")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int CatalogSearchSuggestionsRequestInit(byte* utf8Ptr, int utf8Len, uint mask, void* outBuffer);
}
