import 'dart:typed_data';

class Constants {
  static final Uint8List fileMagic = Uint8List.fromList('UKVDB003'.codeUnits);

  static const int formatReadVer = 3;
  static const int formatWriteVer = 3;
  static const int fileHeaderLen = 32;

  /// Reserved alias for the default (empty) namespace. DESIGN-SPEC §3.3.1:
  /// callers cannot create a physical namespace literally named "default".
  static const String defaultNamespaceAlias = 'default';

  static const int frameMagic = 0x4D52464B; // "KFRM" in uint32-LE
  static const int frameHeaderLen = 28;
  static const int entryFixedHeaderLen = 10;

  static const int maxNsLen = 255;
  static const int maxKeyLen = 65535;
  static const int maxValLen = 16 * 1024 * 1024; // 16MB
  static const int maxPayloadLen = 64 * 1024 * 1024; // 64MB
  static const int maxOpsPerFrame = 1000000;

  static const int opPut = 0x01;
  static const int opDelete = 0x02;
  static const int opDropNamespace = 0x03;
  static const int batchOpClearStaging = 0xFF;

  static const int valTypeBytes = 0x00;
  static const int valTypeString = 0x01;
  static const int valTypeInt64 = 0x02;
  static const int valTypeFloat64 = 0x03;
  static const int valTypeBool = 0x04;
}
