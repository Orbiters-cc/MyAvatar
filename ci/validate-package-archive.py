#!/usr/bin/env python3
import sys
import zipfile
from pathlib import PurePosixPath


# Painter~ holds the Substance 3D Painter plugin My Avatar installs (Unity skips folders ending in "~").
PACKAGE_ROOTS = ("Editor", "Runtime", "Painter~")
ROOT_FILES = ("README.md", "package.json")
FORBIDDEN_ROOTS = (".git", ".github", ".idea", ".agents", ".codex")


def validate_zip_name(name):
    normalized = name.replace("\\", "/")
    parts = PurePosixPath(normalized).parts
    if normalized != name:
        return "uses backslashes"
    if not parts:
        return "is empty"
    if normalized.startswith("/"):
        return "is absolute"
    if any(part in ("", ".", "..") for part in parts):
        return "contains an invalid path segment"
    return None


def is_allowed_package_file(name):
    if name.endswith(".meta"):
        return False

    if name in ROOT_FILES:
        return True

    parts = PurePosixPath(name).parts
    if parts[0] in PACKAGE_ROOTS:
        return True

    return False


def main():
    if len(sys.argv) != 2:
        raise SystemExit("Usage: validate-package-archive.py <package.zip>")

    zip_path = sys.argv[1]
    with zipfile.ZipFile(zip_path) as archive:
        names = sorted(name for name in archive.namelist() if not name.endswith("/"))

    if not names:
        raise SystemExit("Package archive is empty.")

    failures = []
    for name in names:
        invalid_reason = validate_zip_name(name)
        if invalid_reason:
            failures.append(f"{name}: {invalid_reason}")
            continue

        root = PurePosixPath(name).parts[0]
        if root in FORBIDDEN_ROOTS:
            failures.append(f"{name}: forbidden repository/internal path")
            continue

        if not is_allowed_package_file(name):
            failures.append(f"{name}: not in package allowlist")

    required_myavatar_files = (
        "Runtime/Orbiters.MyAvatar.asmdef", "Runtime/MyAvatar.cs",
        "Editor/Orbiters.MyAvatar.Editor.asmdef", "Editor/MyAvatarEditor.cs",
        "Editor/TextureImport.cs", "Editor/TextureMatching.cs", "Editor/TextureAi.cs",
        "Editor/TextureChanges.cs", "Editor/MyAvatarResults.cs",
        "Editor/UI/DropZone.cs", "Editor/UI/AccessoriesSection.cs", "Editor/AccessoryService.cs", "Editor/TextureOptimization.cs", "Editor/UI/MyAvatarLogo.svg.txt",
        "Editor/UI/myavatar.uss", "Editor/AvatarThumbnail.cs", "Editor/VrcSdkThumbnail.cs",
        "Editor/UI/ThumbnailSection.cs", "Editor/UI/VrcAvatarCard.cs",
        # Installed into Substance 3D Painter by the Painter card (left out of the allowlist until 0.9.12).
        "Painter~/orbiters_my_avatar.py", "Editor/Painter/PainterPack.shader",
    )
    for required_file in ROOT_FILES + required_myavatar_files:
        if required_file not in names:
            failures.append(f"{required_file}: required root file missing")

    for required_root in PACKAGE_ROOTS:
        if not any(name.startswith(required_root + "/") for name in names):
            failures.append(f"{required_root}/: required package root missing")

    if failures:
        print("Package archive validation failed:")
        for failure in failures:
            print(" - " + failure)
        raise SystemExit(1)

    print(f"Package archive validation passed for {len(names)} files.")


if __name__ == "__main__":
    main()
