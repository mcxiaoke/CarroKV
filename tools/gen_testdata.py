#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""按 CKV v3.2 二进制规范独立生成黄金测试向量。

本脚本是**独立于 Rust / Go / .NET 三种实现的第三方生成器**：它只依据
docs/KV-DB-DESIGN-SPEC.md 手写字节，不调用任何 CKV 代码。因此
src/{rust,go,dotnet} 三方读同一份 testdata/*.db 并校验通过，才真正证明
"同一份文件被三种实现无损读取"。

用法：
    python tools/gen_testdata.py

输出（全部为确定性文件，重复运行结果字节相同）：
    testdata/empty_v3.db          32 B  空库
    testdata/single_put_v3.db     80 B  单条 hello=world
    testdata/multi_types_v3.db          5 种 ValueType + 2 个命名空间
    testdata/unicode_order_v3.db        非 ASCII 命名空间/键的字节序验证
    testdata/MANIFEST.txt               文件名 / 大小 / SHA-256 清单
"""

import hashlib
import os
import struct
import sys

# ---------------------------------------------------------------- 规范常量
FILE_MAGIC = b"UKVDB003"
FRAME_MAGIC = 0x4D52464B  # 'KFRM' in little-endian
FORMAT_READ_VER = 3
FORMAT_WRITE_VER = 3
FILE_HEADER_LEN = 32
FRAME_HEADER_LEN = 28
ENTRY_FIXED_HEADER_LEN = 10

OP_PUT = 0x01
OP_DELETE = 0x02
OP_DROP_NAMESPACE = 0x03

VAL_BYTES = 0x00
VAL_STRING = 0x01
VAL_INT64 = 0x02
VAL_FLOAT64 = 0x03
VAL_BOOL = 0x04

DEFAULT_NAMESPACE_ALIAS = "default"
CANONICAL_QUIET_NAN = 0x7FF8000000000000

# ---------------------------------------------------------------- CRC32
_CRC_TABLE = []


def _build_crc_table():
    for i in range(256):
        c = i
        for _ in range(8):
            c = (c >> 1) ^ 0xEDB88320 if (c & 1) else (c >> 1)
        _CRC_TABLE.append(c)


_build_crc_table()


def crc32(data: bytes) -> int:
    """IEEE 802.3 CRC32：多项式 0xEDB88320，初值 0xFFFFFFFF，最终异或 0xFFFFFFFF。"""
    crc = 0xFFFFFFFF
    for b in data:
        crc = (crc >> 8) ^ _CRC_TABLE[(crc ^ b) & 0xFF]
    return crc ^ 0xFFFFFFFF


# ---------------------------------------------------------------- 编码工具
def normalize_namespace(ns: str) -> bytes:
    """空串与 "default" 统一归一化为零字节（规范 §3.3.1）。"""
    if ns == "" or ns == DEFAULT_NAMESPACE_ALIAS:
        return b""
    raw = ns.encode("utf-8")
    if len(raw) > 255:
        raise ValueError("namespace too long: %r" % ns)
    return raw


def encode_f64(val: float) -> bytes:
    """NaN 规范化为小端 00 00 00 00 00 00 F8 7F（规范 §3.3.5）。"""
    if val != val:  # NaN
        return struct.pack("<Q", CANONICAL_QUIET_NAN)
    return struct.pack("<d", val)


def encode_entry(ns: bytes, key: bytes, val_type: int, val: bytes) -> bytes:
    if len(ns) > 255:
        raise ValueError("namespace too long")
    if len(key) > 65535:
        raise ValueError("key too long")
    if len(val) > 16 * 1024 * 1024:
        raise ValueError("value too long")
    return (
        struct.pack(
            "<BBHHI",
            OP_PUT,
            val_type,
            len(ns),
            len(key),
            len(val),
        )
        + ns
        + key
        + val
    )


def file_header() -> bytes:
    buf = bytearray(FILE_HEADER_LEN)
    buf[0:8] = FILE_MAGIC
    struct.pack_into("<H", buf, 8, FORMAT_READ_VER)
    struct.pack_into("<H", buf, 10, FORMAT_WRITE_VER)
    struct.pack_into("<I", buf, 12, 0)  # HeaderFlags
    # 16..28 Reserved = 0x00
    struct.pack_into("<I", buf, 28, crc32(bytes(buf[0:28])))
    return bytes(buf)


def frame(tx_id: int, entries) -> bytes:
    """把若干条目打包为 1 个 BatchFrame（规范 §3.2）。"""
    assert entries, "OpCount must be >= 1"
    payload = b"".join(entries)
    payload_crc = crc32(payload)

    tail20 = struct.pack(
        "<QIII",  # TxID(u64) + OpCount(u32) + PayloadLen(u32) + PayloadCRC32(u32)
        tx_id,
        len(entries),
        len(payload),
        payload_crc,
    )
    header_crc = crc32(tail20)
    header = struct.pack("<II", FRAME_MAGIC, header_crc) + tail20
    assert len(header) == FRAME_HEADER_LEN
    return header + payload


def build_snapshot(items, tx_id: int = 1) -> bytes:
    """items: 可迭代的 (ns, key, val_type, val_bytes)。按 (ns, key) 字节序排序后打包。"""
    prepared = []
    for ns, key, val_type, val in items:
        ns_b = normalize_namespace(ns)
        key_b = key.encode("utf-8") if isinstance(key, str) else key
        prepared.append((ns_b, key_b, val_type, val))
    # 规范 §3.3.2：严格按 (NamespaceBytes, KeyBytes) 无符号 UTF-8 字节字典序升序
    prepared.sort(key=lambda e: (e[0], e[1]))

    if not prepared:
        # 空库：仅 32B 文件头，N=0 帧
        return file_header()

    entries = [encode_entry(ns_b, key_b, vt, v) for ns_b, key_b, vt, v in prepared]
    return file_header() + frame(tx_id, entries)


# ---------------------------------------------------------------- 向量定义
def vector_empty():
    return build_snapshot([])


def vector_single_put():
    return build_snapshot([("", "hello", VAL_STRING, b"world")])


def vector_multi_types():
    """5 种 ValueType 全覆盖 + 2 个命名空间（含非默认命名空间）。"""
    return build_snapshot(
        [
            ("", "bytes", VAL_BYTES, bytes([0x00, 0x01, 0xFE, 0xFF])),
            ("", "str", VAL_STRING, "你好".encode("utf-8")),
            ("", "i64", VAL_INT64, struct.pack("<q", -1234567890123456789)),
            ("", "f64", VAL_FLOAT64, encode_f64(3.141592653589793)),
            ("", "f64nan", VAL_FLOAT64, encode_f64(float("nan"))),
            ("", "bool_t", VAL_BOOL, bytes([0x01])),
            ("", "bool_f", VAL_BOOL, bytes([0x00])),
            ("cfg", "enabled", VAL_BOOL, bytes([0x01])),
            ("cfg", "limit", VAL_INT64, struct.pack("<q", 1000)),
            ("users", "u1", VAL_STRING, "alice".encode("utf-8")),
        ]
    )


def vector_unicode_order():
    """非 ASCII 命名空间与键，锁定 UTF-8 无符号字节序（而非 UTF-16 码元序）。"""
    return build_snapshot(
        [
            # 默认命名空间内：'a' < 'z' < U+E000(EE 80 80) < U+1F600(F0 9F 98 80)
            ("", "a", VAL_STRING, b"1"),
            ("", "z", VAL_STRING, b"2"),
            ("", "\uE000", VAL_STRING, b"3"),
            ("", "\U0001F600", VAL_STRING, b"4"),
            # 命名空间按字节序："" < "cf" < "中文"(E4 B8 AD) < "é"(C3 A9)?
            # 注意 C3 < E4，所以 "é" < "中文"
            ("é", "k", VAL_STRING, b"5"),
            ("中文", "k", VAL_STRING, b"6"),
            ("cf", "k", VAL_STRING, b"7"),
        ]
    )


VECTORS = [
    ("empty_v3.db", vector_empty),
    ("single_put_v3.db", vector_single_put),
    ("multi_types_v3.db", vector_multi_types),
    ("unicode_order_v3.db", vector_unicode_order),
]


# ---------------------------------------------------------------- 主流程
def main():
    here = os.path.dirname(os.path.abspath(__file__))
    root = os.path.dirname(here)
    out_dir = os.path.join(root, "testdata")
    os.makedirs(out_dir, exist_ok=True)

    # 自检：CRC32 标准向量（规范 §4.3.2）
    assert crc32(b"123456789") == 0xCBF43926, "CRC32 self-test failed"
    assert crc32(file_header()[0:28]) == 0x0C44C937, "empty header CRC self-test failed"

    manifest = []
    manifest.append("# CKV v3.2 黄金测试向量清单")
    manifest.append("# 由 tools/gen_testdata.py 确定性生成；请勿手工编辑")
    manifest.append("#")
    manifest.append("# 约定：三语言（Rust / Go / .NET）与后续新语言都必须能")
    manifest.append("#   1) 逐字节复现以下 SHA-256；")
    manifest.append("#   2) 正确解析出文件内容（见各语言 golden 测试的断言）。")
    manifest.append("")
    manifest.append("%-26s %10s  %s" % ("FILE", "SIZE", "SHA-256"))
    manifest.append("-" * 100)

    for name, fn in VECTORS:
        data = fn()
        path = os.path.join(out_dir, name)
        with open(path, "wb") as f:
            f.write(data)
        digest = hashlib.sha256(data).hexdigest()
        manifest.append("%-26s %10d  %s" % (name, len(data), digest))
        print("%-26s %8d bytes  %s" % (name, len(data), digest))

    manifest.append("")
    with open(os.path.join(out_dir, "MANIFEST.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(manifest) + "\n")

    print("\nMANIFEST.txt written to", out_dir)
    return 0


if __name__ == "__main__":
    sys.exit(main())
