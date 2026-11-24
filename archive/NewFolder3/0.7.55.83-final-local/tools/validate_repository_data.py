"""Check public map inventory, field allowlists, pinned hashes and original signatures."""
from pathlib import Path, PurePosixPath
import base64, hashlib, json, math, re, subprocess, tempfile

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / "data/NewFolder3"

def require(ok, message):
    if not ok:
        raise AssertionError(message)

def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "Duplicate JSON field: " + key)
        result[key] = value
    return result

def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"), object_pairs_hook=unique_object)

def fields(value, required, optional=()):
    require(isinstance(value, dict) and set(required) <= set(value) <= set(required) | set(optional),
            "Unexpected public-data fields")

def digest(data):
    return hashlib.sha256(data).hexdigest()

def scope(value, scenario, territory, floors):
    require(value["schemaVersion"] == 1 and value["scenarioKey"] == scenario and
            value["territoryId"] == territory and value["floors"] == floors, "Map scope mismatch")

def position(value):
    fields(value, ("x", "y", "z"))
    require(all(type(n) in (int, float) and math.isfinite(n) for n in value.values()), "Invalid coordinates")

manifest = read(DATA / "manifest.json")
fields(manifest, ("schemaVersion", "exportDate", "coordinateDataVersion", "detailedScenarios", "files"))
require(manifest["schemaVersion"] == 1 and manifest["detailedScenarios"] == ["pt-21-30", "pt-31-40"], "Unsupported map manifest")
contract = (ROOT / "NewFolder3/shared/DeepDungeon.Fsd/Core/RepositoryPalacePalContract.cs").read_text(encoding="utf-8-sig")
version = re.search(r'DataVersion = "([0-9]+)"', contract)[1]
pinned_sha = re.search(r'Sha256 = "([0-9a-f]{64})"', contract)[1]
require(manifest["coordinateDataVersion"] == version, "Coordinate version mismatch")
coordinate_name = "palacepal/" + version + ".json"
expected = {coordinate_name}
for scenario in manifest["detailedScenarios"]:
    expected.add("detailed/" + scenario + "/latest.json")
    latest = read(DATA / "detailed" / scenario / "latest.json")
    fields(latest, ("schemaVersion", "scenarioKey", "releaseId", "catalogPath", "signaturePath", "catalogSha256", "modelSha256"))
    release = latest["releaseId"]
    require(re.fullmatch(r"[a-zA-Z0-9._-]{1,80}", release) is not None, "Unsafe release name")
    expected.update("detailed/" + scenario + "/" + release + "/" + n for n in
                    ("catalog.json", "catalog.sig.json", "hoard-yield-catalog.json"))
require(set(manifest["files"]) == expected, "Unexpected public inventory")
require({p.relative_to(DATA).as_posix() for p in DATA.rglob("*") if p.is_file()} == expected | {"manifest.json", "README.md"},
        "Private or unlisted file in public data directory")
for name, metadata in manifest["files"].items():
    path = PurePosixPath(name)
    require(not path.is_absolute() and ".." not in path.parts and ":" not in name and "\\" not in name, "Unsafe data path")
    fields(metadata, ("bytes", "sha256"))
    raw = (DATA / name).read_bytes()
    require(len(raw) == metadata["bytes"] and digest(raw) == metadata["sha256"], "Public artifact hash mismatch: " + name)

raw = (DATA / coordinate_name).read_bytes()
require(0 < len(raw) <= 512 * 1024 and digest(raw) == pinned_sha, "Client coordinate pin mismatch")
coordinates = read(DATA / coordinate_name)
fields(coordinates, ("schemaVersion", "dataVersion", "territories"))
require(coordinates["schemaVersion"] == 1 and coordinates["dataVersion"] == version and
        set(coordinates["territories"]) == {str(n) for n in range(1281, 1291)}, "Incomplete coordinate scope")
counts = {"Trap": 0, "Hoard": 0}
for territory, markers in coordinates["territories"].items():
    require(0 < len(markers) <= 10000, "Invalid marker count")
    for marker in markers:
        fields(marker, ("t", "x", "y", "z"))
        require(marker["t"] in counts, "Unexpected marker type")
        position({k: marker[k] for k in ("x", "y", "z")})
        require(territory != "1283" or max(abs(marker[k]) for k in ("x", "y", "z")) <= 1000, "Quarantined 21-30 marker")
        counts[marker["t"]] += 1

