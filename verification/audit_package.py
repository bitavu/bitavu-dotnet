"""Verify the release archive layout and scan every decompressed entry."""

import hashlib
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from zipfile import ZipFile


PACKAGE_ID = "Bitavu.Client"
FRAMEWORKS = {"net10.0", "net11.0"}
DESCRIPTION = "PDF generation for .NET. Generate PDFs from Bitavu templates and JSON data with this C# client."
TAGS = "bitavu pdf pdf-generation pdf-rendering reporting templates"
ICON = "icon.png"
# Require the reviewed 128 x 128 Bitavu icon, including its exact PNG metadata.
ICON_SHA256 = "9faa61b263c85db017338fc73c70669ffb357f13987e53d686dcbec1563af8ae"
CORE_PROPERTIES = "package/services/metadata/core-properties/nuget.psmdcp"
ENTRIES = {
    "[Content_Types].xml", "_rels/.rels", f"{PACKAGE_ID}.nuspec", CORE_PROPERTIES,
    "LICENSE", "README.md", ICON,
} | {
    f"lib/{framework}/{PACKAGE_ID}.{extension}"
    for framework in FRAMEWORKS for extension in ("dll", "xml")
}
PUBLIC_HOSTS = {
    "bitavu.com", "api.bitavu.com", "licenses.nuget.org", "schemas.microsoft.com",
    "schemas.openxmlformats.org", "purl.org", "www.w3.org",
}
PATTERNS = {
    "email address": r"\b[a-z\d._%+-]+@[a-z\d.-]+\.[a-z]{2,}\b",
    "machine path": r"(?:\b[a-z]:[\\/]|\\\\[a-z\d][a-z\d.-]*\\|/(?:home|users|mnt|workspace|var|tmp)/)",
    "local or non-public host": r"\b(?:localhost|127\.0\.0\.1|[\w.-]+\.(?:local|internal)|[\w.-]+-staging\.bitavu\.com)\b",
    "known token or secret format": r"(?:-----BEGIN [A-Z ]*PRIVATE KEY-----|\b(?:bk_(?:production|test)_[a-z\d_-]{43}|gh[pousr]_[a-z\d]{20,}|github_pat_[\w]{20,}|(?:sk|rk)_(?:live|test)_[a-z\d]{10,}|npm_[a-z\d]{20,}|AKIA[A-Z\d]{16}|eyJ[\w-]{8,}\.eyJ[\w-]{8,}\.[\w-]{8,}))",
    "UUID or commit ID": r"\b(?:[a-f\d]{8}-(?:[a-f\d]{4}-){3}[a-f\d]{12}|[a-f\d]{40})\b",
    "verification fixture": r"synthetic-(?:test-value|operation-001|mapping|padded-value)|synthetic_(?:private_value|document|uppercase_value)",
    "private development factory": r"\bCreateForLocalDevelopment\b",
}


def require(condition, message):
    if not condition:
        raise SystemExit(f"Package audit failed: {message}")


def values(element):
    return {node.tag.split("}")[-1]: (node.text or "").strip() for node in element}


def scan(name, contents):
    for kind, pattern in PATTERNS.items():
        require(not re.search(pattern, contents, re.IGNORECASE), f"{kind} in {name}")
    for host in re.findall(r"https?://([a-z\d.-]+)", contents, re.IGNORECASE):
        require(host.lower() in PUBLIC_HOSTS, f"unexpected URL in {name}")


def audit(path):
    with ZipFile(path) as package:
        names = package.namelist()
        require(len(names) == len(ENTRIES) and set(names) == ENTRIES, "archive entry list changed")
        require(not package.comment and all(not entry.comment and not entry.extra for entry in package.infolist()),
                "ZIP comments or extra fields present")
        require(package.testzip() is None, "corrupt archive")

        for name in sorted(ENTRIES):
            contents = package.read(name)
            if name == ICON:
                require(hashlib.sha256(contents).hexdigest() == ICON_SHA256, "unreviewed package icon")
            elif name.endswith(".dll"):
                # Scan the managed metadata and the UTF-16 string heap and resources.
                for match in re.finditer(rb"[ -~]{4,}|(?:[ -~]\x00){4,}", contents):
                    scan(name, match.group().replace(b"\x00", b"").decode("ascii"))
            else:
                scan(name, contents.decode("utf-8-sig"))

        nuspec = ET.fromstring(package.read(f"{PACKAGE_ID}.nuspec"))
        namespace = nuspec.tag.removesuffix("package")
        metadata = nuspec.find(f"{namespace}metadata")
        require(metadata is not None and len(nuspec) == 1 and not nuspec.attrib and not metadata.attrib,
                "unexpected NuGet manifest structure")
        version = values(metadata).get("version", "")
        require(re.fullmatch(r"\d+\.\d+\.\d+(?:-[a-z\d.]+)?", version), "unexpected version format")
        require(path.name == f"{PACKAGE_ID}.{version}.nupkg", "filename and package identity differ")
        require(len(metadata) == 11 and values(metadata) == {
            "id": PACKAGE_ID,
            "version": version,
            "authors": "bitavu.com",
            "license": "MIT",
            "licenseUrl": "https://licenses.nuget.org/MIT",
            "readme": "README.md",
            "icon": ICON,
            "projectUrl": "https://bitavu.com/",
            "description": DESCRIPTION,
            "tags": TAGS,
            "dependencies": "",
        }, "unexpected manifest metadata")
        license_node = metadata.find(f"{namespace}license")
        require(license_node.attrib == {"type": "expression"} and len(license_node) == 0,
                "unexpected license metadata")
        dependencies = metadata.find(f"{namespace}dependencies")
        require(not dependencies.attrib and len(dependencies) == len(FRAMEWORKS) and
                {group.get("targetFramework") for group in dependencies} == FRAMEWORKS,
                "unexpected package frameworks")
        require(all(group.tag == f"{namespace}group" and len(group.attrib) == 1 and len(group) == 0 and
                    not (group.text or "").strip() for group in dependencies), "unexpected package dependencies")
        require(all(not node.attrib and len(node) == 0 for node in metadata if node not in (license_node, dependencies)),
                "unexpected manifest fields")

        core_properties = ET.fromstring(package.read(CORE_PROPERTIES))
        require(len(core_properties) == 6 and not core_properties.attrib and
                all(not node.attrib and len(node) == 0 for node in core_properties), "unexpected core properties structure")
        properties = values(core_properties)
        tooling = properties.pop("lastModifiedBy", "")
        require(re.fullmatch(r"NuGet\.Packaging, Version=[\d.]+, Culture=neutral, PublicKeyToken=[a-f\d]{16};\.NET [\d.]+", tooling),
                "unexpected archive tooling attribution")
        require(properties == {
            "creator": "bitavu.com", "description": DESCRIPTION, "identifier": PACKAGE_ID,
            "version": version, "keywords": TAGS,
        }, "unexpected core properties")

    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    print(f"Audited {len(ENTRIES)} entries in {path.name}\nSHA-256 {digest}")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Usage: python verification/audit_package.py <package.nupkg>")
    audit(Path(sys.argv[1]))
