package ckv

import (
	"bytes"
	"fmt"
	"path/filepath"
	"testing"
)

// 1. fjall & rocksdb 范式：同一 Batch 内对同一 Key 连续多次覆写、删除并重新赋值
func TestGoBatchSameKeyOverwritesAndTombstones(t *testing.T) {
	tempDir := t.TempDir()
	dbPath := filepath.Join(tempDir, "batch_overwrite.db")
	db, err := Open(dbPath)
	if err != nil {
		t.Fatalf("Open failed: %v", err)
	}
	defer db.Close()

	// 预先写入初始值
	if err := db.PutString("ns1", "target_key", "initial_v0"); err != nil {
		t.Fatalf("PutString failed: %v", err)
	}
	if err := db.PutString("ns1", "stay_key", "stay_v0"); err != nil {
		t.Fatalf("PutString failed: %v", err)
	}

	// 在 batch 中进行一系列覆写和删除
	err = db.Batch(func(b *BatchWriter) error {
		_ = b.PutString("ns1", "target_key", "temp_v1")
		_ = b.PutString("ns1", "target_key", "temp_v2")
		_ = b.Delete("ns1", "target_key")
		_ = b.PutString("ns1", "target_key", "final_v3")

		// 跨命名空间同时操作
		_ = b.PutString("ns2", "target_key", "ns2_v1")
		_ = b.Delete("ns1", "stay_key")
		return nil
	})
	if err != nil {
		t.Fatalf("Batch failed: %v", err)
	}

	// 提交后验证最终原子结果
	v1, err := db.GetString("ns1", "target_key")
	if err != nil || v1 == nil || *v1 != "final_v3" {
		t.Fatalf("Expected final_v3, got %v, err %v", v1, err)
	}
	stayV, err := db.GetString("ns1", "stay_key")
	if err != nil || stayV != nil {
		t.Fatalf("Expected stay_key deleted, got %v, err %v", stayV, err)
	}
	v2, err := db.GetString("ns2", "target_key")
	if err != nil || v2 == nil || *v2 != "ns2_v1" {
		t.Fatalf("Expected ns2_v1, got %v, err %v", v2, err)
	}
}

// 2. hive_ce 范式：极端 Unicode 字符（带 ZWJ 复合 Emoji、多语言文本）、空 Key、空 Value、大 Blob (512KB)
func TestGoUnicodeComplexAndZeroByteKeys(t *testing.T) {
	tempDir := t.TempDir()
	dbPath := filepath.Join(tempDir, "unicode_niche.db")
	db, err := Open(dbPath)
	if err != nil {
		t.Fatalf("Open failed: %v", err)
	}
	defer db.Close()

	// 空字符串作为 Key
	if err := db.PutString("", "", "empty_key_val"); err != nil {
		t.Fatalf("PutString empty key failed: %v", err)
	}
	got, err := db.GetString("", "")
	if err != nil || got == nil || *got != "empty_key_val" {
		t.Fatalf("GetString empty key failed: got %v, err %v", got, err)
	}

	// 空字节作为 Value
	if err := db.PutBytes("", "empty_val_key", []byte{}); err != nil {
		t.Fatalf("PutBytes empty val failed: %v", err)
	}
	gotBytes, err := db.GetBytes("", "empty_val_key")
	if err != nil || gotBytes == nil || len(gotBytes) != 0 {
		t.Fatalf("GetBytes empty val failed: got %v, err %v", gotBytes, err)
	}

	// 复杂的各种语言文本与带零宽连字 (ZWJ) 复合 Emoji
	cases := []struct {
		k string
		v string
	}{
		{"zwj_family", "👨‍👨‍👧‍👦"},
		{"arabic", " ﻬ ﻭ ﻮ ﻯ ﻰ ﻱ"},
		{"malayalam", "അ ആ ഇ "},
		{"katakana", "ｩ ｪ ｫ ｬ ｭ ｮ ｯ ｰ "},
		{"special_math", "∀x ∈ ℝ: x² ≥ 0"},
		{"surrogate_pairs", "𠮷野家"},
	}

	for _, tc := range cases {
		if err := db.PutString("i18n", tc.k, tc.v); err != nil {
			t.Fatalf("PutString %s failed: %v", tc.k, err)
		}
		res, err := db.GetString("i18n", tc.k)
		if err != nil || res == nil || *res != tc.v {
			t.Fatalf("GetString %s mismatch: got %v, want %s", tc.k, res, tc.v)
		}
	}

	// 512KB 大 Blob
	largeBlob := bytes.Repeat([]byte{0xAB}, 512*1024)
	if err := db.PutBytes("blob_ns", "large_blob_key", largeBlob); err != nil {
		t.Fatalf("PutBytes large blob failed: %v", err)
	}
	blobRes, err := db.GetBytes("blob_ns", "large_blob_key")
	if err != nil || !bytes.Equal(blobRes, largeBlob) {
		t.Fatalf("GetBytes large blob mismatch")
	}
}

