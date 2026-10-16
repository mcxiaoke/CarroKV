package ckv

object Constants {
    val FILE_MAGIC = "UKVDB003".toByteArray(Charsets.US_ASCII)

    const val FORMAT_READ_VER: Short = 3
    const val FORMAT_WRITE_VER: Short = 3
    const val FILE_HEADER_LEN: Int = 32

    const val FRAME_MAGIC: Int = 0x4D52464B // 'KFRM' in little-endian
    const val FRAME_HEADER_LEN: Int = 28
    const val ENTRY_FIXED_HEADER_LEN: Int = 10

    const val MAX_NS_LEN: Int = 255
    const val MAX_KEY_LEN: Int = 65535
    const val MAX_VAL_LEN: Int = 16 * 1024 * 1024 // 16 MB
    const val MAX_PAYLOAD_LEN: Int = 64 * 1024 * 1024 // 64 MB
    const val MAX_OPS_PER_FRAME: Int = 1_000_000

    const val OP_PUT: Byte = 0x01
    const val OP_DELETE: Byte = 0x02
    const val OP_DROP_NAMESPACE: Byte = 0x03
    const val BATCH_OP_CLEAR_STAGING: Byte = 0xFF.toByte()

    const val VAL_TYPE_BYTES: Byte = 0x00
    const val VAL_TYPE_STRING: Byte = 0x01
    const val VAL_TYPE_INT64: Byte = 0x02
    const val VAL_TYPE_FLOAT64: Byte = 0x03
    const val VAL_TYPE_BOOL: Byte = 0x04

    const val DEFAULT_NAMESPACE_ALIAS: String = "default"
}
