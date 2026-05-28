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
import re
import subprocess
import sys
from dataclasses import dataclass
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
class Guide:
    """A single discovered guide and the package metadata it documents."""

    page: str          # wiki page name, e.g. "StoreKit2"
    rel_path: str      # guide path relative to repo root (posix)
    body: str          # raw guide markdown
    package_id: str    # <PackageId> from the sibling csproj, or "" if none
    version: str       # <Version> from the sibling csproj, or "" if none
    group: str         # "apple-frameworks" | "libraries"


def _read_tag(text: str, tag: str) -> str:
    """Return the inner text of the first ``<tag>...</tag>``, or ""."""
    m = re.search(rf"<{tag}>([^<]+)</{tag}>", text)
    return m.group(1).strip() if m else ""


def discover_guides(repo_root: Path) -> list[Guide]:
    """Find every ``*-GUIDE.md`` under apple-frameworks/ and libraries/.

    The wiki page name is the guide's parent directory name (the package dir),
    and the sibling ``SwiftBindings.*.csproj`` in that same directory supplies
    the PackageId + Version stamped into the banner.
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
            package_id = ""
            version = ""
            csprojs = sorted(pkg_dir.glob("SwiftBindings.*.csproj"))
            if csprojs:
                csproj_text = csprojs[0].read_text(encoding="utf-8")
                package_id = _read_tag(csproj_text, "PackageId")
                version = _read_tag(csproj_text, "Version")
            else:
                print(
                    f"  warning: no SwiftBindings.*.csproj next to {rel} — "
                    "banner will omit package/version",
                    file=sys.stderr,
                )

            guides.append(
                Guide(
                    page=pkg_dir.name,
                    rel_path=rel.as_posix(),
                    body=guide_path.read_text(encoding="utf-8"),
                    package_id=package_id,
                    version=version,
                    group=group,
                )
            )
    return guides


def render_page(guide: Guide, repo_url: str) -> str:
    """Render one wiki page: version banner + source link, then the guide body."""
    blob = f"{repo_url}/blob/main/{guide.rel_path}"
    lines: list[str] = []
    if guide.package_id:
        nuget = f"https://www.nuget.org/packages/{guide.package_id}"
        version = f" · **Version** `{guide.version}`" if guide.version else ""
        lines.append(f"> **Package** [`{guide.package_id}`]({nuget}){version}")
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
            suffix = ""
            if g.package_id:
                suffix = f" — `{g.package_id}`"
                if g.version:
                    suffix += f" v{g.version}"
            # [[Page]] is GitHub-wiki link syntax; resolves to the page file.
            out.append(f"- [[{g.page}]]{suffix}")
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
        meta = f"{g.package_id} v{g.version}" if g.package_id else "(no csproj)"
        print(f"  {g.page:<22} {g.rel_path}  [{meta}]")

    write_wiki(guides, args.wiki_dir, args.repo_url)
    commit_and_push(args.wiki_dir, push=not args.no_push)
    return 0


if __name__ == "__main__":
    sys.exit(main())
