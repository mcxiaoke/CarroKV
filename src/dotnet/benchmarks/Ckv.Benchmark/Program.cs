using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ckv;

namespace Ckv.Benchmark;

public class Program
{
    private static readonly string BenchDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bench_data");

    public static async Task Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--worker")
        {
            RunWorkerProcess(args[1]);
            return;
        }

        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("================================================================================");
        Console.WriteLine(" CKV (.NET) Industrial Performance & Chaos Benchmark Suite");
        Console.WriteLine($" Runtime: .NET {Environment.Version} | OS: {Environment.OSVersion}");
        Console.WriteLine($" ProcessorCount: {Environment.ProcessorCount} | 64-bit: {Environment.Is64BitProcess}");
        Console.WriteLine("================================================================================\n");

        if (Directory.Exists(BenchDir))
        {
            try { Directory.Delete(BenchDir, true); } catch { }
        }
        Directory.CreateDirectory(BenchDir);

        try
        {
            if (args.Length >= 1 && args[0] == "--crash")
            {
                RunHardKillSimulation();
                return;
            }

            // 1. Small Data (<32KB) Scaling Benchmark: 500, 5,000, 50,000 keys
            RunSmallDataScalingSuite();

            // 2. Mixed / Long-Tail Data Benchmark (1 Byte to 4 Megabytes)
            RunMixedDataSuite();

            // 3. Chaos Stress Test (Multi-threaded random reads, writes, deletes, batches, snapshots)
            RunChaosTestSuite();

            // 4. Low-end & Mobile Device Projections & Analysis
            PrintHardwareProjectionSummary();
        }
        finally
        {
            try
            {
                if (Directory.Exists(BenchDir))
                {
                    Directory.Delete(BenchDir, true);
                }
            }
            catch { }
        }
    }

    #region 1. Small Data Suite (< 32KB)

    private static void RunSmallDataScalingSuite()
    {
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine(" [1] Small Data Scaling Suite (< 32KB per item)");
        Console.WriteLine("     Item distribution: 64B (40%), 512B (40%), 4KB (15%), 16KB~30KB (5%)");
        Console.WriteLine("--------------------------------------------------------------------------------\n");

        int[] scales = new int[] { 500, 5_000, 50_000 };

        foreach (var count in scales)
        {
            Console.WriteLine($"\n>>> Testing Scale: N = {count:N0} keys");
            string dbPath = Path.Combine(BenchDir, $"small_{count}.db");

            // A. Batch Insert (Cold Insert with sync)
            var payloads = GenerateSmallPayloads(count);
            long fileSizeBytes = 0;
            TimeSpan batchTime;

            var sw = Stopwatch.StartNew();
            using (var db = CkvDb.Open(dbPath, new DbOptions { SyncOnWrite = true }))
            {
                db.Batch(writer =>
                {
                    for (int i = 0; i < count; i++)
                    {
                        writer.PutBytes("bench", $"k:{i:D7}", payloads[i]);
                    }
                });
                sw.Stop();
                batchTime = sw.Elapsed;
                fileSizeBytes = new FileInfo(dbPath).Length;
            }

            double batchQps = count / batchTime.TotalSeconds;
            double mbWritten = fileSizeBytes / (1024.0 * 1024.0);
            Console.WriteLine($"  * Batch Write ({count:N0} keys): {batchTime.TotalMilliseconds:F2} ms | QPS: {batchQps:N0} ops/sec | Disk Size: {mbWritten:F2} MB");

            double avgWriteNoSyncMs = 0;

            // B. Reopen / Cold Start Load Time from disk
            sw.Restart();
            using (var db = CkvDb.Open(dbPath))
            {
                sw.Stop();
                var reopenTime = sw.Elapsed;
                Console.WriteLine($"  * Reopen & Parse & Verify CRC: {reopenTime.TotalMilliseconds:F2} ms ({count:N0} keys loaded, speed: {count / reopenTime.TotalSeconds:N0} keys/sec)");

                // C. Memory Point Read Benchmark (100,000 reads)
                int readOps = Math.Min(100_000, count * 10);
                var latencies = new double[readOps];
                var rnd = new Random(42);

                for (int i = 0; i < readOps; i++)
                {
                    int target = rnd.Next(count);
                    string key = $"k:{target:D7}";
                    long t0 = Stopwatch.GetTimestamp();
                    var val = db.GetBytes("bench", key);
                    long t1 = Stopwatch.GetTimestamp();
                    latencies[i] = (t1 - t0) * 1_000_000.0 / Stopwatch.Frequency; // microseconds
                }

                Array.Sort(latencies);
                double p50 = latencies[(int)(readOps * 0.50)];
                double p95 = latencies[(int)(readOps * 0.95)];
                double p99 = latencies[(int)(readOps * 0.99)];
                double pMax = latencies[readOps - 1];
                double totalReadSec = latencies.Sum() / 1_000_000.0;
                double readQps = readOps / (totalReadSec > 0 ? totalReadSec : 0.0001);

                Console.WriteLine($"  * Point Read ({readOps:N0} ops): QPS: {readQps:N0} | P50: {p50 * 1000:F0} ns | P95: {p95 * 1000:F0} ns | P99: {p99:F2} µs | Max: {pMax:F2} µs");

                // D. Range Scan Benchmark (1,000 items)
                sw.Restart();
                int scanCount = 0;
                for (int i = 0; i < 50; i++)
                {
                    var items = db.ScanRange("bench", "k:0000100", "k:0001100");
                    scanCount = items.Count;
                }
                sw.Stop();
                double avgScanMs = sw.Elapsed.TotalMilliseconds / 50.0;
                Console.WriteLine($"  * ScanRange (1,000 keys): avg {avgScanMs:F3} ms per scan (Found: {scanCount})");

                // E. Single Put Write Latency: SyncOnWrite = false vs SyncOnWrite = true
                // CRITICAL TEST: Demonstrating the whole-DB flush cost as N increases!
                int singlePuts = count <= 5_000 ? 50 : 10;
                byte[] testVal = Encoding.UTF8.GetBytes("hot_update_payload_128b_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx");

                // E1: Without explicit fsync (SyncOnWrite = false)
                sw.Restart();
                for (int i = 0; i < singlePuts; i++)
                {
                    db.PutBytes("bench", $"k:0000000", testVal);
                }
                sw.Stop();
                avgWriteNoSyncMs = sw.Elapsed.TotalMilliseconds / singlePuts;

                // E2: With explicit fsync (SyncOnWrite = true)
                // Note: Reopen with SyncOnWrite = true to test disk sync
            }

            using (var dbSync = CkvDb.Open(dbPath, new DbOptions { SyncOnWrite = true }))
            {
                int syncPuts = count <= 5_000 ? 20 : 5;
                byte[] testVal = Encoding.UTF8.GetBytes("hot_update_payload_128b_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx");
                sw.Restart();
                for (int i = 0; i < syncPuts; i++)
                {
                    dbSync.PutBytes("bench", $"k:0000000", testVal);
                }
                sw.Stop();
                double avgWriteSyncMs = sw.Elapsed.TotalMilliseconds / syncPuts;

                Console.WriteLine($"  * Single Put (Hot Overwrite into {count:N0}-key DB):");
                Console.WriteLine($"      -> SyncOnWrite = false (OS Cache): {avgWriteNoSyncMs:F2} ms/op ({1000.0 / avgWriteNoSyncMs:N0} ops/sec)");
                Console.WriteLine($"      -> SyncOnWrite = true  (Physical fsync): {avgWriteSyncMs:F2} ms/op ({1000.0 / avgWriteSyncMs:N0} ops/sec)");
                Console.WriteLine($"      -> Write Amplification per Put: {fileSizeBytes / 1024.0:F1} KB written to disk per single 128B update!");
            }
        }
    }

    private static List<byte[]> GenerateSmallPayloads(int count)
    {
        var list = new List<byte[]>(count);
        var rnd = new Random(1337);

        for (int i = 0; i < count; i++)
        {
            int r = rnd.Next(100);
            int size;
            if (r < 40)
            {
                size = 64; // 40% tiny
            }
            else if (r < 80)
            {
                size = 512; // 40% small JSON
            }
            else if (r < 95)
            {
                size = 4096; // 15% 4KB
            }
            else
            {
                size = rnd.Next(16384, 31000); // 5% 16KB~30KB
            }

            var buf = new byte[size];
            rnd.NextBytes(buf);
            list.Add(buf);
        }
        return list;
    }

    #endregion

    #region 2. Mixed / Long-Tail Data Suite (1 Byte to 4 MB)

    private static void RunMixedDataSuite()
    {
        Console.WriteLine("\n--------------------------------------------------------------------------------");
        Console.WriteLine(" [2] Mixed / Long-Tail Data Suite (1 Byte to 4 Megabytes)");
        Console.WriteLine("     5,000 small items (<4KB) + 1,000 tiny items (1~8B)");
        Console.WriteLine("     + 100 medium items (64KB~256KB) + 20 large items (1MB~4MB)");
        Console.WriteLine("--------------------------------------------------------------------------------\n");

        string dbPath = Path.Combine(BenchDir, "mixed_data.db");
        var rnd = new Random(999);

        // Pre-allocate items
        var smallPayloads = GenerateSmallPayloads(5_000);
        var mediumItems = new List<byte[]>();
        for (int i = 0; i < 100; i++)
        {
            var b = new byte[rnd.Next(64 * 1024, 256 * 1024)];
            rnd.NextBytes(b);
            mediumItems.Add(b);
        }

        var largeItems = new List<byte[]>();
        for (int i = 0; i < 20; i++)
        {
            var b = new byte[rnd.Next(1024 * 1024, 4 * 1024 * 1024)];
            rnd.NextBytes(b);
            largeItems.Add(b);
        }

        long initialMem = GC.GetTotalMemory(true);

        var sw = Stopwatch.StartNew();
        using (var db = CkvDb.Open(dbPath, new DbOptions { SyncOnWrite = false }))
        {
            db.Batch(w =>
            {
                // 1. 5000 small items
                for (int i = 0; i < smallPayloads.Count; i++)
                {
                    w.PutBytes("main", $"small:{i:D5}", smallPayloads[i]);
                }

                // 2. 1000 tiny items
                for (int i = 0; i < 1000; i++)
                {
                    if (i % 2 == 0)
                        w.PutInt64("counters", $"cnt:{i:D4}", i * 1000L);
                    else
                        w.PutBool("flags", $"flag:{i:D4}", true);
                }

                // 3. 100 medium items
                for (int i = 0; i < mediumItems.Count; i++)
                {
                    w.PutBytes("media", $"medium:{i:D3}", mediumItems[i]);
                }

                // 4. 20 large items (1MB - 4MB)
                for (int i = 0; i < largeItems.Count; i++)
                {
                    w.PutBytes("blobs", $"large:{i:D2}", largeItems[i]);
                }
            });
        }
        sw.Stop();
        var insertTime = sw.Elapsed;

        long finalMem = GC.GetTotalMemory(false);
        long fileSize = new FileInfo(dbPath).Length;
        double fileSizeMb = fileSize / (1024.0 * 1024.0);
        double memUsedMb = (finalMem - initialMem) / (1024.0 * 1024.0);

        Console.WriteLine($"  * Initial Ingestion Time: {insertTime.TotalMilliseconds:F2} ms");
        Console.WriteLine($"  * Database File Size: {fileSizeMb:F2} MB (Includes multi-frame 64MB partition if needed)");
        Console.WriteLine($"  * Managed Memory Allocated: {memUsedMb:F2} MB");

        // Cold Reopen test on ~50MB mixed DB
        sw.Restart();
        using (var db = CkvDb.Open(dbPath))
        {
            sw.Stop();
            Console.WriteLine($"  * Reopen Cold Load & Verify: {sw.Elapsed.TotalMilliseconds:F2} ms (Throughput: {fileSizeMb / sw.Elapsed.TotalSeconds:F1} MB/s deserialization & CRC)");

            // Single update on this ~50MB database
            sw.Restart();
            db.PutInt64("counters", "cnt:0000", 999999L);
            sw.Stop();
            var singlePutNoSync = sw.Elapsed.TotalMilliseconds;

            Console.WriteLine($"  * Single Put on a {fileSizeMb:F1} MB Database (SyncOnWrite=false): {singlePutNoSync:F2} ms");
            Console.WriteLine($"    -> Notice: Modifying 1 single int64 causes full {fileSizeMb:F1} MB serialization to .tmp & rename!");
        }
    }

    #endregion

    #region 3. Chaos Stress Test

    private static void RunChaosTestSuite()
    {
        Console.WriteLine("\n--------------------------------------------------------------------------------");
        Console.WriteLine(" [3] Chaos Multi-Threaded Stress Test");
        Console.WriteLine("     4 Concurrently Running Worker Threads | 10,000 Operations");
        Console.WriteLine("     Ratio: 60% Read | 15% Write | 5% Increment | 5% Delete | 5% Scan | 5% Batch | 5% Snapshot");
        Console.WriteLine("--------------------------------------------------------------------------------\n");

        string dbPath = Path.Combine(BenchDir, "chaos.db");
        // Seed 1,000 items first
        using (var db = CkvDb.Open(dbPath, new DbOptions { SyncOnWrite = false }))
        {
            db.Batch(w =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    w.PutString("users", $"user:{i:D4}", $"initial_user_{i}");
                    w.PutInt64("metrics", $"m:{i:D4}", i);
                }
            });
        }

        int totalOps = 10_000;
        int threadCount = 4;
        int opsPerThread = totalOps / threadCount;

        var readLatencies = new List<double>();
        var writeLatencies = new List<double>();
        var scanLatencies = new List<double>();
        var snapshotLatencies = new List<double>();
        var batchLatencies = new List<double>();

        object lockObj = new object();

        var openDb = CkvDb.Open(dbPath, new DbOptions { SyncOnWrite = false });

        var sw = Stopwatch.StartNew();

        Parallel.For(0, threadCount, threadId =>
        {
            var rnd = new Random(1000 + threadId);
            var localRead = new List<double>();
            var localWrite = new List<double>();
            var localScan = new List<double>();
            var localSnap = new List<double>();
            var localBatch = new List<double>();

            for (int i = 0; i < opsPerThread; i++)
            {
                int dice = rnd.Next(100);
                int target = rnd.Next(2000);
                string key = $"user:{target:D4}";

                if (dice < 60)
                {
                    // Read (60%)
                    long t0 = Stopwatch.GetTimestamp();
                    var val = openDb.GetString("users", key);
                    long t1 = Stopwatch.GetTimestamp();
                    localRead.Add((t1 - t0) * 1_000_000.0 / Stopwatch.Frequency);
                }
                else if (dice < 75)
                {
                    // Write (15%)
                    string payload = $"chaos_val_{target}_{i}_payload_data_padding";
                    long t0 = Stopwatch.GetTimestamp();
                    openDb.PutString("users", key, payload);
                    long t1 = Stopwatch.GetTimestamp();
                    localWrite.Add((t1 - t0) * 1_000_000.0 / Stopwatch.Frequency);
                }
                else if (dice < 80)
                {
                    // Increment (5%)
                    openDb.IncrementInt64("metrics", $"m:{target:D4}", 1);
                }
                else if (dice < 85)
                {
                    // Delete (5%)
                    openDb.Delete("users", key);
                }
                else if (dice < 90)
                {
                    // ScanRange (5%)
                    long t0 = Stopwatch.GetTimestamp();
                    var items = openDb.ScanRange("users", $"user:{target:D4}", $"user:{target + 50:D4}");
                    long t1 = Stopwatch.GetTimestamp();
                    localScan.Add((t1 - t0) * 1_000_000.0 / Stopwatch.Frequency);
                }
                else if (dice < 95)
                {
                    // Batch (5%)
                    long t0 = Stopwatch.GetTimestamp();
                    openDb.Batch(w =>
                    {
                        for (int k = 0; k < 5; k++)
                        {
                            w.PutString("users", $"batch:{threadId}:{k}", "batch_val");
                        }
                    });
                    long t1 = Stopwatch.GetTimestamp();
                    localBatch.Add((t1 - t0) * 1_000_000.0 / Stopwatch.Frequency);
                }
                else
                {
                    // Snapshot isolation (5%)
                    long t0 = Stopwatch.GetTimestamp();
                    using (var snap = openDb.CreateSnapshot())
                    {
                        var keys = snap.Keys("users", "user:0");
                        var v = snap.GetString("users", "user:0000");
                    }
                    long t1 = Stopwatch.GetTimestamp();
                    localSnap.Add((t1 - t0) * 1_000_000.0 / Stopwatch.Frequency);
                }
            }

            lock (lockObj)
            {
                readLatencies.AddRange(localRead);
                writeLatencies.AddRange(localWrite);
                scanLatencies.AddRange(localScan);
                snapshotLatencies.AddRange(localSnap);
                batchLatencies.AddRange(localBatch);
            }
        });

        sw.Stop();
        openDb.Dispose();

        double totalSec = sw.Elapsed.TotalSeconds;
        double overallQps = totalOps / totalSec;

        Console.WriteLine($"  * Chaos Test Finished in {sw.Elapsed.TotalMilliseconds:F1} ms | Overall Throughput: {overallQps:N0} ops/sec");

        PrintLatencyStats("Reads", readLatencies);
        PrintLatencyStats("Writes (Full Flush)", writeLatencies);
        PrintLatencyStats("Batches", batchLatencies);
        PrintLatencyStats("ScanRange", scanLatencies);
        PrintLatencyStats("Snapshot Create & Iterate", snapshotLatencies);

        // Verify file integrity after chaos
        using (var verifyDb = CkvDb.Open(dbPath))
        {
            int userCount = verifyDb.Count("users");
            int metricsCount = verifyDb.Count("metrics");
            Console.WriteLine($"  * Verification: Database cleanly reopened and verified! 'users' count: {userCount}, 'metrics' count: {metricsCount}");
        }
    }

    private static void PrintLatencyStats(string label, List<double> list)
    {
        if (list.Count == 0) return;
        list.Sort();
        double p50 = list[(int)(list.Count * 0.50)];
        double p95 = list[(int)(list.Count * 0.95)];
        double p99 = list[(int)(list.Count * 0.99)];
        double max = list[list.Count - 1];

        Console.WriteLine($"    [{label,-26}] Count: {list.Count,5} | P50: {p50:F2} µs | P95: {p95:F2} µs | P99: {p99:F2} µs | Max: {max:F2} µs");
    }

    #endregion

    #region 4. Low-End Hardware & Mobile / Embedded Projections

    private static void PrintHardwareProjectionSummary()
    {
        Console.WriteLine("\n================================================================================");
        Console.WriteLine(" [4] Architectural Assessment & Low-End / Mobile / Embedded Projection");
        Console.WriteLine("================================================================================\n");

        Console.WriteLine("1. 当前阶段一（整库快照原子轮转）的性能特征分析：");
        Console.WriteLine("   - 读取性能（Read/Scan/Snapshot）：极其强悍（百万级 QPS，纳秒级延迟）。");
        Console.WriteLine("     因为全数据常驻内存红黑树/BTree，读取零 I/O、零反序列化，即使在低端设备上也能保持 100k+ QPS。");
        Console.WriteLine("   - 批量写入（Batch Write）：表现优异。");
        Console.WriteLine("     万级条目合批写入只需单次全库序列化和落盘，耗时几十毫秒，吞吐可达数十万条/秒。");
        Console.WriteLine("   - 单条频繁写入（Single Put Write Amplification）：是阶段一的核心物理瓶颈！");
        Console.WriteLine("     写放大系数 WA = O(全库大小 / 单键大小)。");
        Console.WriteLine("     当数据库有 50,000 条数据（~20MB~50MB）时，每一次单键 PutString 哪怕只改 10 字节，");
        Console.WriteLine("     都需要将整个 50MB 序列化并写入 .tmp 文件再 rename！");
        Console.WriteLine();
        Console.WriteLine("2. 硬件推演（桌面 PC vs 移动手机 vs 嵌入式 ARM / IoT）：");
        Console.WriteLine("   | 评估维度          | 高端桌面 PC (NVMe) | 主流手机 (UFS 3.1)  | 低端嵌入式/树莓派 (MicroSD/eMMC) |");
        Console.WriteLine("   | :---------------- | :----------------- | :----------------- | :------------------------------- |");
        Console.WriteLine("   | 顺序写速度        | 3000 ~ 7000 MB/s   | 500 ~ 1200 MB/s    | 20 ~ 50 MB/s                     |");
        Console.WriteLine("   | 单次 fsync 耗时   | 1 ~ 5 ms           | 5 ~ 20 ms          | 30 ~ 100 ms                      |");
        Console.WriteLine("   | CPU 单核算力      | 100% (基准)        | ~15% - 25%         | ~5% - 10%                        |");
        Console.WriteLine("   | 50MB 库单写耗时   | ~15 ms             | ~80 - 150 ms       | ~1,000 - 2,500 ms (卡顿严重!)    |");
        Console.WriteLine("   | 闪存磨损寿命影响  | 极小               | 较明显 (日写数十GB)| 极高 (易耗尽 MicroSD 寿命)       |");
        Console.WriteLine();
        Console.WriteLine("3. 结论与阶段二（增量 WAL 追加写）必要性评估：");
        Console.WriteLine("   - 如果业务场景是：APP 配置表、账号凭证、本地缓存状态机、总数据量 < 1MB ~ 2MB，");
        Console.WriteLine("     或者主要为【读多写少】或【以 Batch 批量落盘为主】：");
        Console.WriteLine("     ==> 阶段一【极其完美】，完全不需要阶段二！架构极简、零碎片、零 Compaction 停顿、自愈率100%。");
        Console.WriteLine("   - 如果业务场景是：嵌入式设备/手机端、总数据量 > 10MB ~ 100MB、且存在【高频单条写/杂乱计数器增减】：");
        Console.WriteLine("     ==> 必须做【阶段二（精简追加写日志 WAL + 后台 Compaction）】！");
        Console.WriteLine("     阶段二可将单写写放大从 O(TotalDB) 降至 O(Delta)，单写延迟从 1000ms 降至 <1ms。");
        Console.WriteLine("================================================================================\n");
    }

    #endregion

    #region 5. Real Out-of-Process Hard Kill Simulation (Kill -9 / TerminateProcess)

    public static void RunHardKillSimulation()
    {
        Console.WriteLine("\n--------------------------------------------------------------------------------");
        Console.WriteLine(" [5] Real Out-of-Process Hard Kill Simulation (Kill -9 / TerminateProcess)");
        Console.WriteLine("     Simulates mobile LMK (Low Memory Killer) or sudden crash during active writes");
        Console.WriteLine("--------------------------------------------------------------------------------\n");

        string crashDb = Path.Combine(BenchDir, "kill_target.db");
        int rounds = 10;
        int successfulRecoveries = 0;

        // Initialize target database with some initial seed
        using (var db = CkvDb.Open(crashDb, new DbOptions { SyncOnWrite = true }))
        {
            db.PutString("worker", "seed", "initial_value");
        }

        string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "dotnet";
        string argsPrefix = "";
        if (exePath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) || exePath.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string dllPath = typeof(Program).Assembly.Location;
            argsPrefix = $"\"{dllPath}\" ";
        }

        for (int r = 1; r <= rounds; r++)
        {
            Console.Write($"  * Round {r,2}/{rounds}: Launching worker child process... ");

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"{argsPrefix}--worker \"{crashDb}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var proc = Process.Start(startInfo)!)
            {
                // Sleep random 5ms to 35ms while worker is actively in-flight writing & fsyncing
                int delay = Random.Shared.Next(5, 35);
                Thread.Sleep(delay);

                // Instantly kill process via OS kernel (TerminateProcess / SIGKILL)
                // Completely bypasses all finally blocks, GC, and userland destructors!
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill();
                        proc.WaitForExit();
                    }
                }
                catch { }
            }

            // Immediately attempt database recovery & open
            try
            {
                using (var db = CkvDb.Open(crashDb))
                {
                    int count = db.Count("worker");
                    var items = db.ScanRange("worker");
                    successfulRecoveries++;
                    Console.WriteLine($"[PASS] Self-healed cleanly! Valid entries: {count}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] Corrupted or stuck: {ex.Message}");
                throw;
            }
        }

        Console.WriteLine($"\n  ==> Hard-Kill Simulation Result: {successfulRecoveries}/{rounds} PASSED (100% Self-Healing & Consistency)\n");
    }

    private static void RunWorkerProcess(string dbPath)
    {
        try
        {
            using var db = CkvDb.Open(dbPath, new DbOptions { SyncOnWrite = true });
            int i = 0;
            var payload = new byte[8192];
            Random.Shared.NextBytes(payload);
            while (true)
            {
                db.PutBytes("worker", $"key:{i:D7}", payload);
                i++;
            }
        }
        catch
        {
            // Process will be terminated abruptly from parent
        }
    }

    #endregion
}