trust = (ROOT / "NewFolder3/shared/DeepDungeon.Fsd/Core/DetailedMapCatalogTrust.cs").read_text(encoding="utf-8-sig")
public_key = base64.b64decode(re.search(r'SubjectPublicKeyInfoBase64 =\s*"([^"]+)"', trust)[1], validate=True)
key_id = re.search(r'KeyId = "([^"]+)"', trust)[1]
for scenario, territory, first in (("pt-21-30", 1283, 21), ("pt-31-40", 1284, 31)):
    base = DATA / "detailed"
    latest = read(base / scenario / "latest.json")
    require(latest["schemaVersion"] == 1 and latest["scenarioKey"] == scenario, "Wrong latest scenario")
    prefix = scenario + "/" + latest["releaseId"] + "/"
    require(latest["catalogPath"] == prefix + "catalog.json" and latest["signaturePath"] == prefix + "catalog.sig.json", "Wrong latest paths")
    catalog_path = base / latest["catalogPath"]
    catalog = read(catalog_path)
    fields(catalog, ("schemaVersion", "scenarioKey", "releaseId", "modelSha256", "hoardYieldSha256", "displayName", "territoryId", "floors", "floorSamples", "rooms"))
    scope(catalog, scenario, territory, list(range(first, first + 9)))
    require(catalog["releaseId"] == latest["releaseId"] and catalog["modelSha256"] == latest["modelSha256"] and
            digest(catalog_path.read_bytes()) == latest["catalogSha256"], "Map latest/hash mismatch")
    require(catalog["displayName"] == "Pilgrim's Traverse " + ("21-30" if first == 21 else "31-40"), "Unexpected map display label")
    for sample in catalog["floorSamples"]:
        fields(sample, ("floor", "observationCount", "hoardPositiveCount", "noHoardCount"))
    for room in catalog["rooms"]:
        fields(room, ("layoutIndex", "roomIndex", "candidates"))
        for candidate in room["candidates"]:
            fields(candidate, ("position", "successor", "hoardCountsByFloor")); position(candidate["position"])
            successor = candidate["successor"]
            fields(successor, ("state", "observationCount"), ("target",))
            require(successor["state"] in ("unknown", "observedUnique", "conflict"), "Invalid successor state")
            if "target" in successor: position(successor["target"])
    signature = read(base / latest["signaturePath"])
    fields(signature, ("schemaVersion", "algorithm", "keyId", "catalogSha256", "signature"))
    require(signature["schemaVersion"] == 1 and signature["keyId"] == key_id and signature["algorithm"] == "ecdsa-p256-sha256" and
            signature["catalogSha256"] == latest["catalogSha256"], "Invalid signature envelope")
    with tempfile.TemporaryDirectory(prefix="newfolder3-public-map-") as tmp:
        tmp = Path(tmp); (tmp / "key.der").write_bytes(public_key)
        (tmp / "signature.der").write_bytes(base64.b64decode(signature["signature"], validate=True))
        result = subprocess.run(["openssl", "dgst", "-sha256", "-verify", str(tmp / "key.der"), "-keyform", "DER",
                                 "-signature", str(tmp / "signature.der"), str(catalog_path)], capture_output=True, text=True)
        require(result.returncode == 0, "Original map signature failed: " + result.stderr)
    yield_path = base / prefix / "hoard-yield-catalog.json"
    require(digest(yield_path.read_bytes()) == catalog["hoardYieldSha256"], "Yield hash mismatch")
    yields = read(yield_path)
    fields(yields, ("schemaVersion", "scenarioKey", "territoryId", "floors", "finiteCandidatePrior", "exactFloorPriorStrength", "floorEstimates", "rooms"))
    scope(yields, scenario, territory, catalog["floors"])
    for estimate in yields["floorEstimates"]:
        fields(estimate, ("floor", "eligibleExposureCount", "hoardPositiveCount", "estimatedHoardProbability"))
    for room in yields["rooms"]:
        fields(room, ("layoutIndex", "roomIndex", "candidates"))
        for candidate in room["candidates"]:
            fields(candidate, ("position", "floorsetHoardCount", "eligibleExposureCount", "floorsetPosteriorWeight", "floors"))
            position(candidate["position"])
            for floor in candidate["floors"]:
                fields(floor, ("floor", "exactHoardCount", "eligibleExposureCount", "posteriorWeight"))
    print("PASS signed repository map " + scenario + ": hash, original key, yield, scope and field allowlists")
print(f"PASS public coordinates: {counts['Trap']} traps, {counts['Hoard']} hoards; pinned SHA256, scope and field allowlists")
print(f"PASS public map inventory: {len(expected)} data files; no private or unexpected files")
