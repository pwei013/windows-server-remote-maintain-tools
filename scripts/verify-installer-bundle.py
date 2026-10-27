#!/usr/bin/env python3
"""Inspect a Windows installer bundle without loading or executing Windows code.

Uses only the Python standard library. Bundle layout follows dotnet/runtime
Microsoft.NET.HostModel Bundle/{Manifest,FileEntry}.cs. This is a local build
check, not a deployment or an installed-machine verification tool.
"""
import argparse
import hashlib
import json
import mmap
import re
import struct
import sys
import xml.etree.ElementTree as ET
import zlib
from pathlib import Path

SIGNATURE = bytes.fromhex("8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae")


def require(condition, code):
    if not condition:
        raise ValueError(code)


def number(data, offset, fmt):
    size = struct.calcsize(fmt)
    require(0 <= offset <= len(data) - size, "BINARY_RANGE_INVALID")
    return struct.unpack_from(fmt, data, offset)[0]


def binary_string(data, offset):
    length = 0
    for shift in range(0, 35, 7):
        part = number(data, offset, "<B")
        offset += 1
        length |= (part & 127) << shift
        if not part & 128:
            break
    else:
        raise ValueError("STRING_PREFIX_INVALID")
    require(length <= 32768 and offset + length <= len(data), "STRING_RANGE_INVALID")
    return bytes(data[offset:offset + length]).decode("utf-8", "strict"), offset + length


def safe_relative(name):
    require(name and "\\" not in name and not name.startswith("/"), "BUNDLE_PATH_INVALID")
    for part in name.split("/"):
        require(part not in ("", ".", "..") and not part.endswith((".", " ")) and
                not any(ord(ch) < 32 or ch in '<>:"|?*' for ch in part), "BUNDLE_PATH_INVALID")
        require(not re.fullmatch(r"CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9]", part.split(".")[0], re.I),
                "BUNDLE_RESERVED_PATH")


class PE:
    def __init__(self, data):
        self.data = data
        require(bytes(data[:2]) == b"MZ", "DOS_SIGNATURE_MISSING")
        self.base = number(data, 0x3c, "<I")
        require(bytes(data[self.base:self.base + 4]) == b"PE\0\0", "PE_SIGNATURE_MISSING")
        self.machine = number(data, self.base + 4, "<H")
        count = number(data, self.base + 6, "<H")
        optional_size = number(data, self.base + 20, "<H")
        self.optional = self.base + 24
        self.magic = number(data, self.optional, "<H")
        require(self.magic in (0x10b, 0x20b), "PE_OPTIONAL_HEADER_INVALID")
        self.subsystem = number(data, self.optional + 68, "<H")
        directory = self.optional + (112 if self.magic == 0x20b else 96)
        self.directories = [(number(data, directory + index * 8, "<I"),
                             number(data, directory + index * 8 + 4, "<I")) for index in range(16)]
        require(0 < count <= 96, "PE_SECTIONS_INVALID")
        self.sections = {}
        start = self.optional + optional_size
        for index in range(count):
            item = start + index * 40
            name = bytes(data[item:item + 8]).split(b"\0", 1)[0].decode("ascii", "strict")
            require(name not in self.sections, "PE_SECTION_DUPLICATED")
            size = number(data, item + 16, "<I")
            raw = number(data, item + 20, "<I")
            require(raw + size <= len(data), "PE_SECTION_RANGE_INVALID")
            self.sections[name] = {"virtualSize": number(data, item + 8, "<I"),
                                   "rva": number(data, item + 12, "<I"), "size": size, "offset": raw}

    def rva(self, address, size=1):
        for section in self.sections.values():
            delta = address - section["rva"]
            if 0 <= delta and delta + size <= section["size"]:
                return section["offset"] + delta
        raise ValueError("PE_RVA_UNMAPPED")

    def section_hash(self, name):
        require(name in self.sections, "HOST_SECTION_MISSING")
        entry = self.sections[name]
        return hashlib.sha256(memoryview(self.data)[entry["offset"]:entry["offset"] + entry["size"]]).hexdigest().upper()

    def resources(self, kind):
        address, size = self.directories[2]
        require(address and size, "PE_RESOURCES_MISSING")
        root = self.rva(address, size)
        result = []

        def walk(relative, path):
            require(len(path) <= 3 and 0 <= relative <= size - 16, "RESOURCE_DIRECTORY_INVALID")
            count = number(self.data, root + relative + 12, "<H") + number(self.data, root + relative + 14, "<H")
            require(count <= 4096 and relative + 16 + count * 8 <= size, "RESOURCE_ENTRIES_INVALID")
            for index in range(count):
                offset = root + relative + 16 + index * 8
                name = number(self.data, offset, "<I")
                value = number(self.data, offset + 4, "<I")
                if not path and name != kind:
                    continue
                names = path + [name]
                if value & 0x80000000:
                    walk(value & 0x7fffffff, names)
                else:
                    require(value + 16 <= size and len(names) == 3, "RESOURCE_DATA_INVALID")
                    entry = root + value
                    data_size = number(self.data, entry + 4, "<I")
                    require(0 < data_size <= 1024 * 1024, "RESOURCE_SIZE_INVALID")
                    data_offset = self.rva(number(self.data, entry, "<I"), data_size)
                    result.append((names, bytes(self.data[data_offset:data_offset + data_size])))

        walk(0, [])
        return result


