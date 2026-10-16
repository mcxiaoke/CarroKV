using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Ckv;
using Xunit;
using Xunit.Abstractions;

namespace Ckv.Tests
{
    public class SyncWalMatrixTests
    {
        private readonly ITestOutputHelper _output;

        public SyncWalMatrixTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private sealed class DotnetBenchResult
        {
            public string Name { get; set; } = "";
            public int TotalOps { get; set; }
            public double TotalDurationMs { get; set; }
            public double OpsPerSec { get; set; }
            public double AvgLatencyUs { get; set; }
            public double P50LatencyUs { get; set; }
            public double P99LatencyUs { get; set; }
            public long FinalFileSize { get; set; }
            public long LogicalBytes { get; set; }
            public double Waf { get; set; }
        }

        private DotnetBenchResult RunSinglePutBench(string name, StorageEngineMode engine, SyncMode sync, int opsCount)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ckv_dotnet_bench_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string dbPath = Path.Combine(tempDir, "bench_matrix.db");

            var opts = new DbOptions
            {
                EngineMode = engine,
                SyncMode = sync,
                SyncIntervalMs = 50
            };

            var latenciesUs = new List<double>(opsCount);
            long logicalBytes = 0;
            var swTotal = Stopwatch.StartNew();

            using (var db = CkvDb.Open(dbPath, opts))
            {
                var swOp = new Stopwatch();
                for (int i = 0; i < opsCount; i++)
                {
                    string key = $"bench_key_{i:D6}";
                    string val = $"bench_val_{i:D6}_padding_for_128_bytes_payload_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";
                    logicalBytes += System.Text.Encoding.UTF8.GetByteCount(key) + System.Text.Encoding.UTF8.GetByteCount(val);

                    swOp.Restart();
                    db.PutString("", key, val);
                    swOp.Stop();
                    latenciesUs.Add(swOp.Elapsed.TotalMilliseconds * 1000.0);
                }

                db.Flush();
            }

            swTotal.Stop();
            double totalDurMs = swTotal.Elapsed.TotalMilliseconds;

            latenciesUs.Sort();
            double avgLat = latenciesUs.Average();
            int p50Idx = (int)(opsCount * 0.50);
            int p99Idx = Math.Min(opsCount - 1, (int)(opsCount * 0.99));

            long finalSize = File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0;
            long physicalBytes;
            if (engine == StorageEngineMode.SnapshotAtomic && sync == SyncMode.Immediate)
            {
                physicalBytes = (long)(opsCount * (finalSize + 32.0) / 2.0);
            }
            else
            {
                physicalBytes = finalSize;
            }

            double waf = (double)physicalBytes / Math.Max(1, logicalBytes);
            double opsPerSec = opsCount / swTotal.Elapsed.TotalSeconds;

            try { Directory.Delete(tempDir, true); } catch { }

            return new DotnetBenchResult
            {
                Name = name,
                TotalOps = opsCount,
                TotalDurationMs = totalDurMs,
                OpsPerSec = opsPerSec,
                AvgLatencyUs = avgLat,
                P50LatencyUs = latenciesUs[p50Idx],
                P99LatencyUs = latenciesUs[p99Idx],
                FinalFileSize = finalSize,
                LogicalBytes = logicalBytes,
                Waf = waf
            };
        }

        [Fact]
        public void BenchmarkSyncModeAndWalMatrix()
        {
            int opsImmediate = 200;
            int opsDeferred = 3000;
            int opsManual = 5000;

            var configs = new[]
            {
                ("Snapshot + Immediate", StorageEngineMode.SnapshotAtomic, SyncMode.Immediate, opsImmediate),
                ("AppendOnlyLog + Immediate", StorageEngineMode.AppendOnlyLog, SyncMode.Immediate, opsImmediate),
                ("Snapshot + Deferred", StorageEngineMode.SnapshotAtomic, SyncMode.Deferred, opsDeferred),
                ("AppendOnlyLog + Deferred", StorageEngineMode.AppendOnlyLog, SyncMode.Deferred, opsDeferred),
                ("Snapshot + Manual", StorageEngineMode.SnapshotAtomic, SyncMode.Manual, opsManual),
                ("AppendOnlyLog + Manual", StorageEngineMode.AppendOnlyLog, SyncMode.Manual, opsManual),
            };

            var results = new List<DotnetBenchResult>();
            foreach (var cfg in configs)
            {
                results.Add(RunSinglePutBench(cfg.Item1, cfg.Item2, cfg.Item3, cfg.Item4));
            }

            _output.WriteLine("\n=========================================================================================");
            _output.WriteLine("                   CKV .NET STORAGE & SYNC ENGINE MATRIX BENCHMARK");
            _output.WriteLine("=========================================================================================");
            _output.WriteLine(string.Format("{0,-28} | {1,7} | {2,10} | {3,10} | {4,9} | {5,9} | {6,8}",
                "Configuration", "Ops", "Throughput", "Avg Latency", "P50 Lat", "P99 Lat", "WAF"));
            _output.WriteLine("----------------------------+---------+------------+------------+-----------+-----------+----------");
            foreach (var r in results)
            {
                _output.WriteLine(string.Format("{0,-28} | {1,7} | {2,8:F0} o/s | {3,8:F2} us | {4,7:F2} us | {5,7:F2} us | {6,7:F2}x",
                    r.Name, r.TotalOps, r.OpsPerSec, r.AvgLatencyUs, r.P50LatencyUs, r.P99LatencyUs, r.Waf));
            }
            _output.WriteLine("=========================================================================================\n");
        }
    }
}
