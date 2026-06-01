// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// @_cdecl trampolines that project MusicKit's `[any P.Type]`-typed search
// request constructors into a calling-convention the generator can speak.
//
// The Swift initializers in question are:
//
//   * MusicCatalogSearchRequest.init(term: String, types: [any MusicCatalogSearchable.Type])
//   * MusicLibrarySearchRequest.init(term: String, types: [any MusicLibrarySearchable.Type])
//   * MusicCatalogSearchSuggestionsRequest.init(term: String,
//                                               includingTopResultsOfTypes types: [any MusicCatalogSearchable.Type])
//
// The last is the sibling search-suggestions ctor; it shares the same
// `[any MusicCatalogSearchable.Type]` shape and reuses the same
// bit-to-type mapping.
//
// The `[any P.Type]` parameter is a heterogeneous array of existential
// metatypes that the binding generator cannot project (binding report
// emits "UnsupportedExistential" for these inits). The shims accept a
// stable UInt32 bitmask whose bits index into a fixed, append-only set
// of conformer metatypes — one mapping table per protocol. The C# face
// is a `[Flags]` enum per protocol so call sites read naturally:
//
//     MusicCatalogSearchRequest.Create(term, MusicCatalogSearchTypes.Song | .Album)
//
// Encoding is append-only forever: new conformers in a future SDK get
// the next high bit. Unknown bits cause the shim to return a non-zero
// status so the C# wrapper can throw ArgumentException rather than
// silently dropping the bit.
//
// Storage ownership matches the existing SBW_AttributedString_Init
// pattern: the caller (C#) allocates a heap slot sized by the request
// struct's value witness table, the shim writes the initialized struct
// into the slot, and a SwiftSafeHandle takes sole ownership. Mutations
// on the C# side see refcount 1 and apply in place.
//
// Availability gating: the shim is compiled against the binding's
// wrapper deployment target (currently iOS 15.0 / macOS 12.0 /
// macCatalyst 15.0 / tvOS 15.0). Each @_cdecl entry point is annotated
// with the @available band of the highest-versioned API it references.
// Within decoders, conformer appends introduced in later point releases
// are wrapped in `if #available` so the catalog-search decoder remains
// callable at the iOS 15.0 baseline.

import MusicKit
import Foundation

// MARK: - MusicCatalogSearchable bit mapping (9 conformers)
//
// Bits assigned in alphabetical order of the conforming type name. This
// order is the public ABI contract — any reshuffle is a breaking change
// for already-shipped binaries. New conformers from future MusicKit
// releases must take the next unassigned high bit.
//
//   bit 0 — Album        (iOS 15.0 / macOS 12.0 / tvOS 15.0)
//   bit 1 — Artist       (iOS 15.0 / macOS 12.0 / tvOS 15.0)
//   bit 2 — Curator      (iOS 15.4 / macOS 12.3 / tvOS 15.4)
//   bit 3 — MusicVideo   (iOS 15.4 / macOS 12.3 / tvOS 15.4)
//   bit 4 — Playlist     (iOS 15.0 / macOS 12.0 / tvOS 15.0)
//   bit 5 — RadioShow    (iOS 15.4 / macOS 12.3 / tvOS 15.4)
//   bit 6 — RecordLabel  (iOS 15.0 / macOS 12.0 / tvOS 15.0)
//   bit 7 — Song         (iOS 15.0 / macOS 12.0 / tvOS 15.0)
//   bit 8 — Station      (iOS 15.0 / macOS 12.0 / tvOS 15.0)

private let SBW_MUSICKIT_CATALOG_SEARCH_VALID_MASK: UInt32 = 0x1FF

@available(iOS 15.0, macOS 12.0, macCatalyst 15.0, tvOS 15.0, *)
@inline(__always)
private func decodeCatalogSearchableTypes(_ mask: UInt32) -> [any MusicCatalogSearchable.Type] {
    var out: [any MusicCatalogSearchable.Type] = []
    if mask & (1 << 0) != 0 { out.append(Album.self) }
    if mask & (1 << 1) != 0 { out.append(Artist.self) }
    if mask & (1 << 2) != 0 {
        if #available(iOS 15.4, macOS 12.3, macCatalyst 15.4, tvOS 15.4, *) {
            out.append(Curator.self)
        }
    }
    if mask & (1 << 3) != 0 {
        if #available(iOS 15.4, macOS 12.3, macCatalyst 15.4, tvOS 15.4, *) {
            out.append(MusicVideo.self)
        }
    }
    if mask & (1 << 4) != 0 { out.append(Playlist.self) }
    if mask & (1 << 5) != 0 {
        if #available(iOS 15.4, macOS 12.3, macCatalyst 15.4, tvOS 15.4, *) {
            out.append(RadioShow.self)
        }
    }
    if mask & (1 << 6) != 0 { out.append(RecordLabel.self) }
    if mask & (1 << 7) != 0 { out.append(Song.self) }
    if mask & (1 << 8) != 0 { out.append(Station.self) }
    return out
}

// MARK: - MusicLibrarySearchable bit mapping (5 conformers)
//
// All five conformances are gated by the MusicLibrarySearchable
// protocol itself (iOS 16.0 / macOS 14.0 / macCatalyst 17.0 / tvOS 16.0).
//
//   bit 0 — Album
//   bit 1 — Artist
//   bit 2 — MusicVideo
//   bit 3 — Playlist
//   bit 4 — Song

private let SBW_MUSICKIT_LIBRARY_SEARCH_VALID_MASK: UInt32 = 0x1F

