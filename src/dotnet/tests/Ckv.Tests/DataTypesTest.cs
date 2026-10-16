using System;
using System.IO;
using Xunit;

namespace Ckv.Tests
{
    public class DataTypesTest
    {
        [Fact]
        public void TestCommonDataTypesRoundtrip()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ckv_types_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string dbPath = Path.Combine(tempDir, "types.db");
                using (var db = CkvDb.Open(dbPath))
                {
                    // 1. int64 Boundary tests
                    db.PutInt64("", "i64:zero", 0);
                    db.PutInt64("", "i64:pos", 1234567890123456789L);
                    db.PutInt64("", "i64:neg", -987654321098765432L);
                    db.PutInt64("", "i64:min", long.MinValue);
                    db.PutInt64("", "i64:max", long.MaxValue);

                    Assert.Equal(0L, db.GetInt64("", "i64:zero"));
                    Assert.Equal(1234567890123456789L, db.GetInt64("", "i64:pos"));
                    Assert.Equal(-987654321098765432L, db.GetInt64("", "i64:neg"));
                    Assert.Equal(long.MinValue, db.GetInt64("", "i64:min"));
                    Assert.Equal(long.MaxValue, db.GetInt64("", "i64:max"));

                    // 2. float64 Floating point and canonical NaN
                    db.PutFloat64("", "f64:zero", 0.0);
                    db.PutFloat64("", "f64:neg_zero", -0.0);
                    db.PutFloat64("", "f64:pi", 3.141592653589793);
                    db.PutFloat64("", "f64:inf", double.PositiveInfinity);
                    db.PutFloat64("", "f64:neg_inf", double.NegativeInfinity);
                    db.PutFloat64("", "f64:nan", double.NaN);

                    Assert.Equal(0.0, db.GetFloat64("", "f64:zero"));
                    double negZero = db.GetFloat64("", "f64:neg_zero")!.Value;
                    Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(negZero));

                    Assert.Equal(3.141592653589793, db.GetFloat64("", "f64:pi"));
                    Assert.Equal(double.PositiveInfinity, db.GetFloat64("", "f64:inf"));
                    Assert.Equal(double.NegativeInfinity, db.GetFloat64("", "f64:neg_inf"));

                    double nanVal = db.GetFloat64("", "f64:nan")!.Value;
                    Assert.True(double.IsNaN(nanVal));
                    ulong nanBits = (ulong)BitConverter.DoubleToInt64Bits(nanVal);
                    // Canonical quiet NaN bits
                    Assert.Equal(0x7FF8000000000000UL, nanBits);

                    // 3. bool
                    db.PutBool("", "bool:t", true);
                    db.PutBool("", "bool:f", false);

                    Assert.True(db.GetBool("", "bool:t"));
                    Assert.False(db.GetBool("", "bool:f"));

                    // 4. Batch mixed types
                    db.Batch(b =>
                    {
                        b.PutInt64("metrics", "count", 42);
                        b.PutFloat64("metrics", "ratio", 0.99);
                        b.PutBool("metrics", "active", true);
                    });

                    Assert.Equal(42L, db.GetInt64("metrics", "count"));
                    Assert.Equal(0.99, db.GetFloat64("metrics", "ratio"));
                    Assert.True(db.GetBool("metrics", "active"));

                    // 5. Entry typed accessor verification
                    var entries = db.ScanRange("metrics", null, null);
                    Assert.Equal(3, entries.Count);
                    foreach (var e in entries)
                    {
                        switch (e.Key)
                        {
                            case "count":
                                Assert.True(e.IsInt64);
                                Assert.Equal(42L, e.AsInt64());
                                break;
                            case "ratio":
                                Assert.True(e.IsFloat64);
                                Assert.Equal(0.99, e.AsFloat64());
                                break;
                            case "active":
                                Assert.True(e.IsBool);
                                Assert.True(e.AsBool());
                                break;
                            default:
                                Assert.Fail("Unexpected key " + e.Key);
                                break;
                        }
                    }

                    // 6. Type mismatch error checks
                    Assert.Throws<TypeMismatchError>(() => db.GetInt64("metrics", "ratio"));
                    Assert.Throws<TypeMismatchError>(() => db.GetBool("metrics", "count"));
                    Assert.Throws<TypeMismatchError>(() => db.GetFloat64("metrics", "active"));
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
