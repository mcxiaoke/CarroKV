import 'dart:typed_data';

class Crc32 {
  static const int _polynomial = 0xEDB88320;
  static final Uint32List _table = _initTable();

  static Uint32List _initTable() {
    final table = Uint32List(256);
    for (int i = 0; i < 256; i++) {
      int crc = i;
      for (int j = 0; j < 8; j++) {
        if ((crc & 1) != 0) {
          crc = (crc >>> 1) ^ _polynomial;
        } else {
          crc = crc >>> 1;
        }
      }
      table[i] = crc & 0xFFFFFFFF;
    }
    return table;
  }

  static int compute(List<int> bytes, [int offset = 0, int? length]) {
    final len = length ?? (bytes.length - offset);
    int crc = 0xFFFFFFFF;
    final end = offset + len;
    for (int i = offset; i < end; i++) {
      final index = (crc ^ bytes[i]) & 0xFF;
      crc = (crc >>> 8) ^ _table[index];
    }
    return (crc ^ 0xFFFFFFFF) & 0xFFFFFFFF;
  }
}