def native_manifest(pe):
    result = []
    for names, value in pe.resources(24):
        require(b"<!DOCTYPE" not in value.upper() and b"<!ENTITY" not in value.upper(), "MANIFEST_DTD_FORBIDDEN")
        document = ET.fromstring(value)
        levels = [node.attrib for node in document.iter() if node.tag.split("}")[-1] == "requestedExecutionLevel"]
        require(len(levels) == 1 and levels[0].get("level") == "requireAdministrator" and
                levels[0].get("uiAccess", "false").lower() == "false", "UAC_ADMINISTRATOR_REQUIRED")
        result.append({"resource": names, "size": len(value), "sha256": hashlib.sha256(value).hexdigest().upper(),
                       "requestedExecutionLevel": "requireAdministrator", "uiAccess": False})
    require(result, "NATIVE_MANIFEST_MISSING")
    return result


def native_version(pe):
    versions = []
    for _, value in pe.resources(16):
        position = value.find(b"\xbd\x04\xef\xfe")
        if position >= 0 and position + 24 <= len(value):
            def version(offset):
                high, low = number(value, position + offset, "<I"), number(value, position + offset + 4, "<I")
                return ".".join(str(part) for part in (high >> 16, high & 65535, low >> 16, low & 65535))
            versions.append({"fileVersion": version(8), "productVersion": version(16)})
    return versions


def bundle(data, pe):
    positions = []
    start = 0
    host_end = max(section["offset"] + section["size"] for section in pe.sections.values())
    while True:
        found = data.find(SIGNATURE, start, host_end)
        if found < 0:
            break
        positions.append(found)
        start = found + len(SIGNATURE)
    require(len(positions) == 1 and positions[0] >= 8, "BUNDLE_SIGNATURE_NOT_UNIQUE")
    header = number(data, positions[0] - 8, "<q")
    require(host_end <= header < len(data) - 12, "BUNDLE_HEADER_INVALID")
    major, minor, count = struct.unpack_from("<IIi", data, header)
    require(major in (2, 6) and minor == 0 and 0 < count <= 10000, "BUNDLE_FORMAT_UNSUPPORTED")
    identifier, cursor = binary_string(data, header + 12)
    require(re.fullmatch(r"[A-Za-z0-9_-]{12}", identifier), "BUNDLE_ID_INVALID")
    deps_offset, deps_size, config_offset, config_size, flags = struct.unpack_from("<qqqqQ", data, cursor)
    cursor += 40
    require(flags in (0, 1), "BUNDLE_FLAGS_UNSUPPORTED")
    entries = []
    names = set()
    for _ in range(count):
        offset, size = number(data, cursor, "<q"), number(data, cursor + 8, "<q")
        cursor += 16
        compressed = number(data, cursor, "<q") if major >= 6 else 0
        cursor += 8 if major >= 6 else 0
        kind = number(data, cursor, "<B")
        name, cursor = binary_string(data, cursor + 1)
        safe_relative(name)
        require(name.casefold() not in names, "BUNDLE_ENTRY_DUPLICATED")
        names.add(name.casefold())
        stored = compressed or size
        require(0 <= kind <= 5 and host_end <= offset < header and 0 < size <= 1024 * 1024 * 1024 and
                0 <= compressed <= size and offset + stored <= header, "BUNDLE_ENTRY_RANGE_INVALID")
        entries.append({"path": name, "offset": offset, "size": size, "compressedSize": compressed, "type": kind})
    require(cursor <= len(data), "BUNDLE_MANIFEST_TRUNCATED")
    ordered = sorted(entries, key=lambda item: item["offset"])
    for before, after in zip(ordered, ordered[1:]):
        require(before["offset"] + (before["compressedSize"] or before["size"]) <= after["offset"], "BUNDLE_ENTRY_OVERLAP")
    by_name = {entry["path"]: entry for entry in entries}
    for name in ("IISManager.Setup.dll", "IISManager.Setup.Core.dll", "System.Windows.Forms.dll",
                 "System.Windows.Forms.Primitives.dll", "System.Private.CoreLib.dll", "System.Runtime.dll",
                 "IISManager.Setup.deps.json", "IISManager.Setup.runtimeconfig.json"):
        require(name in by_name, "BUNDLE_RUNTIME_FILE_MISSING:" + name)
    require((deps_offset, deps_size) == (by_name["IISManager.Setup.deps.json"]["offset"], by_name["IISManager.Setup.deps.json"]["size"]) and
            (config_offset, config_size) == (by_name["IISManager.Setup.runtimeconfig.json"]["offset"], by_name["IISManager.Setup.runtimeconfig.json"]["size"]),
            "BUNDLE_CONFIGURATION_LOCATION_INVALID")
    return {"version": f"{major}.{minor}", "id": identifier, "headerOffset": header,
            "manifestEnd": cursor, "entryCount": count, "flags": flags, "entries": entries}, by_name


