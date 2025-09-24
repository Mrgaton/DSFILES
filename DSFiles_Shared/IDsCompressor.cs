using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace DSFiles_Shared
{

    public class IDsCompressor
    {
        private BitReader _in;
        private BitWriter _out; 

        private const long PrecomputedTimespanDelta = 3500;
        public byte[] Compress(ulong[] xs)
        {
            var flakes = xs.Select(Snowflake.Decompose).ToArray();

            foreach (var sf in flakes)
            {
                Console.WriteLine("Time:" + sf.MillisecondsSinceDiscordEpoch + " Worker:" + sf.WorkerId + " Process:" + sf.ProcessId + " Increment:" + sf.Increment);
            }

            _out = new BitWriter();

            var ff = flakes[0];

            //MS EPOCH
            _out.WriteBits(ff.MillisecondsSinceDiscordEpoch, 40);

            ulong timtePrevValue = ff.MillisecondsSinceDiscordEpoch;
            long timePrevDelta = PrecomputedTimespanDelta;

            int workerPrevValue = ff.WorkerId;
            int workerPrevDelta = 0;

            int processPrevValue = ff.ProcessId;
            int processPrevDelta = 0;

            int incrementPrevValue = ff.Increment;
            int incrementPrevDelta = 0;

            for (int i = 1; i < flakes.Length; i++)
            {
                var f = flakes[i];

                var time = f.MillisecondsSinceDiscordEpoch;
                var worker = f.WorkerId;
                var process = f.ProcessId;
                var increment = f.Increment;

                long timeDelta = (long)(time - timtePrevValue);
                long timeDod = timeDelta - timePrevDelta;
                WriteTimeDod(timeDod);
                timtePrevValue = time;
                timePrevDelta = timeDelta;

                int workerDelta = (worker - workerPrevValue);
                long workerDod = workerDelta- workerPrevDelta;
                WriteSmallDod(workerDod);
                workerPrevValue = worker;
                workerPrevDelta = workerDelta;


                int processDelta = (process - processPrevValue);
                long processDod = processDelta- processPrevDelta;
                WriteSmallDod(processDod);
                processPrevValue = process;
                processPrevDelta = processDelta;

                int incrementDelta = (increment - incrementPrevValue);
                long incrementDod = incrementDelta - incrementPrevDelta;
                WriteIncrementDod(incrementDod);
                incrementPrevValue = increment;
                incrementPrevDelta = incrementDelta;
            }

            _out.Flush();
            
            return _out.ToArray();
        }


        private static int GetBits(long v)
        {
            ulong uv = ((ulong)(v << 1)) ^ (ulong)(v >> 63);

            for(int i = 0; i < 64;i++)
            {
                if (uv < (1UL << i))
                {
                    return i;
                }
            }

            return 0;
        }
        private void WriteTimeDod(long v)
        {
            ulong uv = ((ulong)(v << 1)) ^ (ulong)(v >> 63);

            Console.Write($"[WriteDod] dod={v} uv=0x{uv:X}");

            if (uv < (1UL << 8))
            {
                Console.Write(" Bits:" + (2 + 8) + " Size:" + GetBits(v));
                _out.WriteBits(0b00, 2);
                _out.WriteBits(uv, 8);
            }
            else if (uv < (1UL << 10))
            {
                Console.Write(" Bits:" + (2 + 10) + " Size:" + GetBits(v));
                _out.WriteBits(0b10, 2);
                _out.WriteBits(uv, 10);
            }
            else if (uv < (1UL << 14))
            {
                Console.Write(" Bits:" + (2 + 14) + " Size:" + GetBits(v));
                _out.WriteBits(0b01, 2);
                _out.WriteBits(uv, 14);
            }
            else
            {
                if (uv > (1UL << 40))
                {
                    throw new InvalidOperationException("Dod time encoder exceded maximun of 40 bits :c");
                }

                Console.Write(" Bits:" + (2 + 40) + " Size:" + GetBits(v));
                _out.WriteBits(0b11, 2);
                _out.WriteBits(uv, 40);
            }

            Console.Write("\n");
        }
        private void WriteSmallDod(long v)
        {
            ulong uv = ((ulong)(v << 1)) ^ (ulong)(v >> 63);

            Console.Write($"[WriteDod] dod={v} uv=0x{uv:X}");

            if (uv < (1UL << 0))
            {
                Console.Write(" Bits:" + (2) + " Size:" + GetBits(v));
                _out.WriteBits(0b00, 2);
            }
            else if (uv < (1UL << 2))
            {
                Console.Write(" Bits:" + (2 + 2) + " Size:" + GetBits(v));
                _out.WriteBits(0b10, 2);
                _out.WriteBits(uv, 2);
            }
            else if (uv < (1UL << 3))
            {
                Console.Write(" Bits:" + (2 + 3) + " Size:" + GetBits(v));
                _out.WriteBits(0b01, 2);
                _out.WriteBits(uv, 3);
            }
            else
            {
                if (uv > (1UL << 5))
                {
                    throw new InvalidOperationException("Dod small exceded maximun of 40 bits :c");
                }

                Console.Write(" Bits:" + (2 + 5) + " Size:" + GetBits(v));
                _out.WriteBits(0b11, 2);
                _out.WriteBits(uv, 5);
            }

            Console.Write("\n");
        }


        private void WriteIncrementDod(long v)
        {
            ulong uv = ((ulong)(v << 1)) ^ (ulong)(v >> 63);

            Console.Write($"[WriteDod] dod={v} uv=0x{uv:X}");

            if (uv < (1UL << 6))
            {
                Console.Write(" Bits:" + (2 + 6) + " Size:" + GetBits(v));
                _out.WriteBits(0b00, 2);
                _out.WriteBits(uv, 6);
            }
            else if (uv < (1UL << 8))
            {
                Console.Write(" Bits:" + (2 + 8) + " Size:" + GetBits(v));
                _out.WriteBits(0b10, 2);
                _out.WriteBits(uv, 8);
            }
            else if (uv < (1UL << 10))
            {
                Console.Write(" Bits:" + (2 + 10) + " Size:" + GetBits(v));
                _out.WriteBits(0b01, 2);
                _out.WriteBits(uv, 10);
            }
            else
            {
                if (uv > (1UL << 12))
                {
                    throw new InvalidOperationException("Dod small exceded maximun of 40 bits :c");
                }

                Console.Write(" Bits:" + (2 + 12) + " Size:" + GetBits(v));
                _out.WriteBits(0b11, 2);
                _out.WriteBits(uv, 12);
            }

            Console.Write("\n");
        }
        
        public class BitWriter
        {
            private readonly List<byte> _bytes = new();
            private int _bitPos = 0;
            private byte _cur = 0;

            public void WriteBit(int b)
            {
                if (b != 0) _cur |= (byte)(1 << (7 - _bitPos));
                _bitPos++;
                if (_bitPos == 8) FlushByte();
            }

            public void WriteBits(ulong v, int count)
            {
                for (int i = count - 1; i >= 0; i--)
                    WriteBit((int)((v >> i) & 1));
            }

            private void FlushByte()
            {
                _bytes.Add(_cur);
                _cur = 0;
                _bitPos = 0;
            }

            public void Flush()
            {
                if (_bitPos > 0) FlushByte();
            }

            public byte[] ToArray()
                => _bytes.ToArray();
        }

        private long ReadDod()
        {
            int first = _in.ReadBit();
            int second = _in.ReadBit();
            int payloadBits = 0;

            if (first == 0 && second == 0)
            {
                payloadBits = 28;
            }
            else if (first == 1 && second == 0)
            {
                payloadBits = 30;
            }
            else if (first == 0 && second == 1)
            {
                payloadBits = 34;
            }
            else if (first == 1 && second == 1)
            {
                payloadBits = 40;
            }

            ulong uv = _in.ReadBits(payloadBits);
            long v = (long)((uv >> 1) ^ (ulong)-(long)(uv & 1));
            return v;
        }
        /*public ulong[] Decompress(byte[] compressed)
        {
            var results = new List<ulong>();
            _in = new BitReader(compressed);

            _prevValue = _in.ReadBits(62);
            results.Add(_prevValue);

            _prevDelta = PrecomputedTimespanDelta;

            while (_in.BitsLeft >= 30)
            {
                long dod = ReadDod();

                long delta = _prevDelta + dod;
                ulong value = _prevValue + (ulong)delta;

                results.Add(value);

                _prevValue = value;
                _prevDelta = delta;
            }

            return results.ToArray();
        }*/

        public class BitReader
        {
            private readonly byte[] _bytes;
            private int _bytePos = 0;
            private int _bitPos = 0;

            public BitReader(byte[] bytes)
            {
                _bytes = bytes;
            }
            public long BitsLeft
            {
                get
                {
                    long bitsRead = _bytePos * 8L + _bitPos;
                    long totalBits = (long)_bytes.Length * 8L;
                    return totalBits - bitsRead;
                }
            }
            public bool HasMoreBits
            {
                get
                {
                    long bitsRead = _bytePos * 8L + _bitPos;
                    long totalBits = (long)_bytes.Length * 8L;
                    return bitsRead < totalBits;
                }
            }

            public int ReadBit()
            {
                if (_bytePos >= _bytes.Length)
                    throw new InvalidOperationException("No more data available to read bits.");

                int bit = (_bytes[_bytePos] >> (7 - _bitPos)) & 1;
                _bitPos++;
                if (_bitPos == 8)
                {
                    _bitPos = 0;
                    _bytePos++;
                }
                return bit;
            }

            public ulong ReadBits(int count)
            {
                if (count < 0 || count > 64)
                    throw new ArgumentOutOfRangeException(nameof(count));

                ulong v = 0;
                for (int i = 0; i < count; i++)
                {
                    v = (v << 1) | (ulong)ReadBit();
                }
                return v;
            }
        }
    }
}
