"""Prepare single-architecture Godot templates from the official universal ZIP."""
import copy
import pathlib
import struct
import sys
import zipfile

CPU_TYPES = {"x86_64": 0x01000007, "arm64": 0x0100000C}
FAT_FORMATS = {b"\xca\xfe\xba\xbe": (">", False), b"\xbe\xba\xfe\xca": ("<", False),
               b"\xca\xfe\xba\xbf": (">", True), b"\xbf\xba\xfe\xca": ("<", True)}
THIN_FORMATS = {b"\xcf\xfa\xed\xfe": "<", b"\xfe\xed\xfa\xcf": ">"}


def thin_macho(data, architecture):
    wanted = CPU_TYPES[architecture]
    if len(data) < 8:
        raise ValueError("Truncated Mach-O header")
    if data[:4] in THIN_FORMATS:
        if struct.unpack_from(THIN_FORMATS[data[:4]] + "I", data, 4)[0] == wanted:
            return data
        raise ValueError(f"Mach-O binary does not contain {architecture}")
    if data[:4] not in FAT_FORMATS:
        raise ValueError("Expected a universal or 64-bit Mach-O binary")
    endian, wide = FAT_FORMATS[data[:4]]
    count = struct.unpack_from(endian + "I", data, 4)[0]
    entry_size = 32 if wide else 20
    header_end = 8 + count * entry_size
    if header_end > len(data):
        raise ValueError("Truncated universal architecture table")
    for index in range(count):
        position = 8 + index * entry_size
        cpu, _, offset, size = struct.unpack_from(endian + ("IIQQ" if wide else "IIII"), data, position)
        if cpu != wanted:
            continue
        if offset < header_end or size < 8 or offset + size > len(data):
            raise ValueError("Invalid universal Mach-O slice bounds")
        # Verify that the slice itself declares the requested CPU type.
        return thin_macho(data[offset:offset + size], architecture)
    raise ValueError(f"Universal Mach-O binary does not contain {architecture}")


def prepare_template(source, destination, architecture):
    if architecture not in CPU_TYPES:
        raise ValueError(f"Unsupported Mac architecture: {architecture}")
    required = {"godot_macos_debug", "godot_macos_release"}
    found = set()
    temporary = destination.with_suffix(destination.suffix + ".tmp")
    try:
        with zipfile.ZipFile(source) as original, zipfile.ZipFile(temporary, "w") as output:
            for entry in original.infolist():
                path = pathlib.PurePosixPath(entry.filename)
                content = original.read(entry)
                info = copy.copy(entry)
                for prefix in required:
                    if path.parent.name == "MacOS" and path.name in (prefix + ".universal", prefix + "." + architecture):
                        if prefix in found:
                            raise ValueError(f"Duplicate template binary: {prefix}")
                        found.add(prefix)
                        info.filename = str(path.with_name(prefix + "." + architecture))
                        content = thin_macho(content, architecture)
                        break
                output.writestr(info, content)
        if found != required:
            raise ValueError(f"Missing Mac template binaries: {sorted(required - found)}")
        temporary.replace(destination)
    finally:
        temporary.unlink(missing_ok=True)


def prepare_templates(source, directory):
    outputs = []
    for architecture in CPU_TYPES:
        destination = directory / f"macos-{architecture}.zip"
        prepare_template(source, destination, architecture)
        print(f"Prepared {architecture} template: {destination}", flush=True)
        outputs.append(destination)
    return outputs


if __name__ == "__main__":
    prepare_templates(pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2]))