def extract(data, entry):
    raw = bytes(data[entry["offset"]:entry["offset"] + (entry["compressedSize"] or entry["size"])])
    if entry["compressedSize"]:
        inflater = zlib.decompressobj(-zlib.MAX_WBITS)
        result = inflater.decompress(raw, entry["size"] + 1)
        require(inflater.eof and not inflater.unused_data and not inflater.unconsumed_tail, "BUNDLE_DEFLATE_INVALID")
    else:
        result = raw
    require(len(result) == entry["size"], "BUNDLE_EXTRACT_SIZE_MISMATCH")
    return result


# ECMA-335 table column widths through ManifestResource (table 40).
SCHEMAS = [
    "u2 str guid guid guid", "ResolutionScope str str", "u4 str str TypeDefOrRef t4 t6", "t4", "u2 str blob", "t6",
    "u4 u2 u2 str blob t8", "t8", "u2 u2 str", "t2 TypeDefOrRef", "MemberRefParent str blob", "u2 HasConstant blob",
    "HasCustomAttribute CustomAttributeType blob", "HasFieldMarshal blob", "u2 HasDeclSecurity blob", "u2 u4 t2", "u4 t4", "blob",
    "t2 t20", "t20", "u2 str TypeDefOrRef", "t2 t23", "t23", "u2 str blob", "u2 t6 HasSemantics",
    "t2 MethodDefOrRef MethodDefOrRef", "str", "blob", "u2 MemberForwarded str t26", "u4 t4", "u4 u4", "u4",
    "u4 u2 u2 u2 u2 u4 blob str str", "u4", "u4 u4 u4", "u2 u2 u2 u2 u4 blob str str blob", "u4 t35", "u4 u4 u4 t35",
    "u4 str blob", "u4 u4 str str Implementation", "u4 u4 str Implementation"
]
CODED = {
    "ResolutionScope": (2, (0, 26, 35, 1)), "TypeDefOrRef": (2, (2, 1, 27)),
    "MemberRefParent": (3, (2, 1, 26, 6, 27)), "HasConstant": (2, (4, 8, 23)),
    "HasCustomAttribute": (5, (6, 4, 1, 2, 8, 9, 10, 0, 14, 23, 20, 17, 26, 27, 32, 35, 38, 39, 40, 42, 44, 43)),
    "CustomAttributeType": (3, (6, 10)), "HasFieldMarshal": (1, (4, 8)), "HasDeclSecurity": (2, (2, 6, 32)),
    "HasSemantics": (1, (20, 23)), "MethodDefOrRef": (1, (6, 10)), "MemberForwarded": (1, (4, 6)),
    "Implementation": (2, (38, 35, 39))
}