// 3. rocksdb 列族 / redb 表范式：命名空间完全隔离与 Drop 重建
func TestGoNamespaceIsolationAndDropRecreate(t *testing.T) {
	tempDir := t.TempDir()
	dbPath := filepath.Join(tempDir, "ns_isolation.db")
	db, err := Open(dbPath)
	if err != nil {
		t.Fatalf("Open failed: %v", err)
	}
	defer db.Close()

	// 同名 key 分别写入 3 个命名空间（包括默认命名空间）
	_ = db.PutString("", "shared_id", "default_val")
	_ = db.PutString("default", "shared_id", "default_val_alias") // 别名测试
	_ = db.PutString("ns_alpha", "shared_id", "alpha_val")
	_ = db.PutString("ns_beta", "shared_id", "beta_val")

	// 验证默认命名空间与 "" 完全互通
	vDef, _ := db.GetString("", "shared_id")
	vAlias, _ := db.GetString("default", "shared_id")
	if *vDef != "default_val_alias" || *vAlias != "default_val_alias" {
		t.Fatalf("Default namespace alias mismatch")
	}

	// 验证隔离
	vAlpha, _ := db.GetString("ns_alpha", "shared_id")
	vBeta, _ := db.GetString("ns_beta", "shared_id")
	if *vAlpha != "alpha_val" || *vBeta != "beta_val" {
		t.Fatalf("Namespace isolation failed")
	}

	// Drop 掉 ns_alpha
	if _, err := db.DropNamespace("ns_alpha"); err != nil {
		t.Fatalf("DropNamespace failed: %v", err)
	}
	vAlphaDropped, _ := db.GetString("ns_alpha", "shared_id")
	if vAlphaDropped != nil {
		t.Fatalf("Expected ns_alpha dropped, but found key")
	}

	// 其他命名空间完好
	vBetaStay, _ := db.GetString("ns_beta", "shared_id")
	if *vBetaStay != "beta_val" {
		t.Fatalf("ns_beta corrupted after ns_alpha drop")
	}

	// 重建 ns_alpha
	_ = db.PutString("ns_alpha", "shared_id", "recreated_alpha_val")
	vAlphaRe, _ := db.GetString("ns_alpha", "shared_id")
	if *vAlphaRe != "recreated_alpha_val" {
		t.Fatalf("ns_alpha recreation failed")
	}
}

// 4. rust-rocksdb 范式：范围扫描极限边界与 Seek 逻辑
func TestGoScanRangeAdvancedBoundaries(t *testing.T) {
	tempDir := t.TempDir()
	dbPath := filepath.Join(tempDir, "scan_bounds.db")
	db, err := Open(dbPath)
	if err != nil {
		t.Fatalf("Open failed: %v", err)
	}
	defer db.Close()

	keys := []string{"apple", "banana", "cherry", "date", "fig", "grape"}
	for _, k := range keys {
		_ = db.PutString("fruit", k, "ok")
	}

	// 1. start > end 空扫描
	fig := "fig"
	banana := "banana"
	emptyScan, err := db.ScanRange("fruit", &fig, &banana)
	if err != nil || len(emptyScan) != 0 {
		t.Fatalf("Expected empty scan when start > end")
	}

	// 2. start 和 end 完全一致
	cherry := "cherry"
	exactEmpty, err := db.ScanRange("fruit", &cherry, &cherry)
	if err != nil || len(exactEmpty) != 0 {
		t.Fatalf("Expected empty scan when start == end")
	}

	// 3. 不存在的 start 定位 (seek_ge)
	blueberry := "blueberry"
	date := "date"
	seekScan, err := db.ScanRange("fruit", &blueberry, &date)
	if err != nil || len(seekScan) != 1 || seekScan[0].Key != "cherry" {
		t.Fatalf("Seek ge failed: got %v", seekScan)
	}

	// 4. 超出范围的 end
	zebra := "zebra"
	allFromCherry, err := db.ScanRange("fruit", &cherry, &zebra)
	if err != nil || len(allFromCherry) != 4 {
		t.Fatalf("Scan to end of keyspace failed: got %d items", len(allFromCherry))
	}

	// 5. 空命名空间
	nonExistent, err := db.ScanRange("non_existent", nil, nil)
	if err != nil || len(nonExistent) != 0 {
		t.Fatalf("Expected empty slice for non-existent namespace")
	}
}

// 5. redb abort_tests 范式：事务批处理异常中断与暂存区丢弃
func TestGoBatchAbortStagingRollback(t *testing.T) {
	tempDir := t.TempDir()
	dbPath := filepath.Join(tempDir, "batch_abort.db")
	db, err := Open(dbPath)
	if err != nil {
		t.Fatalf("Open failed: %v", err)
	}
	defer db.Close()

	_ = db.PutString("state", "status", "clean_v1")

	// 在 Batch 中抛出错误，模拟事务中止
	err = db.Batch(func(b *BatchWriter) error {
		_ = b.PutString("state", "status", "dirty_v2")
		_ = b.PutString("state", "new_leak", "should_not_exist")
		_ = b.Delete("state", "status")
		return fmt.Errorf("simulated business transaction abort")
	})
	if err == nil {
		t.Fatalf("Expected batch error")
	}

	// 验证未产生脏数据
	v, err := db.GetString("state", "status")
	if err != nil || v == nil || *v != "clean_v1" {
		t.Fatalf("Batch abort leaked dirty data: got %v", v)
	}
	leak, err := db.GetString("state", "new_leak")
	if err != nil || leak != nil {
		t.Fatalf("Batch abort leaked new_leak key")
	}

	// 再次正常写入不受影响
	_ = db.PutString("state", "status", "clean_v2")
	v2, _ := db.GetString("state", "status")
	if *v2 != "clean_v2" {
		t.Fatalf("Subsequent write failed")
	}
}
