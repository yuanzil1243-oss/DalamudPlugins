"""Verify separate stable/testing packages and each channel's actual source provenance."""
from pathlib import Path, PurePosixPath
import hashlib, io, json, subprocess, tarfile, zipfile

ROOT = Path(__file__).resolve().parents[1]
RAW = "https://raw.githubusercontent.com/yuanzil1243-oss/DalamudPlugins/main/"

def require(ok, message):
    if not ok:
        raise AssertionError(message)

def digest(data):
    return hashlib.sha256(data).hexdigest()

def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))

def safe_path(name):
    path = PurePosixPath(name)
    require(not path.is_absolute() and ".." not in path.parts and ":" not in name and "\\" not in name,
            "Unsafe path: " + name)
    return path

feed = read_json(ROOT / "pluginmaster.json")
require(isinstance(feed, list), "Feed must be a list")
identities = [x["InternalName"] for x in feed]
require(len(identities) == len(set(identities)), "Duplicate plugin identity")
channels = read_json(ROOT / "dist/CHANNELS.json")
require(set(channels) == set(identities), "Each plugin requires its own channel provenance")

for entry in feed:
    identity = entry["InternalName"]
    safe_path(identity)
    require(entry["ApplicableVersion"] == "any", "ApplicableVersion mismatch")
    require(isinstance(entry["LastUpdate"], int) and entry["LastUpdate"] > 0, "Invalid LastUpdate")
    require(entry["RepoUrl"] == "https://github.com/yuanzil1243-oss/DalamudPlugins", "Wrong repository")
    if identity == "NewFolder3":
        require(entry["Author"] == "John Smith / Yuzi", "Original author attribution must be retained")
    for channel, config in channels[identity].items():
        require(channel in ("stable", "testing"), "Unknown channel")
        testing = channel == "testing"
        info_path = ROOT / safe_path(config["buildInfo"])
        info = read_json(info_path)
        package = info_path.parent / safe_path(info["package"]["file"])
        version_key = "TestingAssemblyVersion" if testing else "AssemblyVersion"
        api_key = "TestingDalamudApiLevel" if testing else "DalamudApiLevel"
        require(info["version"] == entry[version_key] and entry[api_key] == 15, "Version/API mismatch")
        if testing:
            require(tuple(map(int, entry[version_key].split("."))) > tuple(map(int, entry["AssemblyVersion"].split("."))),
                    "Testing version must be newer")
            require(entry["IsTestingExclusive"] is False, "Stable channel must remain available")
        expected_url = RAW + package.relative_to(ROOT).as_posix()
        for key in (("DownloadLinkTesting",) if testing else ("DownloadLinkInstall", "DownloadLinkUpdate")):
            require(entry[key] == expected_url, "Wrong download URL: " + key)
        payload = package.read_bytes()
        require(digest(payload) == info["package"]["sha256"] and len(payload) == info["package"]["bytes"], "Package hash/size mismatch")
        if "preservedSha256" in config:
            require(digest(payload) == config["preservedSha256"], "Preserved stable package changed")
        with zipfile.ZipFile(io.BytesIO(payload)) as z:
            names = z.namelist()
            require(z.testzip() is None and len(names) == len(set(names)), "ZIP CRC/duplicate failure")
            for name in names:
                safe_path(name)
            require(not any(name.endswith((".pdb", ".jsonl", ".raw", ".sqlite3")) for name in names), "Private/debug data in package")
            require(set(names) == set(info["package"]["files"]), "ZIP inventory mismatch")
            manifest = json.loads(z.read(identity + ".json"))
            for key in ("Author", "Name", "Punchline", "Description", "InternalName", "ApplicableVersion", "RepoUrl", "IconUrl"):
                require(manifest[key] == entry[key], "Feed/package identity mismatch: " + key)
            require(manifest["AssemblyVersion"] == entry[version_key] and manifest["DalamudApiLevel"] == entry[api_key], "Feed/package version mismatch")
            if identity == "NewFolder3":
                require({n for n in names if n.endswith(".dll")} == {
                    "NewFolder3.dll", "DeepDungeon.Fsd.Core.dll", "DeepDungeon.Fsd.Runtime.dll", "DeepDungeon.Fsd.Dalamud.dll",
                    "OmenTools.dll", "GuerrillaNtp.dll", "TinyPinyin.dll"}, "Wrong seven-DLL dependency set")
            for name, sha in info["package"]["files"].items():
                require(digest(z.read(name)) == sha, "ZIP member hash mismatch: " + name)
            internal = {}
            for line in z.read("SHA256SUMS.txt").decode("utf-8-sig").splitlines():
                sha, name = line.split("  ", 1)
                require(name not in internal, "Duplicate internal checksum")
                internal[name] = sha
                require(digest(z.read(name)) == sha, "Internal checksum mismatch: " + name)
            require(set(internal) == set(names) - {"SHA256SUMS.txt"}, "Incomplete internal checksum inventory")
        sums = {}
        for line in (info_path.parent / "SHA256SUMS.txt").read_text(encoding="utf-8-sig").splitlines():
            sha, name = line.split("  ", 1)
            safe_path(name)
            sums[name] = sha
            require(digest((info_path.parent / name).read_bytes()) == sha, "Distribution checksum mismatch: " + name)
        require(set(sums) == {package.name, info_path.name}, "Distribution checksum inventory mismatch")
        revision = config["sourceCommit"]
        source_files = info["sourceFiles"]
        if revision == "working-tree":
            def source_bytes(name):
                return (ROOT / safe_path(name)).read_bytes()
        else:
            # A stable package must be verified against its published source commit,
            # never against a newer testing tree. CI fetches full history explicitly.
            archived = subprocess.check_output(["git", "archive", revision, identity], cwd=ROOT)
            with tarfile.open(fileobj=io.BytesIO(archived)) as archive:
                source = {m.name: archive.extractfile(m).read() for m in archive.getmembers() if m.isfile()}
            def source_bytes(name):
                safe_path(name)
                return source[name]
        for name, sha in source_files.items():
            require(digest(source_bytes(name)) == sha, "Source provenance mismatch: " + name)
        require(json.loads(source_bytes(identity + "/" + identity + ".json")) == manifest, "Package/source manifest mismatch")
        print(f"PASS {identity} {channel} {info['version']}: identity/API, ZIP, seven DLLs, checksums, source provenance")

require(read_json(ROOT / "NewFolder3/NewFolder3-release.json") == feed, "Source feed mismatch")
print("PASS: stable and opt-in testing remain separate; no plugin-count restriction")