def managed_payloads(assembly):
    pe = PE(assembly)
    clr_rva, clr_size = pe.directories[14]
    require(clr_rva and clr_size >= 72, "MANAGED_HEADER_MISSING")
    clr = pe.rva(clr_rva, 72)
    metadata = pe.rva(number(assembly, clr + 8, "<I"), number(assembly, clr + 12, "<I"))
    require(bytes(assembly[metadata:metadata + 4]) == b"BSJB", "METADATA_SIGNATURE_INVALID")
    cursor = metadata + 16 + number(assembly, metadata + 12, "<I")
    cursor = (cursor + 3) & ~3
    count = number(assembly, cursor + 2, "<H")
    cursor += 4
    require(0 < count <= 32, "METADATA_STREAMS_INVALID")
    streams = {}
    for _ in range(count):
        offset, size = number(assembly, cursor, "<I"), number(assembly, cursor + 4, "<I")
        name_start = cursor + 8
        name_end = assembly.find(b"\0", name_start, name_start + 32)
        require(name_end >= 0, "METADATA_STREAM_NAME_INVALID")
        name = assembly[name_start:name_end].decode("ascii")
        require(name not in streams and metadata + offset + size <= len(assembly), "METADATA_STREAM_RANGE_INVALID")
        streams[name] = (metadata + offset, size)
        cursor = (name_end + 4) & ~3
    require("#Strings" in streams and ("#~" in streams or "#-" in streams), "METADATA_TABLES_MISSING")
    tables, table_size = streams.get("#~", streams.get("#-"))
    heap_flags = number(assembly, tables + 6, "<B")
    valid = number(assembly, tables + 8, "<Q")
    rows = [0] * 64
    cursor = tables + 24
    for table in range(64):
        if valid & 1 << table:
            rows[table] = number(assembly, cursor, "<I")
            cursor += 4

    def width(column):
        if column in ("u2", "u4"):
            return int(column[1:])
        if column in ("str", "guid", "blob"):
            return 4 if heap_flags & {"str": 1, "guid": 2, "blob": 4}[column] else 2
        if column.startswith("t") and column[1:].isdigit():
            return 4 if rows[int(column[1:])] >= 65536 else 2
        bits, targets = CODED[column]
        return 4 if max(rows[index] for index in targets) >= 1 << (16 - bits) else 2

    for table in range(40):
        cursor += rows[table] * sum(width(column) for column in SCHEMAS[table].split())
    resource_row_size = sum(width(column) for column in SCHEMAS[40].split())
    require(cursor + rows[40] * resource_row_size <= tables + table_size, "RESOURCE_TABLE_RANGE_INVALID")
    strings, strings_size = streams["#Strings"]
    resource_rva, resource_size = number(assembly, clr + 24, "<I"), number(assembly, clr + 28, "<I")
    resource_base = pe.rva(resource_rva, resource_size)
    result = {}
    for _ in range(rows[40]):
        offset, flags = number(assembly, cursor, "<I"), number(assembly, cursor + 4, "<I")
        name_index = number(assembly, cursor + 8, "<I" if width("str") == 4 else "<H")
        implementation = number(assembly, cursor + 8 + width("str"), "<I" if width("Implementation") == 4 else "<H")
        cursor += resource_row_size
        require(name_index < strings_size, "RESOURCE_NAME_INVALID")
        end = assembly.find(b"\0", strings + name_index, strings + strings_size)
        require(end >= 0, "RESOURCE_NAME_INVALID")
        name = assembly[strings + name_index:end].decode("utf-8", "strict")
        if name not in ("IISManager.Setup.Payload.Agent.zip", "IISManager.Setup.Payload.Console.zip"):
            continue
        require(name not in result and implementation == 0 and flags == 1 and offset + 4 <= resource_size,
                "PAYLOAD_RESOURCE_NOT_EMBEDDED")
        size = number(assembly, resource_base + offset, "<I")
        start = resource_base + offset + 4
        require(size > 0 and offset + 4 + size <= resource_size and assembly[start:start + 4] == b"PK\x03\x04", "PAYLOAD_RESOURCE_NOT_ZIP")
        result[name] = {"size": size, "sha256": hashlib.sha256(memoryview(assembly)[start:start + size]).hexdigest().upper(),
                        "implementation": "Embedded"}
    require(len(result) == 2, "EMBEDDED_PAYLOAD_RESOURCE_MISSING")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--installer", required=True, type=Path)
    parser.add_argument("--assembly-output", required=True, type=Path, help="New task-owned work file; never overwrite")
    parser.add_argument("--evidence-output", required=True, type=Path, help="New local evidence JSON; never overwrite")
    parser.add_argument("--host-path", type=Path, help="Matching SDK singlefilehost.exe; compares .text/.pdata without execution")
    args = parser.parse_args()
    source = args.installer.resolve(strict=True)
    assembly_output, evidence_output = args.assembly_output.resolve(), args.evidence_output.resolve()
    require(source.is_file() and not assembly_output.exists() and not evidence_output.exists() and
            assembly_output != evidence_output and source not in (assembly_output, evidence_output), "INPUT_OUTPUT_PATH_INVALID")
    with source.open("rb") as handle, mmap.mmap(handle.fileno(), 0, access=mmap.ACCESS_READ) as data:
        pe = PE(data)
        require(pe.machine == 0x8664 and pe.magic == 0x20b and pe.subsystem == 2, "WINDOWS_X64_GUI_REQUIRED")
        uac, versions = native_manifest(pe), native_version(pe)
        manifest, by_name = bundle(data, pe)
        config = json.loads(extract(data, by_name["IISManager.Setup.runtimeconfig.json"]))
        deps = json.loads(extract(data, by_name["IISManager.Setup.deps.json"]))
        options = config["runtimeOptions"]
        require(options.get("tfm") == "net10.0" and not options.get("framework") and not options.get("frameworks"), "SELF_CONTAINED_NET10_REQUIRED")
        frameworks = {entry["name"]: entry["version"] for entry in options.get("includedFrameworks", [])}
        require({"Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App"}.issubset(frameworks), "SELF_CONTAINED_FRAMEWORKS_MISSING")
        require(deps["runtimeTarget"]["name"].endswith("/win-x64"), "DEPENDENCY_WINDOWS_X64_REQUIRED")
        assembly = extract(data, by_name["IISManager.Setup.dll"])
        resources = managed_payloads(assembly)
        details = {"machine": "0x8664", "peKind": "PE32+", "subsystem": 2,
                   "nativeRuntimeLibrariesEmbeddedInHost": "Not independently verified without matching SDK host",
                   "sdkHostCompared": False, "sections": {name: {"size": pe.sections[name]["size"], "sha256": pe.section_hash(name)}
                                                             for name in (".text", ".pdata")}}
        if args.host_path:
            host_path = args.host_path.resolve(strict=True)
            with host_path.open("rb") as host_handle, mmap.mmap(host_handle.fileno(), 0, access=mmap.ACCESS_READ) as host_data:
                host = PE(host_data)
                require(host.machine == 0x8664, "SDK_HOST_ARCHITECTURE_INVALID")
                for name in (".text", ".pdata"):
                    require(pe.sections[name]["size"] == host.sections[name]["size"] and pe.section_hash(name) == host.section_hash(name), "SDK_HOST_CODE_MISMATCH:" + name)
                details.update({"sdkHostCompared": True, "sdkHost": str(host_path),
                                "sdkHostSha256": hashlib.sha256(host_data).hexdigest().upper(),
                                "nativeRuntimeLibrariesEmbeddedInHost": "Matching supplied SDK singlefilehost .text and .pdata",
                                "sdkHostVersions": native_version(host)})
        evidence = {"schemaVersion": 1, "installer": str(source), "size": len(data),
                    "sha256": hashlib.sha256(data).hexdigest().upper(), "versions": {"bundle": manifest["version"],
                    "targetFramework": options["tfm"], "includedFrameworks": frameworks, "nativeVersionResources": versions},
                    "entryCount": manifest["entryCount"], "uac": uac, "nativeHostDetails": details,
                    "bundle": manifest, "embeddedPayloadResources": resources, "externalPayloadFilesRequired": False,
                    "extractedAssembly": {"path": str(assembly_output), "size": len(assembly), "sha256": hashlib.sha256(assembly).hexdigest().upper()},
                    "windowsExecutionPerformed": False, "windowsInstallationVerified": False}
        assembly_output.parent.mkdir(parents=True, exist_ok=True)
        evidence_output.parent.mkdir(parents=True, exist_ok=True)
        with assembly_output.open("xb") as output:
            output.write(assembly)
        with evidence_output.open("x", encoding="utf-8", newline="\n") as output:
            json.dump(evidence, output, ensure_ascii=False, indent=2)
            output.write("\n")
        print(json.dumps({key: evidence[key] for key in ("size", "sha256", "entryCount", "externalPayloadFilesRequired", "windowsExecutionPerformed")}, ensure_ascii=False))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, struct.error, OSError, ET.ParseError, zlib.error) as failure:
        # Status/type only: do not dump binary input, raw exceptions or configuration.
        code = str(failure) if isinstance(failure, ValueError) and re.fullmatch(r"[A-Z0-9_:.+-]+", str(failure)) else "BUNDLE_VERIFICATION_FAILED"
        print(json.dumps({"status": "failed", "code": code, "exceptionType": type(failure).__name__}), file=sys.stderr)
        raise SystemExit(1)
