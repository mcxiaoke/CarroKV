package ckv

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"testing"
	"time"
)

type goBenchResult struct {
	name          string
	totalOps      int
	durationMs    float64
	opsPerSec     float64
	avgLatencyUs  float64
	p50LatencyUs  float64
	p99LatencyUs  float64
	finalFileSize int64
	logicalBytes  int64
	waf           float64
}

func runGoSinglePutBench(name string, engine StorageEngineMode, sync SyncMode, opsCount int) (*goBenchResult, error) {
	dir, err := os.MkdirTemp("", "ckv_go_bench_*")
	if err != nil {
		return nil, err
	}
	defer os.RemoveAll(dir)
	dbPath := filepath.Join(dir, "bench_matrix.db")

	opts := DefaultDbOptions()
	opts.EngineMode = engine
	opts.SyncMode = sync
	opts.SyncIntervalMs = 50

	db, err := Open(dbPath, opts)
	if err != nil {
		return nil, err
	}

	latenciesUs := make([]float64, opsCount)
	totalStart := time.Now()
	var logicalBytes int64

	for i := 0; i < opsCount; i++ {
		key := fmt.Sprintf("bench_key_%06d", i)
		val := fmt.Sprintf("bench_val_%06d_padding_for_128_bytes_payload_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", i)
		logicalBytes += int64(len(key) + len(val))

		opStart := time.Now()
		if err := db.PutString("", key, val); err != nil {
			_ = db.Close()
			return nil, err
		}
		latenciesUs[i] = float64(time.Since(opStart).Microseconds())
	}

	if err := db.Flush(); err != nil {
		_ = db.Close()
		return nil, err
	}
	_ = db.Close()

	totalDur := time.Since(totalStart)
	totalDurMs := float64(totalDur.Milliseconds())

	sort.Float64s(latenciesUs)
	var sumLat float64
	for _, l := range latenciesUs {
		sumLat += l
	}
	avgLat := sumLat / float64(opsCount)
	p50Idx := int(float64(opsCount) * 0.50)
	p99Idx := int(float64(opsCount) * 0.99)
	if p99Idx >= opsCount {
		p99Idx = opsCount - 1
	}

	fi, err := os.Stat(dbPath)
	var finalSize int64
	if err == nil {
		finalSize = fi.Size()
	}

	var physicalBytes int64
	if engine == StorageEngineSnapshotAtomic && sync == SyncModeImmediate {
		physicalBytes = int64(float64(opsCount) * float64(finalSize+32) / 2.0)
	} else {
		physicalBytes = finalSize
	}

	waf := float64(physicalBytes) / float64(logicalBytes)
	opsPerSec := float64(opsCount) / totalDur.Seconds()

	return &goBenchResult{
		name:          name,
		totalOps:      opsCount,
		durationMs:    totalDurMs,
		opsPerSec:     opsPerSec,
		avgLatencyUs:  avgLat,
		p50LatencyUs:  latenciesUs[p50Idx],
		p99LatencyUs:  latenciesUs[p99Idx],
		finalFileSize: finalSize,
		logicalBytes:  logicalBytes,
		waf:           waf,
	}, nil
}

func TestBenchmarkSyncModeAndWalMatrix(t *testing.T) {
	if testing.Short() {
		t.Skip("skipping matrix benchmark in short mode")
	}

	opsImmediate := 200
	opsDeferred := 3000
	opsManual := 5000

	configs := []struct {
		name   string
		engine StorageEngineMode
		sync   SyncMode
		ops    int
	}{
		{"Snapshot + Immediate", StorageEngineSnapshotAtomic, SyncModeImmediate, opsImmediate},
		{"AppendOnlyLog + Immediate", StorageEngineAppendOnlyLog, SyncModeImmediate, opsImmediate},
		{"Snapshot + Deferred", StorageEngineSnapshotAtomic, SyncModeDeferred, opsDeferred},
		{"AppendOnlyLog + Deferred", StorageEngineAppendOnlyLog, SyncModeDeferred, opsDeferred},
		{"Snapshot + Manual", StorageEngineSnapshotAtomic, SyncModeManual, opsManual},
		{"AppendOnlyLog + Manual", StorageEngineAppendOnlyLog, SyncModeManual, opsManual},
	}

	var results []*goBenchResult
	for _, cfg := range configs {
		res, err := runGoSinglePutBench(cfg.name, cfg.engine, cfg.sync, cfg.ops)
		if err != nil {
			t.Fatalf("bench %s failed: %v", cfg.name, err)
		}
		results = append(results, res)
	}

	fmt.Printf("\n=========================================================================================\n")
	fmt.Printf("                    CKV GO STORAGE & SYNC ENGINE MATRIX BENCHMARK\n")
	fmt.Printf("=========================================================================================\n")
	fmt.Printf("%-28s | %7s | %10s | %10s | %9s | %9s | %8s\n",
		"Configuration", "Ops", "Throughput", "Avg Latency", "P50 Lat", "P99 Lat", "WAF")
	fmt.Printf("----------------------------+---------+------------+------------+-----------+-----------+----------\n")
	for _, r := range results {
		fmt.Printf("%-28s | %7d | %8.0f o/s | %8.2f us | %7.2f us | %7.2f us | %7.2fx\n",
			r.name, r.totalOps, r.opsPerSec, r.avgLatencyUs, r.p50LatencyUs, r.p99LatencyUs, r.waf)
	}
	fmt.Printf("=========================================================================================\n\n")
}
