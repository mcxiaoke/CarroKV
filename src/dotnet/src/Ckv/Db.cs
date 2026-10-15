using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Ckv
{
    internal sealed class KeyItem
    {
        public byte[] Key { get; }
        public byte ValueType { get; set; }
        public byte[] Value { get; set; }

        public KeyItem(byte[] key, byte valueType, byte[] value)
        {
            Key = key;
            ValueType = valueType;
            Value = value;
        }
    }

    internal sealed class NamespaceStore
    {
        public List<KeyItem> Items { get; } = new List<KeyItem>();

        public int FindIndex(ReadOnlySpan<byte> key)
        {
            int low = 0;
            int high = Items.Count - 1;
            while (low <= high)
            {
                int mid = low + ((high - low) >> 1);
                int cmp = Items[mid].Key.AsSpan().SequenceCompareTo(key);
                if (cmp == 0) return mid;
                if (cmp < 0) low = mid + 1;
                else high = mid - 1;
            }
            return ~low;
        }

        public void Put(byte[] key, byte valType, byte[] val)
        {
            int idx = FindIndex(key);
            if (idx >= 0)
            {
                Items[idx].ValueType = valType;
                Items[idx].Value = val;
            }
            else
            {
                Items.Insert(~idx, new KeyItem(key, valType, val));
            }
        }

        public bool Delete(ReadOnlySpan<byte> key)
        {
            int idx = FindIndex(key);
            if (idx >= 0)
            {
                Items.RemoveAt(idx);
                return true;
            }
            return false;
        }
    }

    public sealed class CkvDb : IDisposable
    {
        private static readonly ConcurrentDictionary<string, WeakReference<CkvDb>> Registry =
            new ConcurrentDictionary<string, WeakReference<CkvDb>>(StringComparer.OrdinalIgnoreCase);

        private readonly string _path;
        private readonly string _canonicalPath;
        private readonly DbOptions _options;
        private readonly ReaderWriterLockSlim _rwLock = new ReaderWriterLockSlim();

        private readonly SortedDictionary<string, NamespaceStore> _namespaces =
            new SortedDictionary<string, NamespaceStore>(StringComparer.Ordinal);

        private ulong _currentMaxTxId;
        private bool _isDisposed;
        private bool _registered;

        private readonly List<Action<KeyChangeEvent>> _watchers = new List<Action<KeyChangeEvent>>();
        public event Action<KeyChangeEvent>? KeyChanged;

        public void Watch(Action<KeyChangeEvent> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            lock (_watchers)
            {
                _watchers.Add(callback);
            }
        }

        private void DispatchEvents(List<KeyChangeEvent> events)
        {
            if (events == null || events.Count == 0) return;
            Action<KeyChangeEvent>[] snapshot;
            lock (_watchers)
            {
                snapshot = _watchers.ToArray();
            }

            foreach (var ev in events)
            {
                KeyChanged?.Invoke(ev);
                for (int i = 0; i < snapshot.Length; i++)
                {
                    try { snapshot[i](ev); } catch { }
                }
            }
        }

        private CkvDb(string path, string canonicalPath, DbOptions options)
        {
            _path = path;
            _canonicalPath = canonicalPath;
            _options = options.Clone();
        }

        public static CkvDb Open(string path, DbOptions? options = null)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be null or empty", nameof(path));

            var opts = options ?? DbOptions.Default;
            string canonical = Path.GetFullPath(path);

            while (true)
            {
                if (Registry.TryGetValue(canonical, out var weakRef) && weakRef.TryGetTarget(out var existing))
                {
                    if (!existing._isDisposed)
                    {
                        return existing;
                    }
                }

                var db = new CkvDb(path, canonical, opts);
                db.InitAndRecover();

                var newWeakRef = new WeakReference<CkvDb>(db);
                if (Registry.TryAdd(canonical, newWeakRef))
                {
                    db._registered = true;
                    return db;
                }

                // If racing with another insert, loop again
                if (Registry.TryGetValue(canonical, out var racingRef) && racingRef.TryGetTarget(out var rDb))
                {
                    if (!rDb._isDisposed)
                    {
                        return rDb;
                    }
                    Registry.TryUpdate(canonical, newWeakRef, racingRef);
                    db._registered = true;
                    return db;
                }
            }
        }

        private void InitAndRecover()
        {
            string dbPath = _path;
            string tmpPath = _path + ".tmp";
            string bakPath = _path + ".bak";

            bool dbExists = File.Exists(dbPath);

            if (!dbExists)
            {
                // Crash recovery promotion: check .tmp
                if (File.Exists(tmpPath))
                {
                    try
                    {
                        byte[] tmpBytes = File.ReadAllBytes(tmpPath);
                        ValidateDatabaseBytes(tmpBytes);
                        // Valid .tmp! Promote it
                        File.Move(tmpPath, dbPath);
                        dbExists = true;
                    }
                    catch
                    {
                        // Invalid .tmp, remove it
                        try { File.Delete(tmpPath); } catch { }
                    }
                }
            }

            if (dbExists)
            {
                byte[] bytes = File.ReadAllBytes(dbPath);
                try
                {
                    LoadFromBytes(bytes);
                }
                catch (CorruptedDatabaseException)
                {
                    if (_options.AutoRecoverFromBackup && File.Exists(bakPath))
                    {
                        try
                        {
                            byte[] bakBytes = File.ReadAllBytes(bakPath);
                            LoadFromBytes(bakBytes);
                            // Backup healed! Overwrite corrupted dbPath
                            File.Copy(bakPath, dbPath, true);
                            return;
                        }
                        catch
                        {
                            // Both corrupted
                            throw new CorruptedDatabaseException(
                                $"Database '{_path}' and backup '{bakPath}' are both corrupted"
                            );
                        }
                    }
                    throw;
                }
            }
        }

        private static void ValidateDatabaseBytes(byte[] bytes)
        {
            if (bytes.Length < Constants.FileHeaderLen)
                throw new CorruptedDatabaseException($"File size {bytes.Length} is less than header length 32");

            for (int i = 0; i < 8; i++)
            {
                if (bytes[i] != Constants.FileMagic[i])
                    throw new CorruptedDatabaseException("Invalid file magic");
            }

            ushort readVer = BinaryHelper.ReadUInt16LE(bytes.AsSpan(8, 2));
            if (readVer > Constants.FormatReadVer)
                throw new UnsupportedVersionException($"Unsupported format read version {readVer}");

            uint storedHeaderCrc = BinaryHelper.ReadUInt32LE(bytes.AsSpan(28, 4));
            uint calcHeaderCrc = Crc32.Compute(bytes, 0, 28);
            if (storedHeaderCrc != calcHeaderCrc)
                throw new CorruptedDatabaseException($"Header CRC mismatch: stored {storedHeaderCrc:X8}, calculated {calcHeaderCrc:X8}");
        }

        private void LoadFromBytes(byte[] bytes)
        {
            ValidateDatabaseBytes(bytes);

            _namespaces.Clear();
            _currentMaxTxId = 0;

            int offset = Constants.FileHeaderLen;
            while (offset < bytes.Length)
            {
                if (offset + Constants.FrameHeaderLen > bytes.Length)
                    throw new CorruptedDatabaseException($"Incomplete frame header at offset {offset}");

                var frameSpan = bytes.AsSpan(offset, Constants.FrameHeaderLen);
                uint frameMagic = BinaryHelper.ReadUInt32LE(frameSpan.Slice(0, 4));
                if (frameMagic != Constants.FrameMagic)
                    throw new CorruptedDatabaseException($"Invalid frame magic 0x{frameMagic:X8} at offset {offset}");

                uint storedHdrCrc = BinaryHelper.ReadUInt32LE(frameSpan.Slice(4, 4));
                uint calcHdrCrc = Crc32.Compute(bytes, offset + 8, 20);
                if (storedHdrCrc != calcHdrCrc)
                    throw new CorruptedDatabaseException($"Frame header CRC mismatch at offset {offset}");

                ulong txId = BinaryHelper.ReadUInt64LE(frameSpan.Slice(8, 8));
                uint opCount = BinaryHelper.ReadUInt32LE(frameSpan.Slice(16, 4));
                uint payloadLen = BinaryHelper.ReadUInt32LE(frameSpan.Slice(20, 4));
                uint storedPayloadCrc = BinaryHelper.ReadUInt32LE(frameSpan.Slice(24, 4));

                int payloadOffset = offset + Constants.FrameHeaderLen;
                if ((long)payloadOffset + payloadLen > bytes.Length)
                    throw new CorruptedDatabaseException($"Incomplete frame payload at offset {payloadOffset}");

                uint calcPayloadCrc = Crc32.Compute(bytes, payloadOffset, (int)payloadLen);
                if (storedPayloadCrc != calcPayloadCrc)
                    throw new CorruptedDatabaseException($"Frame payload CRC mismatch at offset {payloadOffset}");

                // Parse entries
                int pIdx = payloadOffset;
                int pEnd = payloadOffset + (int)payloadLen;
                for (uint op = 0; op < opCount; op++)
                {
                    if (pIdx + Constants.EntryFixedHeaderLen > pEnd)
                        throw new CorruptedDatabaseException("Truncated entry fixed header");

                    byte opType = bytes[pIdx];
                    byte valType = bytes[pIdx + 1];
                    ushort nsLen = BinaryHelper.ReadUInt16LE(bytes.AsSpan(pIdx + 2, 2));
                    ushort keyLen = BinaryHelper.ReadUInt16LE(bytes.AsSpan(pIdx + 4, 2));
                    uint valLen = BinaryHelper.ReadUInt32LE(bytes.AsSpan(pIdx + 6, 4));
                    pIdx += Constants.EntryFixedHeaderLen;

                    if (pIdx + nsLen + keyLen + valLen > pEnd)
                        throw new CorruptedDatabaseException("Truncated entry dynamic payload");

                    string ns = nsLen == 0 ? "" : Encoding.UTF8.GetString(bytes, pIdx, nsLen);
                    pIdx += nsLen;

                    byte[] keyBytes = new byte[keyLen];
                    Array.Copy(bytes, pIdx, keyBytes, 0, keyLen);
                    pIdx += keyLen;

                    byte[] valBytes = new byte[valLen];
                    Array.Copy(bytes, pIdx, valBytes, 0, (int)valLen);
                    pIdx += (int)valLen;

                    if (opType == Constants.OpPut)
                    {
                        if (!_namespaces.TryGetValue(ns, out var store))
                        {
                            store = new NamespaceStore();
                            _namespaces[ns] = store;
                        }
                        store.Put(keyBytes, valType, valBytes);
                    }
                }

                if (txId > _currentMaxTxId)
                {
                    _currentMaxTxId = txId;
                }

                offset = payloadOffset + (int)payloadLen;
            }
        }

        private void FlushSnapshot()
        {
            if (_options.ReadOnly)
                throw new InvalidOperationException("Cannot write to read-only database");

            using (LockFile.Acquire(_path, _options))
            {
                string tmpPath = _path + ".tmp";
                string bakPath = _path + ".bak";

                // Count total alive entries
                int totalEntries = 0;
                foreach (var kvp in _namespaces)
                {
                    totalEntries += kvp.Value.Items.Count;
                }

                using (var ms = new MemoryStream())
                {
                    // 1. Write File Header (32 bytes)
                    byte[] fileHeader = new byte[32];
                    Array.Copy(Constants.FileMagic, 0, fileHeader, 0, 8);
                    BinaryHelper.WriteUInt16LE(fileHeader.AsSpan(8, 2), Constants.FormatReadVer);
                    BinaryHelper.WriteUInt16LE(fileHeader.AsSpan(10, 2), Constants.FormatWriteVer);
                    BinaryHelper.WriteUInt32LE(fileHeader.AsSpan(12, 4), 0); // Flags
                    // 16..28 reserved 0x00
                    uint headerCrc = Crc32.Compute(fileHeader, 0, 28);
                    BinaryHelper.WriteUInt32LE(fileHeader.AsSpan(28, 4), headerCrc);
                    ms.Write(fileHeader, 0, 32);

                    if (totalEntries > 0)
                    {
                        // Multi-frame greedy packing
                        ulong nextTxId = _currentMaxTxId + 1;

                        var currentPayload = new MemoryStream();
                        uint currentOpCount = 0;

                        Action flushFrame = () =>
                        {
                            if (currentOpCount == 0) return;
                            byte[] payloadBytes = currentPayload.ToArray();
                            uint payloadLen = (uint)payloadBytes.Length;
                            uint pCrc = Crc32.Compute(payloadBytes);

                            byte[] frameHeader = new byte[28];
                            BinaryHelper.WriteUInt32LE(frameHeader.AsSpan(0, 4), Constants.FrameMagic);

                            byte[] tail20 = new byte[20];
                            BinaryHelper.WriteUInt64LE(tail20.AsSpan(0, 8), nextTxId++);
                            BinaryHelper.WriteUInt32LE(tail20.AsSpan(8, 4), currentOpCount);
                            BinaryHelper.WriteUInt32LE(tail20.AsSpan(12, 4), payloadLen);
                            BinaryHelper.WriteUInt32LE(tail20.AsSpan(16, 4), pCrc);

                            uint fHdrCrc = Crc32.Compute(tail20);
                            BinaryHelper.WriteUInt32LE(frameHeader.AsSpan(4, 4), fHdrCrc);
                            Array.Copy(tail20, 0, frameHeader, 8, 20);

                            ms.Write(frameHeader, 0, 28);
                            ms.Write(payloadBytes, 0, payloadBytes.Length);

                            currentPayload.SetLength(0);
                            currentOpCount = 0;
                        };

                        // Sort namespaces by UTF-8 bytes
                        var sortedNsList = new List<KeyValuePair<string, NamespaceStore>>(_namespaces);
                        sortedNsList.Sort((a, b) =>
                        {
                            byte[] ab = Encoding.UTF8.GetBytes(a.Key);
                            byte[] bb = Encoding.UTF8.GetBytes(b.Key);
                            return ab.AsSpan().SequenceCompareTo(bb.AsSpan());
                        });

                        foreach (var nsEntry in sortedNsList)
                        {
                            byte[] nsBytes = Encoding.UTF8.GetBytes(nsEntry.Key);
                            foreach (var item in nsEntry.Value.Items)
                            {
                                int entryLen = Constants.EntryFixedHeaderLen + nsBytes.Length + item.Key.Length + item.Value.Length;
                                if (currentPayload.Length + entryLen > Constants.MaxPayloadLen || currentOpCount >= Constants.MaxOpsPerFrame)
                                {
                                    flushFrame();
                                }

                                byte[] entryHdr = new byte[10];
                                entryHdr[0] = Constants.OpPut;
                                entryHdr[1] = item.ValueType;
                                BinaryHelper.WriteUInt16LE(entryHdr.AsSpan(2, 2), (ushort)nsBytes.Length);
                                BinaryHelper.WriteUInt16LE(entryHdr.AsSpan(4, 2), (ushort)item.Key.Length);
                                BinaryHelper.WriteUInt32LE(entryHdr.AsSpan(6, 4), (uint)item.Value.Length);

                                currentPayload.Write(entryHdr, 0, 10);
                                if (nsBytes.Length > 0) currentPayload.Write(nsBytes, 0, nsBytes.Length);
                                currentPayload.Write(item.Key, 0, item.Key.Length);
                                currentPayload.Write(item.Value, 0, item.Value.Length);
                                currentOpCount++;
                            }
                        }

                        if (currentOpCount > 0)
                        {
                            flushFrame();
                        }

                        _currentMaxTxId = nextTxId - 1;
                    }

                    // Write to .tmp with fsync
                    byte[] finalData = ms.ToArray();
                    using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        fs.Write(finalData, 0, finalData.Length);
                        if (_options.SyncOnWrite)
                        {
                            fs.Flush(true);
                        }
                    }
                }

                // Two-generation atomic rotation
                if (File.Exists(_path))
                {
                    try
                    {
                        if (File.Exists(bakPath)) File.Delete(bakPath);
                        File.Move(_path, bakPath);
                    }
                    catch
                    {
                        File.Copy(_path, bakPath, true);
                    }
                }

                try
                {
                    if (File.Exists(_path)) File.Delete(_path);
                    File.Move(tmpPath, _path);
                }
                catch
                {
                    File.Copy(tmpPath, _path, true);
                    try { File.Delete(tmpPath); } catch { }
                }
            }
        }

        private static void ValidateKeyAndValueLengths(byte[] key, byte[] val)
        {
            if (key.Length == 0 && val.Length == 0) return; // Empty key and empty val are permitted
            if (key.Length > Constants.MaxKeyLen)
                throw new KeyTooLargeException($"Key length {key.Length} exceeds maximum {Constants.MaxKeyLen}");
            if (val.Length > Constants.MaxValLen)
                throw new CkvException($"Value length {val.Length} exceeds maximum {Constants.MaxValLen}");
        }

        // ================= API Implementation =================

        public byte[]? GetBytes(string? ns, string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);

            _rwLock.EnterReadLock();
            try
            {
                if (!_namespaces.TryGetValue(normNs, out var store)) return null;
                int idx = store.FindIndex(keyBytes);
                if (idx < 0) return null;
                byte[] res = new byte[store.Items[idx].Value.Length];
                Array.Copy(store.Items[idx].Value, 0, res, 0, res.Length);
                return res;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        public string? GetString(string? ns, string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);

            _rwLock.EnterReadLock();
            try
            {
                if (!_namespaces.TryGetValue(normNs, out var store)) return null;
                int idx = store.FindIndex(keyBytes);
                if (idx < 0) return null;
                var item = store.Items[idx];
                if (item.ValueType != Constants.ValTypeString)
                    throw new TypeMismatchException($"Key '{key}' has value_type {item.ValueType}, expected string (0x01)");
                if (!Utf8Helper.IsValid(item.Value))
                    throw new InvalidUtf8Exception($"Invalid UTF-8 sequence in key '{key}'");
                return Encoding.UTF8.GetString(item.Value);
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        public long? GetInt64(string? ns, string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);

            _rwLock.EnterReadLock();
            try
            {
                if (!_namespaces.TryGetValue(normNs, out var store)) return null;
                int idx = store.FindIndex(keyBytes);
                if (idx < 0) return null;
                var item = store.Items[idx];
                if (item.ValueType != Constants.ValTypeInt64)
                    throw new TypeMismatchException($"Key '{key}' has value_type {item.ValueType}, expected int64 (0x02)");
                return BinaryHelper.DecodeInt64(item.Value);
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        public double? GetFloat64(string? ns, string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);

            _rwLock.EnterReadLock();
            try
            {
                if (!_namespaces.TryGetValue(normNs, out var store)) return null;
                int idx = store.FindIndex(keyBytes);
                if (idx < 0) return null;
                var item = store.Items[idx];
                if (item.ValueType != Constants.ValTypeFloat64)
                    throw new TypeMismatchException($"Key '{key}' has value_type {item.ValueType}, expected float64 (0x03)");
                return BinaryHelper.DecodeFloat64(item.Value);
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        public bool? GetBool(string? ns, string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);

            _rwLock.EnterReadLock();
            try
            {
                if (!_namespaces.TryGetValue(normNs, out var store)) return null;
                int idx = store.FindIndex(keyBytes);
                if (idx < 0) return null;
                var item = store.Items[idx];
                if (item.ValueType != Constants.ValTypeBool)
                    throw new TypeMismatchException($"Key '{key}' has value_type {item.ValueType}, expected bool (0x04)");
                return BinaryHelper.DecodeBool(item.Value);
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        public bool ContainsKey(string? ns, string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);

            _rwLock.EnterReadLock();
            try
            {
                if (!_namespaces.TryGetValue(normNs, out var store)) return false;
                return store.FindIndex(keyBytes) >= 0;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        public int Count(string? ns)
        {
            string normNs = Utf8Helper.NormalizeNamespace(ns);

            _rwLock.EnterReadLock();
            try
            {
                if (!_namespaces.TryGetValue(normNs, out var store)) return 0;
                return store.Items.Count;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        public void PutBytes(string? ns, string key, byte[] val)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (val == null) throw new ArgumentNullException(nameof(val));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            ValidateKeyAndValueLengths(keyBytes, val);

            List<KeyChangeEvent>? events = null;
            _rwLock.EnterWriteLock();
            try
            {
                if (!_namespaces.TryGetValue(normNs, out var store))
                {
                    store = new NamespaceStore();
                    _namespaces[normNs] = store;
                }
                store.Put(keyBytes, Constants.ValTypeBytes, val);
                FlushSnapshot();
                events = new List<KeyChangeEvent>
                {
                    new KeyChangeEvent(KeyChangeOp.Put, normNs, key)
                };
            }
            finally
            {
                _rwLock.ExitWriteLock();
                if (events != null)
                {
                    DispatchEvents(events);
                }
            }
        }

        public void PutString(string? ns, string key, string val)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (val == null) throw new ArgumentNullException(nameof(val));
            byte[] valBytes = Encoding.UTF8.GetBytes(val);
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            ValidateKeyAndValueLengths(keyBytes, valBytes);

            List<KeyChangeEvent>? events = null;
            _rwLock.EnterWriteLock();
            try
            {
                if (!_namespaces.TryGetValue(normNs, out var store))
                {
                    store = new NamespaceStore();
                    _namespaces[normNs] = store;
                }
                store.Put(keyBytes, Constants.ValTypeString, valBytes);
                FlushSnapshot();
                events = new List<KeyChangeEvent>
                {
                    new KeyChangeEvent(KeyChangeOp.Put, normNs, key)
                };
            }
            finally
            {
                _rwLock.ExitWriteLock();
                if (events != null)
                {
                    DispatchEvents(events);
                }
            }
        }

        public void PutInt64(string? ns, string key, long val)
        {
            byte[] valBytes = BinaryHelper.EncodeInt64(val);
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            ValidateKeyAndValueLengths(keyBytes, valBytes);

            List<KeyChangeEvent>? events = null;
            _rwLock.EnterWriteLock();
            try
            {
                if (!_namespaces.TryGetValue(normNs, out var store))
                {
                    store = new NamespaceStore();
                    _namespaces[normNs] = store;
                }
                store.Put(keyBytes, Constants.ValTypeInt64, valBytes);
                FlushSnapshot();
                events = new List<KeyChangeEvent>
                {
                    new KeyChangeEvent(KeyChangeOp.Put, normNs, key)
                };
            }
            finally
            {
                _rwLock.ExitWriteLock();
                if (events != null)
                {
                    DispatchEvents(events);
                }
            }
        }

        public void PutFloat64(string? ns, string key, double val)
        {
            byte[] valBytes = BinaryHelper.EncodeFloat64(val);
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            ValidateKeyAndValueLengths(keyBytes, valBytes);

            List<KeyChangeEvent>? events = null;
            _rwLock.EnterWriteLock();
            try
            {
                if (!_namespaces.TryGetValue(normNs, out var store))
                {
                    store = new NamespaceStore();
                    _namespaces[normNs] = store;
                }
                store.Put(keyBytes, Constants.ValTypeFloat64, valBytes);
                FlushSnapshot();
                events = new List<KeyChangeEvent>
                {
                    new KeyChangeEvent(KeyChangeOp.Put, normNs, key)
                };
            }
            finally
            {
                _rwLock.ExitWriteLock();
                if (events != null)
                {
                    DispatchEvents(events);
                }
            }
        }

        public void PutBool(string? ns, string key, bool val)
        {
            byte[] valBytes = BinaryHelper.EncodeBool(val);
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            ValidateKeyAndValueLengths(keyBytes, valBytes);

            List<KeyChangeEvent>? events = null;
            _rwLock.EnterWriteLock();
            try
            {
                if (!_namespaces.TryGetValue(normNs, out var store))
                {
                    store = new NamespaceStore();
                    _namespaces[normNs] = store;
                }
                store.Put(keyBytes, Constants.ValTypeBool, valBytes);
                FlushSnapshot();
                events = new List<KeyChangeEvent>
                {
                    new KeyChangeEvent(KeyChangeOp.Put, normNs, key)
                };
            }
            finally
            {
                _rwLock.ExitWriteLock();
                if (events != null)
                {
                    DispatchEvents(events);
                }
            }
        }

        public long IncrementInt64(string? ns, string key, long delta)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            ValidateKeyAndValueLengths(keyBytes, Array.Empty<byte>());

            List<KeyChangeEvent>? events = null;
            _rwLock.EnterWriteLock();
            try
            {
                if (!_namespaces.TryGetValue(normNs, out var store))
                {
                    store = new NamespaceStore();
                    _namespaces[normNs] = store;
                }

                int idx = store.FindIndex(keyBytes);
                long newVal;
                if (idx < 0)
                {
                    newVal = delta;
                    byte[] valBytes = BinaryHelper.EncodeInt64(newVal);
                    store.Put(keyBytes, Constants.ValTypeInt64, valBytes);
                }
                else
                {
                    var item = store.Items[idx];
                    if (item.ValueType != Constants.ValTypeInt64)
                    {
                        throw new TypeMismatchException($"Key '{key}' has value_type {item.ValueType}, expected int64 (0x02)");
                    }
                    long current = BinaryHelper.DecodeInt64(item.Value);
                    newVal = unchecked(current + delta);
                    byte[] valBytes = BinaryHelper.EncodeInt64(newVal);
                    store.Put(keyBytes, Constants.ValTypeInt64, valBytes);
                }

                FlushSnapshot();
                events = new List<KeyChangeEvent>
                {
                    new KeyChangeEvent(KeyChangeOp.Put, normNs, key)
                };
                return newVal;
            }
            finally
            {
                _rwLock.ExitWriteLock();
                if (events != null)
                {
                    DispatchEvents(events);
                }
            }
        }

        public bool Delete(string? ns, string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);

            List<KeyChangeEvent>? events = null;
            _rwLock.EnterWriteLock();
            try
            {
                if (!_namespaces.TryGetValue(normNs, out var store)) return false;
                bool deleted = store.Delete(keyBytes);
                if (deleted)
                {
                    FlushSnapshot();
                    events = new List<KeyChangeEvent>
                    {
                        new KeyChangeEvent(KeyChangeOp.Delete, normNs, key)
                    };
                }
                return deleted;
            }
            finally
            {
                _rwLock.ExitWriteLock();
                if (events != null)
                {
                    DispatchEvents(events);
                }
            }
        }

        public bool DropNamespace(string? ns)
        {
            string normNs = Utf8Helper.NormalizeNamespace(ns);

            List<KeyChangeEvent>? events = null;
            _rwLock.EnterWriteLock();
            try
            {
                if (!_namespaces.Remove(normNs)) return false;
                FlushSnapshot();
                events = new List<KeyChangeEvent>
                {
                    new KeyChangeEvent(KeyChangeOp.DropNamespace, normNs, string.Empty)
                };
                return true;
            }
            finally
            {
                _rwLock.ExitWriteLock();
                if (events != null)
                {
                    DispatchEvents(events);
                }
            }
        }

        public bool ClearNamespace(string? ns)
        {
            string normNs = Utf8Helper.NormalizeNamespace(ns);

            List<KeyChangeEvent>? events = null;
            _rwLock.EnterWriteLock();
            try
            {
                if (!_namespaces.TryGetValue(normNs, out var store) || store.Items.Count == 0)
                {
                    return false;
                }
                store.Items.Clear();
                FlushSnapshot();
                events = new List<KeyChangeEvent>
                {
                    new KeyChangeEvent(KeyChangeOp.Clear, normNs, string.Empty)
                };
                return true;
            }
            finally
            {
                _rwLock.ExitWriteLock();
                if (events != null)
                {
                    DispatchEvents(events);
                }
            }
        }

        public List<string> Keys(string? ns, string? prefix = null)
        {
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[]? prefixBytes = string.IsNullOrEmpty(prefix) ? null : Encoding.UTF8.GetBytes(prefix);

            _rwLock.EnterReadLock();
            try
            {
                var list = new List<string>();
                if (!_namespaces.TryGetValue(normNs, out var store)) return list;

                foreach (var item in store.Items)
                {
                    if (prefixBytes == null || item.Key.AsSpan().StartsWith(prefixBytes.AsSpan()))
                    {
                        list.Add(Encoding.UTF8.GetString(item.Key));
                    }
                }
                return list;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        public List<Entry> ScanRange(string? ns, string? startKey = null, string? endKey = null)
        {
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[]? startBytes = startKey != null ? Encoding.UTF8.GetBytes(startKey) : null;
            byte[]? endBytes = endKey != null ? Encoding.UTF8.GetBytes(endKey) : null;

            if (startBytes != null && endBytes != null && startBytes.AsSpan().SequenceCompareTo(endBytes.AsSpan()) >= 0)
            {
                return new List<Entry>();
            }

            _rwLock.EnterReadLock();
            try
            {
                var result = new List<Entry>();
                if (!_namespaces.TryGetValue(normNs, out var store)) return result;

                int startIdx = 0;
                if (startBytes != null)
                {
                    int found = store.FindIndex(startBytes);
                    startIdx = found >= 0 ? found : ~found;
                }

                for (int i = startIdx; i < store.Items.Count; i++)
                {
                    var item = store.Items[i];
                    if (endBytes != null && item.Key.AsSpan().SequenceCompareTo(endBytes.AsSpan()) >= 0)
                    {
                        break;
                    }
                    byte[] copyVal = new byte[item.Value.Length];
                    Array.Copy(item.Value, 0, copyVal, 0, copyVal.Length);
                    result.Add(new Entry(normNs, Encoding.UTF8.GetString(item.Key), item.ValueType, copyVal));
                }

                return result;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        public void Batch(Action<BatchWriter> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            var writer = new BatchWriter(this);
            action(writer);

            List<KeyChangeEvent>? events = null;
            _rwLock.EnterWriteLock();
            try
            {
                events = new List<KeyChangeEvent>();
                foreach (var op in writer.Ops)
                {
                    if (op.OpType == Constants.OpPut)
                    {
                        if (!_namespaces.TryGetValue(op.Namespace, out var store))
                        {
                            store = new NamespaceStore();
                            _namespaces[op.Namespace] = store;
                        }
                        store.Put(op.Key, op.ValType, op.Val);
                        events.Add(new KeyChangeEvent(KeyChangeOp.Put, op.Namespace, Encoding.UTF8.GetString(op.Key)));
                    }
                    else if (op.OpType == Constants.OpDelete)
                    {
                        if (_namespaces.TryGetValue(op.Namespace, out var store))
                        {
                            store.Delete(op.Key);
                        }
                        events.Add(new KeyChangeEvent(KeyChangeOp.Delete, op.Namespace, Encoding.UTF8.GetString(op.Key)));
                    }
                    else if (op.OpType == Constants.OpDropNamespace)
                    {
                        _namespaces.Remove(op.Namespace);
                        events.Add(new KeyChangeEvent(KeyChangeOp.DropNamespace, op.Namespace, string.Empty));
                    }
                    else if (op.OpType == Constants.OpClearNamespace)
                    {
                        if (_namespaces.TryGetValue(op.Namespace, out var store))
                        {
                            store.Items.Clear();
                        }
                        events.Add(new KeyChangeEvent(KeyChangeOp.Clear, op.Namespace, string.Empty));
                    }
                }
                FlushSnapshot();
            }
            finally
            {
                _rwLock.ExitWriteLock();
                if (events != null)
                {
                    DispatchEvents(events);
                }
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            if (_registered)
            {
                Registry.TryRemove(_canonicalPath, out _);
            }
            _rwLock.Dispose();
        }
    }

    public sealed class BatchWriter
    {
        private readonly CkvDb _db;
        internal List<BatchOp> Ops { get; } = new List<BatchOp>();

        internal BatchWriter(CkvDb db)
        {
            _db = db;
        }

        public BatchWriter PutBytes(string? ns, string key, byte[] val)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (val == null) throw new ArgumentNullException(nameof(val));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            Ops.Add(new BatchOp(Constants.OpPut, normNs, keyBytes, Constants.ValTypeBytes, val));
            return this;
        }

        public BatchWriter PutString(string? ns, string key, string val)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (val == null) throw new ArgumentNullException(nameof(val));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            byte[] valBytes = Encoding.UTF8.GetBytes(val);
            Ops.Add(new BatchOp(Constants.OpPut, normNs, keyBytes, Constants.ValTypeString, valBytes));
            return this;
        }

        public BatchWriter PutInt64(string? ns, string key, long val)
        {
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            byte[] valBytes = BinaryHelper.EncodeInt64(val);
            Ops.Add(new BatchOp(Constants.OpPut, normNs, keyBytes, Constants.ValTypeInt64, valBytes));
            return this;
        }

        public BatchWriter PutFloat64(string? ns, string key, double val)
        {
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            byte[] valBytes = BinaryHelper.EncodeFloat64(val);
            Ops.Add(new BatchOp(Constants.OpPut, normNs, keyBytes, Constants.ValTypeFloat64, valBytes));
            return this;
        }

        public BatchWriter PutBool(string? ns, string key, bool val)
        {
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            byte[] valBytes = BinaryHelper.EncodeBool(val);
            Ops.Add(new BatchOp(Constants.OpPut, normNs, keyBytes, Constants.ValTypeBool, valBytes));
            return this;
        }

        public BatchWriter Delete(string? ns, string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            Ops.Add(new BatchOp(Constants.OpDelete, normNs, keyBytes, 0, Array.Empty<byte>()));
            return this;
        }

        public BatchWriter DropNamespace(string? ns)
        {
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            Ops.Add(new BatchOp(Constants.OpDropNamespace, normNs, Array.Empty<byte>(), 0, Array.Empty<byte>()));
            return this;
        }

        public BatchWriter ClearNamespace(string? ns)
        {
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            Ops.Add(new BatchOp(Constants.OpClearNamespace, normNs, Array.Empty<byte>(), 0, Array.Empty<byte>()));
            return this;
        }
    }

    internal sealed class BatchOp
    {
        public byte OpType { get; }
        public string Namespace { get; }
        public byte[] Key { get; }
        public byte ValType { get; }
        public byte[] Val { get; }

        public BatchOp(byte opType, string @namespace, byte[] key, byte valType, byte[] val)
        {
            OpType = opType;
            Namespace = @namespace;
            Key = key;
            ValType = valType;
            Val = val;
        }
    }
}
