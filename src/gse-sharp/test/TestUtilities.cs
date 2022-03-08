using System;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace gs.sharp.test
{
    [TestClass]
    public class TestUtilities
    {
        [TestMethod]
        public void TestSetThenGet()
        {
            var now = DateTimeOffset.UtcNow;
            var object1 = new Object1(1, now, new Loc1(1, 2, 3), new Rot1(4, 5, 6), new Loc1(7, 8, 9));
            DateTimeMs(now, object1.Timestamp, 1);
        }

        [TestMethod]
        public void TestTimeExtensions()
        {
            var now = DateTimeOffset.UtcNow;
            var sw = new Stopwatch();
            sw.Start();
            var object1 = new Object1(1, now, new Loc1(1, 2, 3), new Rot1(4, 5, 6), new Loc1(7, 8, 9));
            var encoder = new Encoder(1500);
            encoder.Encode(object1);
            var decoder = new Decoder(encoder.GetDataLength(), encoder.DataBuffer);
            (object decoded, Type type)? decoded = decoder.Decode();
            sw.Stop();
            DateTimeMs(now, ((Object1)decoded.Value.decoded).Timestamp, sw.ElapsedMilliseconds + 1);
        }

        private void DateTimeMs(DateTimeOffset expected, DateTimeOffset actual, long allowedDispartyMs)
        {
            Console.WriteLine($"Expected: {expected}");
            Console.WriteLine($"Actual: {actual}");
            Console.WriteLine($"Allowed (ms): {allowedDispartyMs}");
            var diffMs = (actual - expected).TotalMilliseconds;
            Console.WriteLine($"Diff (ms): {diffMs}");
            Assert.IsTrue(diffMs >= 0);
            Assert.IsTrue(diffMs <= allowedDispartyMs);
        }
    }
}