@available(iOS 16.0, macOS 14.0, macCatalyst 17.0, tvOS 16.0, *)
@inline(__always)
private func decodeLibrarySearchableTypes(_ mask: UInt32) -> [any MusicLibrarySearchable.Type] {
    var out: [any MusicLibrarySearchable.Type] = []
    if mask & (1 << 0) != 0 { out.append(Album.self) }
    if mask & (1 << 1) != 0 { out.append(Artist.self) }
    if mask & (1 << 2) != 0 { out.append(MusicVideo.self) }
    if mask & (1 << 3) != 0 { out.append(Playlist.self) }
    if mask & (1 << 4) != 0 { out.append(Song.self) }
    return out
}

@inline(__always)
private func decodeUtf8Term(_ utf8Ptr: UnsafePointer<UInt8>?, _ utf8Len: Int) -> String {
    if let utf8Ptr = utf8Ptr, utf8Len > 0 {
        return String(decoding: UnsafeBufferPointer(start: utf8Ptr, count: utf8Len), as: UTF8.self)
    }
    return ""
}

// MARK: - @_cdecl trampolines
//
// Each trampoline returns Int32:
//   0  — success, outBuffer holds the initialized struct (caller owns)
//   -1 — `mask` carries bits outside the valid range (programming error;
//        C# wrapper throws ArgumentException)
//
// outBuffer is NOT written when the return value is non-zero, so the
// caller must NOT run the destroy witness on the slot in that case.

/// Initialize a `MusicCatalogSearchRequest(term:types:)` into the
/// caller-provided heap slot. `mask` is a UInt32 bitmask over
/// SBW_MUSICKIT_CATALOG_SEARCH_VALID_MASK (see top of file for the
/// bit-to-type mapping).
@available(iOS 15.0, macOS 12.0, macCatalyst 15.0, tvOS 15.0, *)
@_cdecl("SBW_MusicKitShims_CatalogSearchRequest_Init")
public func SBW_MusicKitShims_CatalogSearchRequest_Init(
    _ utf8Ptr: UnsafePointer<UInt8>?,
    _ utf8Len: Int,
    _ mask: UInt32,
    _ outBuffer: UnsafeMutableRawPointer
) -> Int32 {
    if mask & ~SBW_MUSICKIT_CATALOG_SEARCH_VALID_MASK != 0 { return -1 }
    let term = decodeUtf8Term(utf8Ptr, utf8Len)
    let types = decodeCatalogSearchableTypes(mask)
    let req = MusicCatalogSearchRequest(term: term, types: types)
    outBuffer.assumingMemoryBound(to: MusicCatalogSearchRequest.self).initialize(to: req)
    return 0
}

/// Initialize a `MusicLibrarySearchRequest(term:types:)` into the
/// caller-provided heap slot. See SBW_MUSICKIT_LIBRARY_SEARCH_VALID_MASK
/// for the bit-to-type mapping. Available on iOS 16 / macOS 14 /
/// macCatalyst 17 / tvOS 16+; older deployment targets cannot reach
/// this symbol even though it links into the wrapper.
@available(iOS 16.0, macOS 14.0, macCatalyst 17.0, tvOS 16.0, *)
@_cdecl("SBW_MusicKitShims_LibrarySearchRequest_Init")
public func SBW_MusicKitShims_LibrarySearchRequest_Init(
    _ utf8Ptr: UnsafePointer<UInt8>?,
    _ utf8Len: Int,
    _ mask: UInt32,
    _ outBuffer: UnsafeMutableRawPointer
) -> Int32 {
    if mask & ~SBW_MUSICKIT_LIBRARY_SEARCH_VALID_MASK != 0 { return -1 }
    let term = decodeUtf8Term(utf8Ptr, utf8Len)
    let types = decodeLibrarySearchableTypes(mask)
    let req = MusicLibrarySearchRequest(term: term, types: types)
    outBuffer.assumingMemoryBound(to: MusicLibrarySearchRequest.self).initialize(to: req)
    return 0
}

/// Initialize a
/// `MusicCatalogSearchSuggestionsRequest(term:includingTopResultsOfTypes:)`
/// into the caller-provided heap slot. The `includingTopResultsOfTypes`
/// array is decoded from the same MusicCatalogSearchable bit mapping —
/// a zero mask is legal and corresponds to the Swift API's documented
/// default of `[]`. Available on iOS 16 / macOS 13 / macCatalyst 16 /
/// tvOS 16+; older deployment targets cannot link this entry point even
/// if the binding is loaded.
@available(iOS 16.0, macOS 13.0, macCatalyst 16.0, tvOS 16.0, *)
@_cdecl("SBW_MusicKitShims_CatalogSearchSuggestionsRequest_Init")
public func SBW_MusicKitShims_CatalogSearchSuggestionsRequest_Init(
    _ utf8Ptr: UnsafePointer<UInt8>?,
    _ utf8Len: Int,
    _ topResultsMask: UInt32,
    _ outBuffer: UnsafeMutableRawPointer
) -> Int32 {
    if topResultsMask & ~SBW_MUSICKIT_CATALOG_SEARCH_VALID_MASK != 0 { return -1 }
    let term = decodeUtf8Term(utf8Ptr, utf8Len)
    let types = decodeCatalogSearchableTypes(topResultsMask)
    let req = MusicCatalogSearchSuggestionsRequest(term: term, includingTopResultsOfTypes: types)
    outBuffer.assumingMemoryBound(to: MusicCatalogSearchSuggestionsRequest.self).initialize(to: req)
    return 0
}
