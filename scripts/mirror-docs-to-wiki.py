#!/usr/bin/env python3
"""Mirror per-package usage guides (``*-GUIDE.md``) into the repo's GitHub wiki.

The source of truth for every guide lives in the repo, next to the package it
documents (e.g. ``apple-frameworks/StoreKit2/STOREKIT2-GUIDE.md``). This tool
renders each guide into a wiki page named after the package directory
(``StoreKit2``), stamps a version banner read from the sibling
``SwiftBindings.*.csproj``, regenerates ``Home.md`` + ``_Sidebar.md``, and
commits/pushes the wiki — but ONLY when the rendered content actually changed,
so docs-unrelated pushes never produce empty wiki commits.

Why a plain Python script and not a Nuke target: this is pure file + git
plumbing with no build step, and it runs on a cheap Linux runner. Invoking
``dotnet nuke`` on Linux triggers MSBuild workload-manifest evaluation of the
build project (the same reason ``release.yml``'s publish job pushes nupkgs with
raw ``dotnet nuget push`` instead of a Nuke target). Keeping this as stdlib +
``git`` means no .NET, no workloads, and it's runnable/testable locally against
a throwaway wiki checkout.

Usage:
    # CI: render, commit, and push to a wiki checkout
    scripts/mirror-docs-to-wiki.py --wiki-dir ./wiki

    # Local dry-run: render into a scratch dir, show the diff, don't push
    scripts/mirror-docs-to-wiki.py --wiki-dir /tmp/wiki-test --no-push
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

# Default repo URL used to build "edit the source" links back into the repo.
# Overridable via --repo-url; the GitHub remote is not assumed to be parseable
# (the wiki checkout's remote points at the wiki, not the code repo).
DEFAULT_REPO_URL = "https://github.com/justinwojo/swift-dotnet-packages"

# Directory parts that mark build output / test apps — never a published guide.
EXCLUDED_PARTS = {"bin", "obj", "tests"}

# Wiki pages this tool fully owns and regenerates on every run.
HOME_PAGE = "Home.md"
SIDEBAR_PAGE = "_Sidebar.md"


@dataclass
class Package:
    """One NuGet package a guide documents, read from its csproj."""

    package_id: str    # <PackageId>, e.g. "SwiftBindings.Stripe.Core"
    version: str       # displayed version, or "" when none is known


@dataclass
class Guide:
    """A single discovered guide and the package metadata it documents.

    ``packages`` is a list because a vendor-root guide (e.g.
    ``libraries/Stripe/STRIPE-GUIDE.md``) documents every product package in
    the library, not one. Single-package guides carry exactly one entry.

    ``version_label`` distinguishes the two version trains the wiki shows: an
    Apple framework package's own NuGet ``<Version>`` versus a third-party
    binding's *upstream* pin (see ``_upstream_version``).
    """

    page: str          # wiki page name, e.g. "StoreKit2"
    rel_path: str      # guide path relative to repo root (posix)
    body: str          # raw guide markdown
    group: str         # "apple-frameworks" | "libraries"
    packages: list[Package] = field(default_factory=list)
    version_label: str = "Version"   # "Version" (NuGet) | "Upstream" (library.json)

    @property
    def uniform_version(self) -> str:
        """The shared version if every package agrees on a non-empty one, else "".

        Vendor libraries are released as one wave at a single version, so the
        common case is a single version stamped once rather than repeated N
        times. A mid-wave tree (or a hand-edited csproj) breaks that, and the
        renderers fall back to per-package versions.
        """
        versions = {p.version for p in self.packages}
        if len(versions) == 1:
            return next(iter(versions))
        return ""


def _read_tag(text: str, tag: str) -> str:
    """Return the inner text of the first ``<tag>...</tag>``, or ""."""
    m = re.search(rf"<{tag}>([^<]+)</{tag}>", text)
    return m.group(1).strip() if m else ""


def _read_package(csproj: Path) -> Package:
    """Read PackageId + Version out of one csproj."""
    text = csproj.read_text(encoding="utf-8")
    return Package(
        package_id=_read_tag(text, "PackageId"),
        version=_read_tag(text, "Version"),
    )


def _load_library_json(directory: Path) -> dict:
    """Parse ``library.json`` in ``directory``; ``{}`` when absent or malformed.

    Guides are docs, not builds — a library without a config (or with one this
    tool can't parse) still deserves a rendered page, so every caller degrades
    to "no metadata" rather than failing the mirror.
    """
    config = directory / "library.json"
    if not config.is_file():
        return {}
    try:
        data = json.loads(config.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        print(f"  warning: unreadable {config}: {exc}", file=sys.stderr)
        return {}
    return data if isinstance(data, dict) else {}


def _upstream_version(pkg_dir: Path, group_dir: Path) -> str:
    """The upstream pin from ``library.json``, or "" if there isn't one.

    Third-party guides are stamped with the version of the *upstream* library
    they bind, not the csproj's ``<Version>``. Release waves pass the NuGet
    version as ``/p:Version`` and never rewrite the csproj, so csproj versions
    drift stale (Kingfisher's says 1.0.0 while 8.11.0 is published), whereas
    ``library.json``'s ``version`` is committed truth — and it is what a doc
    reader actually wants to know the guide describes. Parents are searched so
    a guide living inside a product subdirectory still finds the vendor root's
    config.
    """
    directory = pkg_dir
    while True:
        version = _load_library_json(directory).get("version")
        if isinstance(version, str) and version.strip():
            return version.strip()
        if directory == group_dir or directory.parent == directory:
            return ""
        directory = directory.parent


def _internal_subdirs(pkg_dir: Path) -> set[str]:
    """Subdirectory names of products that ship no NuGet package.

    Products flagged ``"internal": true`` in ``library.json`` are built as
    xcframeworks purely so sibling products can link against them; ``Pack``
    skips them, so they must not appear in a vendor-root guide's banner. No
    product carries the flag today (Stripe's ``Stripe3DS2`` /
    ``StripeCameraCore`` do ship as real packages) — this keeps the banner
    honest if one ever does. The directory is the product's ``subdirectory``
    when set, else its ``framework`` name — the same resolution the Nuke
    harness uses when laying out xcframeworks.
    """
    internal: set[str] = set()
    products = _load_library_json(pkg_dir).get("products")
    if not isinstance(products, list):
        return internal
    for product in products:
        if not isinstance(product, dict):
            continue
        if product.get("internal"):
            subdir = product.get("subdirectory") or product.get("framework") or ""
            if subdir:
                internal.add(subdir)
    return internal


def _collect_packages(pkg_dir: Path) -> list[Package]:
    """Collect the packages a guide in ``pkg_dir`` documents.

    Two layouts exist. A single-package directory holds the guide next to its
    own ``SwiftBindings.*.csproj``. A multi-product vendor root (Stripe,
    Facebook) holds the guide next to ``library.json``, with one csproj per
    product subdirectory — so when the guide's own directory has no csproj, the
    immediate subdirectories supply them (minus internal products and build /
    test output).
    """
    own = sorted(pkg_dir.glob("SwiftBindings.*.csproj"))
    if own:
        return [_read_package(own[0])]

    skip = EXCLUDED_PARTS | _internal_subdirs(pkg_dir)
    packages: list[Package] = []
    for child in sorted(p for p in pkg_dir.iterdir() if p.is_dir()):
        if child.name in skip:
            continue
        for csproj in sorted(child.glob("SwiftBindings.*.csproj")):
            packages.append(_read_package(csproj))
    # Sorted by package id, not by directory name, so the banner order doesn't
    # depend on vendor subdirectory naming (Stripe's dirs and package ids don't
    # sort the same way).
    return sorted((p for p in packages if p.package_id), key=lambda p: p.package_id)


def discover_guides(repo_root: Path) -> list[Guide]:
    """Find every ``*-GUIDE.md`` under apple-frameworks/ and libraries/.

    The wiki page name is the guide's parent directory name (the package dir
    or the vendor root), and the csproj(s) that directory owns supply the
    PackageIds stamped into the banner. The version comes from the csproj for
    Apple frameworks (whose ``<Version>`` is the shipped identity) but from
    ``library.json`` for third-party libraries, where the interesting number is
    the upstream pin and the csproj version is stale by construction.
    """
    guides: list[Guide] = []
    for group in ("apple-frameworks", "libraries"):
        group_dir = repo_root / group
        if not group_dir.is_dir():
            continue
        for guide_path in sorted(group_dir.rglob("*-GUIDE.md")):
            rel = guide_path.relative_to(repo_root)
            if EXCLUDED_PARTS.intersection(rel.parts):
                continue

            pkg_dir = guide_path.parent
            packages = _collect_packages(pkg_dir)
            if not packages:
                print(
                    f"  warning: no SwiftBindings.*.csproj next to {rel} — "
                    "banner will omit package/version",
                    file=sys.stderr,
                )

            version_label = "Version"
            if group == "libraries":
                # Re-stamp every package with the library's upstream pin; ""
                # (no library.json / no version field) simply drops the version
                # from the banner and index entry.
                version_label = "Upstream"
                upstream = _upstream_version(pkg_dir, group_dir)
                packages = [Package(p.package_id, upstream) for p in packages]

            guides.append(
                Guide(
                    page=pkg_dir.name,
                    rel_path=rel.as_posix(),
                    body=guide_path.read_text(encoding="utf-8"),
                    group=group,
                    packages=packages,
                    version_label=version_label,
                )
            )
    return guides


def _nuget_link(package: Package) -> str:
    """Markdown link to a package's nuget.org page."""
    return f"[`{package.package_id}`](https://www.nuget.org/packages/{package.package_id})"


def _banner_lines(guide: Guide) -> list[str]:
    """The blockquote line(s) naming the package(s) the guide documents.

    The single-package rendering is kept byte-identical to the original
    one-line form so existing wiki pages don't churn. Multi-package (vendor
    root) guides get a count + a comma-joined link list on a continuation line;
    consecutive blockquote lines render as one paragraph, so the banner stays
    one readable wrapped line even with a dozen Stripe packages behind it.
    """
    packages = [p for p in guide.packages if p.package_id]
    if not packages:
        return []
    label = guide.version_label
    if len(packages) == 1:
        package = packages[0]
        version = f" · **{label}** `{package.version}`" if package.version else ""
        return [f"> **Package** {_nuget_link(package)}{version}"]

    uniform = guide.uniform_version
    head = f"> **Packages** ({len(packages)})"
    if uniform:
        head += f" · **{label}** `{uniform}`"
        listed = [_nuget_link(p) for p in packages]
    else:
        listed = [
            f"{_nuget_link(p)} `{p.version}`" if p.version else _nuget_link(p)
            for p in packages
        ]
    return [head, "> — " + ", ".join(listed)]


def render_page(guide: Guide, repo_url: str) -> str:
    """Render one wiki page: version banner + source link, then the guide body."""
    blob = f"{repo_url}/blob/main/{guide.rel_path}"
    lines: list[str] = _banner_lines(guide)
    lines.append(f"> _Auto-published from [`{guide.rel_path}`]({blob})._")
    lines.append("")
    lines.append("---")
    return "\n".join(lines) + "\n\n" + guide.body.lstrip("\n")


def render_home(guides: list[Guide]) -> str:
    """Render the wiki Home page: intro + grouped index of every guide."""
    out = [
        "# SwiftBindings Usage Guides",
        "",
        "In-depth usage guides for the [SwiftBindings](https://www.nuget.org/profiles/justinwojo) "
        ".NET packages — native Swift/Apple-framework bindings for .NET on Apple platforms.",
        "",
        "> These guides describe the **.NET binding surface**. For full API "
        "semantics and platform behavior, also consult the native Apple / "
        "third-party documentation linked inside each guide.",
        "",
        "_Pages are auto-generated from the guide that ships in the repo next to "
        "each package; edit the source there, not the wiki._",
        "",
    ]
    out += _index_section(guides)
    return "\n".join(out) + "\n"


def render_sidebar(guides: list[Guide]) -> str:
    """Render the wiki sidebar: Home link + grouped page list."""
    out = ["**[Home](Home)**", ""]
    out += _index_section(guides)
    return "\n".join(out) + "\n"


def _common_package_prefix(package_ids: list[str]) -> str:
    """Longest common *dotted* prefix of package ids, e.g. "SwiftBindings.Stripe".

    Dotted rather than character-wise so the wildcard form lands on a real id
    boundary: ``SwiftBindings.Stripe.Core`` + ``SwiftBindings.Stripe.CardScan``
    yields ``SwiftBindings.Stripe``, not ``SwiftBindings.Stripe.Ca``.
    """
    split = [pid.split(".") for pid in package_ids]
    common: list[str] = []
    for parts in zip(*split):
        if len(set(parts)) != 1:
            break
        common.append(parts[0])
    return ".".join(common)


def _index_version(guide: Guide, version: str) -> str:
    """Index-entry spelling of a version: ``v26.2.9`` or ``upstream 13.0.6``.

    The index is a dense one-line-per-guide list, so the upstream train is
    marked with a word rather than the bare ``v`` prefix an Apple package's own
    NuGet version carries — otherwise the two trains read as the same number.
    """
    if guide.version_label == "Upstream":
        return f"upstream {version}"
    return f"v{version}"


def _index_suffix(guide: Guide) -> str:
    """The `` — `pkg` vX`` tail after a guide's index entry.

    Single-package Apple entries render exactly as they always have. A
    vendor-root guide would otherwise spill a dozen ids into a sidebar line, so
    it collapses to a wildcard over the packages' common prefix, qualified by
    the shared version when there is one and by a package count when there
    isn't.
    """
    packages = [p for p in guide.packages if p.package_id]
    if not packages:
        return ""
    if len(packages) == 1:
        suffix = f" — `{packages[0].package_id}`"
        if packages[0].version:
            suffix += f" {_index_version(guide, packages[0].version)}"
        return suffix

    prefix = _common_package_prefix([p.package_id for p in packages])
    if not prefix:
        return f" — {len(packages)} packages"
    suffix = f" — `{prefix}.*`"
    uniform = guide.uniform_version
    if uniform:
        return f"{suffix} {_index_version(guide, uniform)}"
    return f"{suffix} ({len(packages)} packages)"


def _index_section(guides: list[Guide]) -> list[str]:
    """Shared grouped index used by both Home.md and _Sidebar.md."""
    headings = [("apple-frameworks", "Apple Frameworks"), ("libraries", "Libraries")]
    out: list[str] = []
    for group, heading in headings:
        in_group = [g for g in guides if g.group == group]
        if not in_group:
            continue
        out.append(f"### {heading}")
        for g in sorted(in_group, key=lambda x: x.page.lower()):
            # [[Page]] is GitHub-wiki link syntax; resolves to the page file.
            out.append(f"- [[{g.page}]]{_index_suffix(g)}")
        out.append("")
    return out


def write_wiki(guides: list[Guide], wiki_dir: Path, repo_url: str) -> None:
    """Render all pages + Home + Sidebar into the wiki working directory."""
    wiki_dir.mkdir(parents=True, exist_ok=True)
    for guide in guides:
        (wiki_dir / f"{guide.page}.md").write_text(
            render_page(guide, repo_url), encoding="utf-8"
        )
    (wiki_dir / HOME_PAGE).write_text(render_home(guides), encoding="utf-8")
    (wiki_dir / SIDEBAR_PAGE).write_text(render_sidebar(guides), encoding="utf-8")


def _git(wiki_dir: Path, *args: str, check: bool = True) -> subprocess.CompletedProcess:
    return subprocess.run(
        ["git", "-C", str(wiki_dir), *args],
        check=check,
        capture_output=True,
        text=True,
    )


def commit_and_push(wiki_dir: Path, push: bool) -> bool:
    """Stage the wiki, and commit+push only if something changed.

    Returns True if a commit was made, False if the wiki was already up to date.
    This is the "don't deploy when nothing changed" guarantee: an empty staged
    diff short-circuits before any commit or push.
    """
    _git(wiki_dir, "add", "-A")
    status = _git(wiki_dir, "status", "--porcelain").stdout.strip()
    if not status:
        print("Wiki already up to date — no changes to commit.")
        return False

    print("Changed wiki pages:")
    for line in status.splitlines():
        print(f"  {line}")

    _git(
        wiki_dir,
        "-c", "user.name=github-actions[bot]",
        "-c", "user.email=41898282+github-actions[bot]@users.noreply.github.com",
        "commit", "-m", "docs: sync usage guides from repo",
    )
    if push:
        _git(wiki_dir, "push")
        print("Pushed updated guides to the wiki.")
    else:
        print("--no-push: committed locally, skipping push.")
    return True


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--wiki-dir", required=True, type=Path,
        help="Path to a checkout of the <repo>.wiki.git repository.",
    )
    parser.add_argument(
        "--repo-root", type=Path, default=Path(__file__).resolve().parents[1],
        help="Repo root to scan for guides (default: parent of scripts/).",
    )
    parser.add_argument(
        "--repo-url", default=DEFAULT_REPO_URL,
        help="Base GitHub URL used for 'edit the source' links.",
    )
    parser.add_argument(
        "--no-push", action="store_true",
        help="Render and commit locally but do not push (local testing).",
    )
    args = parser.parse_args()

    guides = discover_guides(args.repo_root)
    if not guides:
        print("No *-GUIDE.md files found — nothing to mirror.")
        return 0

    print(f"Discovered {len(guides)} guide(s):")
    for g in guides:
        if not g.packages:
            meta = "(no csproj)"
        elif len(g.packages) == 1:
            meta = g.packages[0].package_id
            if g.packages[0].version:
                meta += f" {_index_version(g, g.packages[0].version)}"
        else:
            uniform = g.uniform_version
            meta = f"{len(g.packages)} packages"
            if uniform:
                meta += f" {_index_version(g, uniform)}"
        print(f"  {g.page:<22} {g.rel_path}  [{meta}]")

    write_wiki(guides, args.wiki_dir, args.repo_url)
    commit_and_push(args.wiki_dir, push=not args.no_push)
    return 0


if __name__ == "__main__":
    sys.exit(main())
