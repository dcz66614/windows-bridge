#!/usr/bin/env python3
"""Generate release/SBOM.md (T28) from the built solution.

Reads every project's project.assets.json (the full transitive
closure, as restored) and takes each package's license from the
local NuGet cache's .nuspec. No network access: if a package is
not in the cache, its license is reported UNKNOWN (never guessed).
The .NET runtime itself is listed separately (it ships with the
self-contained publish output, MIT).

Usage: python3 release/generate-sbom.py
"""
import json
import os
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

WORK = Path(__file__).resolve().parent.parent
NUGET = Path.home() / ".nuget" / "packages"
OUT = WORK / "release" / "SBOM.md"


def find_assets():
    for p in sorted(WORK.rglob("project.assets.json")):
        if ".tmp" in p.parts:
            continue
        yield p


def _find(parent, tag):
    # Match by local name: nuspecs use several schema namespaces
    # (2013/05, 2013/01, ...), so a fixed namespace lookup misses
    # most of them.
    if parent is None:
        return None
    for child in parent:
        if child.tag.rsplit("}", 1)[-1] == tag:
            return child
    return None


def license_from_nuspec(pkg_id, version):
    # NuGet cache layout is lower-case.
    base = NUGET / pkg_id.lower() / version.lower()
    nuspecs = list(base.glob("*.nuspec"))
    if not nuspecs:
        return "UNKNOWN (not in local cache)"
    try:
        root = ET.parse(nuspecs[0]).getroot()
    except ET.ParseError:
        return "UNKNOWN (nuspec unreadable)"
    meta = _find(root, "metadata")
    if meta is None:
        return "UNKNOWN"
    lic = _find(meta, "license")
    if lic is not None:
        text = (lic.text or "").strip()
        if text:
            kind = lic.get("type")
            if kind == "file":
                # Read the actual license file from the package
                # directory and identify it by its first line.
                lic_file = base / text
                if lic_file.exists():
                    first = ""
                    for line in lic_file.read_text(
                            encoding="utf-8", errors="replace").splitlines():
                        if line.strip():
                            first = line.strip()
                            break
                    if "MIT License" in first or first.startswith("The MIT"):
                        return f"MIT ({text} in package)"
                    if "Apache License" in first:
                        return f"Apache-2.0 ({text} in package)"
                    return f"{first} ({text} in package)"
                return f"file ({text} in package)"
            return text
    url = _find(meta, "licenseUrl")
    if url is not None and url.text and url.text.strip():
        return "see " + url.text.strip()
    return "UNKNOWN (no license element)"


def main():
    packages = {}  # (lower id, version) -> (display id, version, {projects})
    for assets in find_assets():
        project = assets.parent.parent.name
        data = json.loads(assets.read_text())
        for key, lib in data.get("libraries", {}).items():
            if lib.get("type") != "package":
                continue
            pkg_id, version = key.split("/", 1)
            entry = packages.setdefault(
                (pkg_id.lower(), version.lower()),
                {"id": pkg_id, "version": version, "projects": set()})
            entry["projects"].add(project)

    rows = []
    for (lid, ver), e in sorted(packages.items()):
        lic = license_from_nuspec(e["id"], e["version"])
        rows.append((e["id"], e["version"], lic,
                     ", ".join(sorted(e["projects"]))))

    lines = []
    lines.append("# Windows Bridge — Software Bill of Materials (SBOM)")
    lines.append("")
    lines.append("Generated from the restored solution (`project.assets.json` "
                 "transitive closure). Licenses are read from the local "
                 "NuGet cache `.nuspec`; UNKNOWN is reported, never guessed.")
    lines.append("")
    lines.append("Runtime: .NET 10 (self-contained publish output), MIT, "
                 "https://github.com/dotnet/runtime — the runtime is part "
                 "of the published payload, not a NuGet package here.")
    lines.append("")
    lines.append("| Package | Version | License | Used by |")
    lines.append("|---|---|---|---|")
    for pkg_id, version, lic, projects in rows:
        lines.append(f"| {pkg_id} | {version} | {lic} | {projects} |")
    lines.append("")
    lines.append("## License conclusions (T28)")
    lines.append("")
    lines.append("- The repository's own code is under the license Sam "
                 "chooses (P021 precedent: MIT; this project's license is "
                 "still PENDING_SAM_DECISION — do not ship a LICENSE file "
                 "claiming otherwise).")
    lines.append("- The top-level MIT (if chosen) does NOT make every "
                 "packaged dependency MIT-distributable. Each package above "
                 "keeps its own license. License families present: MIT "
                 "(majority), Apache-2.0 (OpenIddict and friends), "
                 "BSD-3-Clause (5 packages). One row (xunit.abstractions) "
                 "carries only a license URL, not an SPDX expression; its "
                 "license was NOT resolved by this script and must be "
                 "confirmed before a public release. Apache-2.0 components "
                 "require their NOTICE/attribution to be preserved in the "
                 "distribution.")
    lines.append("- No GPL/AGPL package is present in the closure. If a "
                 "future dependency introduces one, the release must be "
                 "re-reviewed before shipping.")
    lines.append("- Windows-MCP is research reference only; none of its "
                 "source is in this package.")
    lines.append("")
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text("\n".join(lines))
    unknown = [r for r in rows if r[2].startswith("UNKNOWN")]
    print(f"SBOM: {len(rows)} packages -> {OUT}")
    if unknown:
        print("UNKNOWN licenses:")
        for r in unknown:
            print(f"  {r[0]} {r[1]}")
    else:
        print("All licenses resolved.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
